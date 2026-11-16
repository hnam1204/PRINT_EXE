using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using RPM.Core.Cloud;
using RPM.Core.Connection;
using RPM.Core.Config;
using RPM.Core.Models;
using RPM.Core.Protocol;
using RPM.Core.Security;
using RPM.Networking;
using RPM.Printing;
using RPM.Core.Printing;

namespace RPM.App.Services;

public class ServerService : IDisposable, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly RelayClient _relayClient = new();
    private readonly PrinterService _printerService = new();
    private readonly CloudConnectionService _cloud;
    private readonly CancellationTokenSource _cloudWorkerStop = new();
    private readonly HashSet<string> _cloudJobsInProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cloudPairingIds = new(StringComparer.Ordinal);
    private readonly CloudJobStateStore _cloudJobStateStore = new();
    private readonly WindowsLocationService _locationService = new();
    private readonly SemaphoreSlim _cloudPairingRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _cloudGeofenceLock = new(1, 1);
    private DateTime _lastApprovedClientStatusRefreshUtc = DateTime.MinValue;
    private int _cloudWorkersStarted;
    private int _cloudGeofenceConfigured;
    private Task? _cloudWorkerTask;
    private Task? _cloudPairingWorkerTask;
    private int _isDisposed;
    private readonly Queue<PrintJob> _printQueue = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly HashSet<string> _recentlyPrintedHashes = new();
    private readonly Dictionary<string, DirectStreamState> _directStreams = new(StringComparer.Ordinal);
    private readonly object _directStreamLock = new();
    private const int MaxQueuedJobs = 100;
    private static readonly TimeSpan DirectStreamIdleTimeout = TimeSpan.FromMinutes(15);
    private RelayServer? _embeddedRelay;
    private int _isProcessingQueue;
    private Task? _directStreamCleanupTask;
    private readonly object _startupGate = new();
    private Task? _startupTask;

    public RelayClient Relay => _relayClient;
    public PrinterService PrinterService => _printerService;

    public ObservableCollection<DeviceInfo> ConnectedClients { get; } = new();
    public ObservableCollection<PrintJob> PrintJobs { get; } = new();
    public ObservableCollection<SharedPrinterInfo> SharedPrinters { get; } = new();
    public SharedPrinterInfo? ActiveSharedPrinter { get; private set; }
    public bool IsCloudOnline => _cloud.IsOnline;
    public ActivationStatus ActivationStatus => _cloud.ActivationStatus;
    public bool HasStoredCloudToken => _cloud.HasStoredToken;
    public CloudActivationInfo? CloudActivationInfo => _cloud.ActivationInfo;
    public bool IsOnline => _cloud.IsOnline || _relayClient.IsConnected;
    public bool IsInternalConnectionReady => _embeddedRelay?.IsLanAvailable == true || _relayClient.IsConnected;
    public ConnectionStatusSnapshot ConnectionStatusInfo => ConnectionStatusResolver.Resolve(
        _cloud.ActivationStatus,
        _cloud.IsOnline,
        IsInternalConnectionReady);
    public ConnectionMode ConnectionMode => ConnectionStatusInfo.ConnectionMode;
    public TransportStatus TransportStatus => ConnectionStatusInfo.TransportStatus;
    public string ConnectionStatus => ConnectionStatusInfo.PrimaryText;
    public string ConnectionStatusSecondaryDisplay => ConnectionStatusInfo.SecondaryText ?? string.Empty;
    public string ActivationStatusDisplay => ConnectionStatusResolver.GetActivationText(_cloud.ActivationStatus);
    public bool IsLanRelayAvailable => _embeddedRelay?.IsLanAvailable == true;
    private bool RequiresBackendPrintAuthorization => _cloud.IsCustomerActivation;
    public string LanAccessStatus { get; private set; } = "Chưa kiểm tra quyền LAN";
    public int CloudApprovedClientCount { get; private set; }
    public int CloudOnlineClientCount { get; private set; }
    public int CloudMaxClientCount { get; private set; } = 15;
    public bool CloudCapacityLoaded { get; private set; }
    public string GeofenceStatusDisplay { get; private set; } = "Đang tắt";
    public IReadOnlyList<string> LanRelayEndpoints => GetLanRelayEndpoints();

    public event EventHandler<string>? OnLog;
    public event EventHandler<DeviceInfo>? OnClientRequestedApproval;
    public event EventHandler? OnStatusChanged;
    public event EventHandler? OnEnrollmentRevoked;

    public ServerService(AppSettings settings)
    {
        _settings = settings;
        _cloud = new CloudConnectionService(settings);
        _cloud.OnLog += (s, msg) => OnLog?.Invoke(this, msg);
        _cloud.OnEnrollmentRevoked += (s, e) => OnEnrollmentRevoked?.Invoke(this, EventArgs.Empty);
        _cloud.OnRealtimeEventAsync += Cloud_OnRealtimeEventAsync;
        _cloud.OnStateChanged += (s, e) =>
        {
            if (_settings.CloudTransportEnabled && _cloud.IsOnline)
            {
                _ = EnsureCloudWorkersAsync();
                _ = EnsureCloudGeofenceAsync();
            }
            else
            {
                CloudCapacityLoaded = false;
                CloudOnlineClientCount = 0;
            }
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        };

        // Restore known approved clients
        foreach (var client in _settings.ApprovedClients)
        {
            client.IsOnline = false;
            ConnectedClients.Add(client);
        }

        // Restore recent jobs
        foreach (var job in _settings.PrintHistory.Take(50))
        {
            PrintJobs.Add(job);
        }

        _relayClient.OnConnected += Relay_OnConnected;
        _relayClient.OnDisconnected += Relay_OnDisconnected;
        _relayClient.OnPacketReceived += Relay_OnPacketReceived;
        _relayClient.OnLog += (s, msg) => OnLog?.Invoke(this, msg);
    }

    private async Task Cloud_OnRealtimeEventAsync(CloudRealtimeEvent realtimeEvent)
    {
        if (realtimeEvent.EventType.Equals("PAIRING_REQUESTED", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshPendingPairingsAsync();
            return;
        }

        if (realtimeEvent.EventType.Equals("PRINT_JOB_CREATED", StringComparison.OrdinalIgnoreCase))
            await ProcessCloudJobNotificationAsync(realtimeEvent.JobId);
    }

    private async Task ProcessCloudJobNotificationAsync(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || _cloud.Client is null || !_cloud.IsOnline) return;
        try
        {
            // The client uploads the ciphertext immediately after creating the
            // durable job. Give that upload a small head start, then reconcile.
            await Task.Delay(200, _cloudWorkerStop.Token);
            var job = await _cloud.Client.GetJobAsync(jobId, _cloudWorkerStop.Token);
            if (job is not null && string.Equals(job.Status, "QUEUED", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(job.DataRef))
            {
                if (_cloudJobsInProgress.Add(job.JobId)) _ = ProcessCloudJobAsync(job);
            }
        }
        catch (OperationCanceledException) when (_cloudWorkerStop.IsCancellationRequested) { }
        catch (Exception ex) { OnLog?.Invoke(this, $"[Realtime] Không xử lý ngay Job {jobId}: {ex.Message}"); }
    }

    public Task StartAsync(string startupReason = "normal")
    {
        lock (_startupGate)
        {
            _startupTask ??= StartCoreAsync(NormalizeStartupReason(startupReason));
            return _startupTask;
        }
    }

    private async Task StartCoreAsync(string startupReason)
    {
        OnLog?.Invoke(this, $"[Startup] Host khởi động với lý do: {startupReason}; mã ghép nối nội bộ sẽ được tạo mới một lần.");
        string previousCloudSessionId = RotateLocalPairingCode();

        _relayClient.PairingCode = _settings.PairingCode;

        if (string.IsNullOrWhiteSpace(_settings.EncryptionSecret))
        {
            _settings.EncryptionSecret = Guid.NewGuid().ToString("N");
            ConfigManager.Save(_settings);
        }

        // Load and detect all installed printers and shared configuration
        LoadPrinters();

        bool cloudReady = false;
        if (_settings.CloudTransportEnabled || _cloud.HasStoredToken)
        {
            await _cloud.StartAsync();
            if (_cloud.IsOnline)
            {
                cloudReady = true;
                await StartCloudWorkersIfNeededAsync(previousCloudSessionId);
                await EnsureCloudGeofenceAsync();
            }
        }

        // Keep the embedded Relay available even when Cloud is online. It is the
        // LAN fallback and must listen on the same port for other devices.
        if (_settings.Role == AppRole.Server &&
            Uri.TryCreate(_settings.RelayServerUrl, UriKind.Absolute, out var relayUri) &&
            relayUri.Scheme == "ws")
        {
            try
            {
                int port = 9090;
                if (relayUri.Port > 0)
                {
                    port = relayUri.Port;
                }
                _embeddedRelay = new RelayServer();
                _embeddedRelay.Start(port);
                LanAccessStatus = _embeddedRelay.LanBindingStatus;
                await RefreshLanAccessStatusAsync();
                OnLog?.Invoke(this, $"[Relay] Đã tự động kích hoạt máy chủ trung chuyển nội bộ trên cổng {port}.");
            }
            catch (Exception ex)
            {
                LanAccessStatus = $"Không thể mở quyền LAN cho cổng 9090: {ex.Message}";
                OnLog?.Invoke(this, $"[Relay] Khởi động trung chuyển nội bộ: {ex.Message}");
            }
        }

        await _relayClient.StartAsync(_settings.RelayServerUrl);
        _directStreamCleanupTask = Task.Run(DirectStreamCleanupLoopAsync);

        if (cloudReady)
        {
            OnLog?.Invoke(this, "[Cloud] Host worker đã khởi động; kết nối nội bộ LAN vẫn sẵn sàng dự phòng.");
        }
    }

    public static string NormalizeStartupReason(string? startupReason) => startupReason?.Trim().ToLowerInvariant() switch
    {
        "updated" => "updated",
        "rolled-back" => "rolled-back",
        _ => "normal"
    };

    public async Task<bool> RefreshLanAccessStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_embeddedRelay is null)
        {
            LanAccessStatus = "Relay LAN chưa được khởi động";
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (!_embeddedRelay.IsLanAvailable)
        {
            LanAccessStatus = _embeddedRelay.LanBindingStatus;
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        bool reachable = await _embeddedRelay.CheckLocalLanAccessAsync(cancellationToken);
        LanAccessStatus = reachable
            ? $"Đã mở LAN và kiểm tra được cổng {_embeddedRelay.Port} trên Host; Client cần kết nối thử để xác minh từ xa"
            : $"Đã bind LAN nhưng chưa kiểm tra được cổng {_embeddedRelay.Port} trên Host";
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
        return reachable;
    }

    private IReadOnlyList<string> GetLanRelayEndpoints()
    {
        int port = 9090;
        if (Uri.TryCreate(_settings.RelayServerUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
        {
            port = uri.Port;
        }

        return LanRelayDiscovery.GetPrivateIpv4Addresses()
            .Distinct()
            .Select(address => $"ws://{address}:{port}")
            .ToList();
    }

    public async Task<bool> RotatePairingCodeAsync(CancellationToken cancellationToken = default)
    {
        bool restartRelay = _relayClient.IsConnected;
        if (restartRelay)
        {
            await _relayClient.StopAsync();
        }

        string previousCloudSessionId = RotateLocalPairingCode();

        if (restartRelay)
        {
            await _relayClient.StartAsync(_settings.RelayServerUrl);
        }

        if (_settings.CloudTransportEnabled && _cloud.IsOnline && _cloud.Client is not null)
        {
            try
            {
                await IssueCloudPairingCodeAsync(previousCloudSessionId, cancellationToken);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(this, $"[Cloud] Đã đổi mã Relay, nhưng chưa đổi được mã Cloud: {ex.Message}");
            }
        }

        OnStatusChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private string RotateLocalPairingCode()
    {
        string previousCloudSessionId = _settings.CloudPairingSessionId;
        _settings.PairingCode = Core.Security.RPMCrypto.GeneratePairingCode(_settings.PairingCode);
        _settings.CloudPairingCode = string.Empty;
        _settings.CloudPairingExpiresAt = null;
        _settings.CloudPairingSessionId = string.Empty;
        ConfigManager.Save(_settings);
        _relayClient.PairingCode = _settings.PairingCode;
        return previousCloudSessionId;
    }

    public async Task<bool> EnrollCloudAsync(string enrollmentSecret, CancellationToken cancellationToken = default)
    {
        bool enrolled = await _cloud.EnrollAsync(enrollmentSecret, cancellationToken);
        if (!enrolled) return false;
        _settings.CloudTransportEnabled = true;
        ConfigManager.Save(_settings);
        await _cloud.StartAsync(cancellationToken);
        await StartCloudWorkersIfNeededAsync();
        Interlocked.Exchange(ref _cloudGeofenceConfigured, 0);
        await EnsureCloudGeofenceAsync(cancellationToken);
        return true;
    }

    private async Task EnsureCloudGeofenceAsync(CancellationToken cancellationToken = default)
    {
        await _cloudGeofenceLock.WaitAsync(cancellationToken);
        try
        {
            if (_cloudGeofenceConfigured != 0 || _cloud.Client is null || !_cloud.IsOnline)
                return;

            bool unlimited = string.Equals(_settings.CloudActivationGeofencePolicy, "UNLIMITED", StringComparison.OrdinalIgnoreCase);
            int configuredRadius = _settings.CloudActivationGeofenceRadiusMeters ?? 0;
            if (!unlimited && configuredRadius <= 0)
                return;

            if (unlimited)
            {
                var updatedUnlimited = await _cloud.Client.UpdateGeofenceAsync(new CloudGeofenceSettings(
                    _settings.CloudHostId,
                    false,
                    null,
                    null,
                    null,
                    "UNLIMITED"), cancellationToken);
                Interlocked.Exchange(ref _cloudGeofenceConfigured, 1);
                GeofenceStatusDisplay = updatedUnlimited?.Policy == "UNLIMITED" ? "Không giới hạn khoảng cách" : "Chính sách phạm vi chưa đồng bộ";
                OnStatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var location = await _locationService.TryGetCurrentAsync(cancellationToken);
            if (location is null)
            {
                GeofenceStatusDisplay = "Không có quyền truy cập vị trí";
                OnStatusChanged?.Invoke(this, EventArgs.Empty);
                OnLog?.Invoke(this, "[Cloud] Chưa thể bật phạm vi vì Windows Location Services chưa được cấp quyền.");
                return;
            }

            var updated = await _cloud.Client.UpdateGeofenceAsync(new CloudGeofenceSettings(
                _settings.CloudHostId,
                true,
                location.Latitude,
                location.Longitude,
                configuredRadius,
                "LIMITED"), cancellationToken);
            Interlocked.Exchange(ref _cloudGeofenceConfigured, 1);
            GeofenceStatusDisplay = updated?.Enabled == true ? $"Đang bật: {updated.RadiusMeters:0} m" : "Đang tắt";
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _cloudGeofenceLock.Release();
        }
    }

    private async Task EnsureCloudWorkersAsync()
    {
        try { await StartCloudWorkersIfNeededAsync(); }
        catch (Exception ex) { OnLog?.Invoke(this, $"[Cloud] Không thể khởi động lại Worker: {ex.Message}"); }
    }

    private async Task StartCloudWorkersIfNeededAsync(string? previousCloudSessionId = null)
    {
        if (Interlocked.Exchange(ref _cloudWorkersStarted, 1) != 0) return;
        try
        {
            await RegisterCloudPrintersAsync();
            await IssueCloudPairingCodeAsync(previousCloudSessionId);
            _cloudWorkerTask = Task.Run(CloudWorkerLoopAsync);
            _cloudPairingWorkerTask = Task.Run(CloudPairingLoopAsync);
        }
        catch
        {
            Interlocked.Exchange(ref _cloudWorkersStarted, 0);
            throw;
        }
    }

    private async Task IssueCloudPairingCodeAsync(string? previousCloudSessionId = null, CancellationToken cancellationToken = default)
    {
        if (_cloud.Client is null) return;
        string sessionToRevoke = previousCloudSessionId ?? _settings.CloudPairingSessionId;
        if (!string.IsNullOrWhiteSpace(sessionToRevoke))
        {
            try
            {
                await _cloud.Client.RevokePairingCodeAsync(sessionToRevoke, cancellationToken);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(this, $"[Cloud] Không thể thu hồi mã cũ, tiếp tục tạo mã mới: {ex.Message}");
            }
        }

        string sessionId = $"{_settings.DeviceId}:{Guid.NewGuid():N}";
        var code = await _cloud.Client.IssuePairingCodeAsync(sessionId, cancellationToken);
        if (code?.Code is not null)
        {
            _settings.CloudPairingCode = code.Code;
            _settings.CloudPairingExpiresAt = code.ExpiresAt;
            _settings.CloudPairingSessionId = code.SessionId;
            ConfigManager.Save(_settings);
            OnLog?.Invoke(this, $"Mã kết nối: {code.Code}");
        }
    }

    private async Task RegisterCloudPrintersAsync()
    {
        if (_cloud.Client is null) return;
        foreach (var printer in SharedPrinters)
        {
            try
            {
                await _cloud.Client.RegisterPrinterAsync(new CloudPrinterRegistration(
                    CloudPrinterId(printer.Name), printer.Name, printer.Status, printer.IsShared,
                    new Dictionary<string, object?>
                    {
                        ["driverName"] = printer.DriverName,
                        ["portName"] = printer.PortName,
                        ["supportsColor"] = printer.SupportsColor,
                        ["supportsDuplex"] = printer.SupportsDuplex
                    }));
            }
            catch (Exception ex) { OnLog?.Invoke(this, $"[Cloud] Không đồng bộ máy in '{printer.Name}': {ex.Message}"); }
        }
    }

    private static string CloudPrinterId(string printerName)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(printerName));
        return "printer-" + Convert.ToHexString(hash[..12]).ToLowerInvariant();
    }

    private async Task CloudPairingLoopAsync()
    {
        while (!_cloudWorkerStop.IsCancellationRequested)
        {
            try
            {
                await RefreshPendingPairingsAsync(_cloudWorkerStop.Token);
                await Task.Delay(TimeSpan.FromSeconds(2), _cloudWorkerStop.Token);
            }
            catch (OperationCanceledException) when (_cloudWorkerStop.IsCancellationRequested) { break; }
            catch (Exception ex) { OnLog?.Invoke(this, $"Không thể kiểm tra yêu cầu kết nối: {ex.Message}"); await Task.Delay(2000); }
        }
    }

    public async Task RefreshPendingPairingsAsync(CancellationToken cancellationToken = default)
    {
        if (_cloud.Client is null || !_cloud.IsOnline) return;

        await _cloudPairingRefreshLock.WaitAsync(cancellationToken);
        try
        {
            var client = _cloud.Client;
            var pending = await client.ListPendingPairingsAsync(cancellationToken);
            var summary = await client.GetPairingSummaryAsync(cancellationToken);
            var pendingItems = pending ?? Array.Empty<CloudPendingPairing>();
            var pendingClientIds = pendingItems.Select(item => item.ClientId).ToHashSet(StringComparer.Ordinal);
            if (summary is not null)
            {
                CloudApprovedClientCount = summary.ApprovedCount;
                CloudOnlineClientCount = summary.OnlineCount;
                CloudMaxClientCount = summary.MaxClients;
                CloudCapacityLoaded = true;
            }

            await RefreshApprovedClientStatusesAsync(client, cancellationToken);

            var geofence = await client.GetGeofenceAsync(cancellationToken);
            if (geofence is not null)
            {
                GeofenceStatusDisplay = string.Equals(geofence.Policy, "UNLIMITED", StringComparison.OrdinalIgnoreCase)
                    ? "Không giới hạn khoảng cách"
                    : geofence.Enabled ? $"Đang bật: {geofence.RadiusMeters:0} m" : "Đang tắt";
            }

            foreach (var item in pendingItems)
            {
                bool isNewPairing = !_cloudPairingIds.TryGetValue(item.ClientId, out var existingPairingId)
                    || existingPairingId != item.PairingId;
                _cloudPairingIds[item.ClientId] = item.PairingId;
                var device = ConnectedClients.FirstOrDefault(x => x.DeviceId == item.ClientId);
                if (device is null)
                {
                    device = new DeviceInfo
                    {
                        DeviceId = item.ClientId,
                        DeviceName = item.ClientName,
                        IsOnline = false
                    };
                    Application.Current?.Dispatcher.Invoke(() => ConnectedClients.Add(device));
                    SaveClientList();
                }

                device.PairingId = item.PairingId;

                if (isNewPairing)
                {
                    // A new request is authoritative: allow a previously rejected device to be approved again.
                    device.IsApproved = false;
                    device.IsBlocked = false;
                    device.ReconnectAllowed = false;
                    device.DeviceName = item.ClientName;
                    device.IsOnline = false;
                    SaveClientList();
                }

                if (isNewPairing) OnClientRequestedApproval?.Invoke(this, device);
            }

            // Do not keep stale pairing IDs for devices that are no longer pending.
            foreach (var clientId in _cloudPairingIds.Keys.ToList())
            {
                var device = ConnectedClients.FirstOrDefault(x => x.DeviceId == clientId);
                if (!pendingClientIds.Contains(clientId) && device is not null && !device.IsApproved)
                {
                    _cloudPairingIds.Remove(clientId, out _);
                }
            }

            var expiredPending = ConnectedClients
                .Where(device => !device.IsApproved && !device.IsBlocked && !device.ReconnectAllowed)
                .Where(device => !pendingClientIds.Contains(device.DeviceId))
                .ToList();
            if (expiredPending.Count > 0)
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    foreach (var device in expiredPending)
                        ConnectedClients.Remove(device);
                });
                if (Application.Current is null)
                {
                    foreach (var device in expiredPending)
                        ConnectedClients.Remove(device);
                }
                SaveClientList();
            }

            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _cloudPairingRefreshLock.Release();
        }
    }

    private async Task RefreshApprovedClientStatusesAsync(CloudApiClient client, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastApprovedClientStatusRefreshUtc < TimeSpan.FromSeconds(5)) return;
        _lastApprovedClientStatusRefreshUtc = DateTime.UtcNow;

        var approvedClients = ConnectedClients
            .Where(item => item.IsApproved && !item.IsBlocked)
            .Take(15)
            .ToList();
        if (approvedClients.Count == 0) return;

        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(120);
        var updates = await Task.WhenAll(approvedClients.Select(async item =>
        {
            try
            {
                return (item, profile: await client.GetDeviceProfileAsync(item.DeviceId, cancellationToken));
            }
            catch
            {
                return (item, profile: null);
            }
        }));

        void ApplyUpdates()
        {
            foreach (var update in updates)
            {
                if (update.profile is null)
                {
                    update.item.IsOnline = false;
                    continue;
                }

                update.item.DeviceName = update.profile.Name;
                update.item.LastSeen = NormalizeUtc(update.profile.LastSeenAt);
                update.item.IsOnline = update.item.LastSeen >= cutoff;
            }
        }

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.Invoke(ApplyUpdates);
        else
            ApplyUpdates();
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public void LoadPrinters()
    {
        ApplyPrinterScan(ScanPrinters());
    }

    public List<SharedPrinterInfo> ScanPrinters()
    {
        var installed = _printerService.GetInstalledPrinters();
        foreach (var p in installed)
        {
            p.IsShared = _settings.SharedPrinterNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase);
            p.IsDefault = !string.IsNullOrWhiteSpace(_settings.SelectedPrinterName)
                && string.Equals(p.Name, _settings.SelectedPrinterName, StringComparison.OrdinalIgnoreCase);
        }
        return installed;
    }

    public void ApplyPrinterScan(IEnumerable<SharedPrinterInfo> printers)
    {
        ActiveSharedPrinter = null;
        SharedPrinters.Clear();
        foreach (var printer in printers)
            SharedPrinters.Add(printer);
        ActiveSharedPrinter = SharedPrinters.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(_settings.SelectedPrinterName) &&
            string.Equals(p.Name, _settings.SelectedPrinterName, StringComparison.OrdinalIgnoreCase));
        foreach (var item in SharedPrinters)
            item.IsDefault = ActiveSharedPrinter != null && string.Equals(item.Name, ActiveSharedPrinter.Name, StringComparison.OrdinalIgnoreCase);
    }

    public void TogglePrinterSharing(string printerName, bool? forceState = null)
    {
        var printer = SharedPrinters.FirstOrDefault(p => p.Name == printerName);
        if (printer == null)
        {
            var installed = _printerService.GetInstalledPrinters();
            printer = installed.FirstOrDefault(p => p.Name == printerName);
            if (printer != null) SharedPrinters.Add(printer);
        }

        if (printer != null)
        {
            printer.IsShared = forceState ?? !printer.IsShared;
            if (printer.IsShared)
            {
                if (!_settings.SharedPrinterNames.Contains(printer.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.SharedPrinterNames.Add(printer.Name);
                }
                if (ActiveSharedPrinter == null)
                {
                    ActiveSharedPrinter = printer;
                    _settings.SelectedPrinterName = printer.Name;
                }
                OnLog?.Invoke(this, $"[Máy In] Đã BẬT chia sẻ máy in: {printer.Name}");
            }
            else
            {
                _settings.SharedPrinterNames.RemoveAll(n => string.Equals(n, printer.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var item in SharedPrinters)
                {
                    item.IsDefault = ActiveSharedPrinter != null && string.Equals(item.Name, ActiveSharedPrinter.Name, StringComparison.OrdinalIgnoreCase);
                }
                OnLog?.Invoke(this, $"[Máy In] Đã TẮT chia sẻ máy in: {printer.Name}");
            }
            ConfigManager.Save(_settings);
            BroadcastPrinterStatus();
            _ = RegisterCloudPrintersAsync();
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetDefaultPrinter(string printerName)
    {
        var target = SharedPrinters.FirstOrDefault(p => p.Name == printerName);
        if (target != null)
        {
            ActiveSharedPrinter = target;
            _settings.SelectedPrinterName = printerName;
            foreach (var item in SharedPrinters)
            {
                item.IsDefault = string.Equals(item.Name, printerName, StringComparison.OrdinalIgnoreCase);
            }
            ConfigManager.Save(_settings);

            BroadcastPrinterStatus();
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
            OnLog?.Invoke(this, $"[Máy In] Đã chọn máy in mặc định chính: {printerName}");
        }
    }

    public void SetSharedPrinter(string printerName) => SetDefaultPrinter(printerName);

    public void PauseSharing()
    {
        if (ActiveSharedPrinter != null)
        {
            TogglePrinterSharing(ActiveSharedPrinter.Name, false);
        }
    }

    public void ResumeSharing()
    {
        if (ActiveSharedPrinter != null)
        {
            TogglePrinterSharing(ActiveSharedPrinter.Name, true);
        }
    }

    public List<SharedPrinterInfo> GetPrintersForClient(DeviceInfo client)
    {
        return PrinterAccessPolicy.Filter(
            SharedPrinters,
            client.PrinterPermissionMode,
            client.AllowedPrinters).ToList();
    }

    public async Task ApproveClientAsync(DeviceInfo client)
    {
        bool previousApproved = client.IsApproved;
        bool previousBlocked = client.IsBlocked;
        bool previousReconnectAllowed = client.ReconnectAllowed;
        var permittedPrinters = GetPrintersForClient(client);
        var accessMode = client.EffectivePrinterPermissionMode;
        try
        {
            if (_settings.CloudTransportEnabled && _cloud.Client is not null && _cloudPairingIds.TryGetValue(client.DeviceId, out var cloudPairingId))
            {
                var result = await _cloud.Client.DecidePairingAsync(
                    cloudPairingId,
                    true,
                    permittedPrinters.Select(p => CloudPrinterId(p.Name)).ToList(),
                    PrinterAccessPolicy.ToApiValue(accessMode));
                if (result is null || !string.Equals(result.Status, "APPROVED", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Máy chủ từ xa chưa xác nhận phê duyệt thiết bị.");
                OnLog?.Invoke(this, $"[Cloud] Đã duyệt thiết bị: {client.DeviceName}");
            }
            else
            {
                var response = new ClientJoinResponsePayload
                {
                    Approved = true,
                    IsPending = false,
                    Message = "Máy chủ đã chấp thuận thiết bị của bạn.",
                    EncryptionSecret = _settings.EncryptionSecret,
                    SharedPrinter = permittedPrinters.FirstOrDefault(),
                    AvailablePrinters = permittedPrinters,
                    PrinterAccessMode = PrinterAccessPolicy.ToApiValue(accessMode)
                };

                bool sent = await _relayClient.SendEncryptedPayloadAsync(
                    MessageType.ClientJoinResponse,
                    _settings.DeviceId,
                    client.DeviceId,
                    _settings.PairingCode,
                    response,
                    _settings.PairingCode);
                if (!sent) throw new InvalidOperationException("Không gửi được phản hồi phê duyệt tới máy con.");
                OnLog?.Invoke(this, $"[Duyệt] Đã chấp thuận máy con: {client.DeviceName} (Phòng: {client.Department}) - Quyền: {client.AllowedPrintersSummary}");
            }

            client.IsApproved = true;
            client.IsBlocked = false;
            client.ReconnectAllowed = false;
            SaveClientList();
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            client.IsApproved = previousApproved;
            client.IsBlocked = previousBlocked;
            client.ReconnectAllowed = previousReconnectAllowed;
            SaveClientList();
            throw;
        }
    }

    public async Task RevokeClientAsync(DeviceInfo client)
    {
        bool previousApproved = client.IsApproved;
        bool previousBlocked = client.IsBlocked;
        bool previousReconnectAllowed = client.ReconnectAllowed;
        try
        {
            if (_settings.CloudTransportEnabled && _cloud.Client is not null && _cloudPairingIds.TryGetValue(client.DeviceId, out var cloudPairingId))
            {
                var result = await _cloud.Client.DecidePairingAsync(cloudPairingId, false, Array.Empty<string>());
                if (result is null || !string.Equals(result.Status, "REJECTED", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Máy chủ từ xa chưa xác nhận từ chối thiết bị.");
                OnLog?.Invoke(this, $"[Cloud] Đã từ chối thiết bị: {client.DeviceName}");
            }
            else
            {
                var packet = new NetworkPacket
                {
                    Type = MessageType.DeviceRevoked,
                    SenderId = _settings.DeviceId,
                    TargetId = client.DeviceId,
                    PairingCode = _settings.PairingCode,
                    PayloadJson = "{\"status\":\"revoked\"}"
                };

                bool sent = await _relayClient.SendPacketAsync(packet);
                if (!sent) throw new InvalidOperationException("Không gửi được phản hồi từ chối tới máy con.");
                OnLog?.Invoke(this, $"[Thu hồi] Đã thu hồi quyền máy con: {client.DeviceName}");
            }

            client.IsApproved = false;
            client.IsBlocked = true;
            client.ReconnectAllowed = false;
            _cloudPairingIds.Remove(client.DeviceId);
            SaveClientList();
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            client.IsApproved = previousApproved;
            client.IsBlocked = previousBlocked;
            client.ReconnectAllowed = previousReconnectAllowed;
            SaveClientList();
            throw;
        }
    }

    public bool AllowReconnectClient(DeviceInfo client)
    {
        if (!client.IsBlocked) return false;

        // This only opens a new request path. It never approves the client or
        // consumes a capacity slot; the next pairing must still be approved.
        client.IsBlocked = false;
        client.IsApproved = false;
        client.ReconnectAllowed = true;
        client.IsOnline = false;
        _cloudPairingIds.Remove(client.DeviceId);
        SaveClientList();
        OnLog?.Invoke(this, $"[Kết nối lại] Đã cho phép '{client.DeviceName}' gửi yêu cầu kết nối mới.");
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SetClientDepartment(string deviceId, string department)
    {
        var client = ConnectedClients.FirstOrDefault(c => c.DeviceId == deviceId);
        if (client != null)
        {
            client.Department = department;
            SaveClientList();
            OnLog?.Invoke(this, $"[Phòng ban] Cập nhật '{client.DeviceName}' sang phòng: {department}");
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SetClientAllowedPrintersAsync(string deviceId, List<string> allowedPrinters, PrinterAccessMode accessMode)
    {
        var client = ConnectedClients.FirstOrDefault(c => c.DeviceId == deviceId);
        if (client == null) return;

        var normalizedPrinters = allowedPrinters
            .Where(name => SharedPrinters.Any(printer => printer.IsShared && string.Equals(printer.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accessMode == PrinterAccessMode.AllShared || accessMode == PrinterAccessMode.None)
            normalizedPrinters.Clear();

        if (client.IsApproved && _settings.CloudTransportEnabled && _cloud.IsOnline && _cloud.Client is not null)
        {
            string pairingId = await ResolveCloudPairingIdAsync(client);
            if (string.IsNullOrWhiteSpace(pairingId))
                throw new InvalidOperationException("Không xác định được Pairing đã phê duyệt để cập nhật quyền máy in.");

            var result = await _cloud.Client.UpdatePairingPrinterPermissionsAsync(
                pairingId,
                PrinterAccessPolicy.ToApiValue(accessMode),
                normalizedPrinters.Select(CloudPrinterId).ToList());
            if (result is null || !string.Equals(result.Status, "APPROVED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Máy chủ từ xa không xác nhận quyền máy in mới.");
        }

        client.AllowedPrinters = normalizedPrinters;
        client.PrinterPermissionMode = accessMode;
        SaveClientList();
        OnLog?.Invoke(this, $"[Phân quyền] Cập nhật máy in cho '{client.DeviceName}': {client.AllowedPrintersSummary}");
        if (client.IsApproved)
        {
            await SendPrinterListToClientAsync(client);
        }
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> ResolveCloudPairingIdAsync(DeviceInfo client, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(client.PairingId)) return client.PairingId;
        if (_cloudPairingIds.TryGetValue(client.DeviceId, out var mappedPairingId) && !string.IsNullOrWhiteSpace(mappedPairingId))
        {
            client.PairingId = mappedPairingId;
            return mappedPairingId;
        }
        if (_cloud.Client is null || !_cloud.IsOnline) return string.Empty;

        var pairing = await _cloud.Client.GetApprovedPairingForClientAsync(client.DeviceId, cancellationToken);
        if (pairing is null || !string.Equals(pairing.Status, "APPROVED", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        client.PairingId = pairing.PairingId;
        _cloudPairingIds[client.DeviceId] = pairing.PairingId;
        SaveClientList();
        return pairing.PairingId;
    }

    public void SetClientAllowedPrinters(string deviceId, List<string> allowedPrinters)
    {
        var client = ConnectedClients.FirstOrDefault(c => c.DeviceId == deviceId);
        if (client == null) return;
        var mode = allowedPrinters.Count == 0 ? PrinterAccessMode.AllShared : PrinterAccessMode.SelectedOnly;
        _ = SetClientAllowedPrintersAsync(deviceId, allowedPrinters, mode);
    }

    private async Task SendPrinterListToClientAsync(DeviceInfo client)
    {
        if (!_relayClient.IsConnected || string.IsNullOrWhiteSpace(client.DeviceId)) return;
        var payload = new PrintersListUpdatePayload
        {
            Printers = GetPrintersForClient(client),
            PrinterAccessMode = PrinterAccessPolicy.ToApiValue(client.EffectivePrinterPermissionMode)
        };
        await _relayClient.SendPacketAsync(new NetworkPacket
        {
            Type = MessageType.PrintersListUpdate,
            SenderId = _settings.DeviceId,
            TargetId = client.DeviceId,
            PairingCode = _settings.PairingCode,
            PayloadJson = JsonSerializer.Serialize(payload)
        });
    }

    private async Task SendPrinterListsToApprovedClientsAsync()
    {
        foreach (var client in ConnectedClients.Where(item => item.IsApproved && !item.IsBlocked).ToList())
        {
            try { await SendPrinterListToClientAsync(client); }
            catch (Exception ex) { OnLog?.Invoke(this, $"[Lỗi phân quyền máy in] {client.DeviceName}: {ex.Message}"); }
        }
    }

    public void DeleteClient(string deviceId)
    {
        var client = ConnectedClients.FirstOrDefault(c => c.DeviceId == deviceId);
        if (client != null)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ConnectedClients.Remove(client);
            });
            SaveClientList();
            OnLog?.Invoke(this, $"[Xóa thiết bị] Đã xóa '{client.DeviceName}' khỏi danh sách.");
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void AddPreapprovedClient(string deviceName, string department)
    {
        var client = new DeviceInfo
        {
            DeviceId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Máy con mới" : deviceName,
            Department = string.IsNullOrWhiteSpace(department) ? "Văn phòng" : department,
            IsApproved = true,
            IsOnline = false,
            FirstConnected = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow
        };
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ConnectedClients.Add(client);
        });
        SaveClientList();
        OnLog?.Invoke(this, $"[Thêm thiết bị] Đã đăng ký trước máy con: '{client.DeviceName}' (Phòng: {client.Department})");
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveClientList()
    {
        _settings.ApprovedClients = ConnectedClients.ToList();
        ConfigManager.Save(_settings);
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void BroadcastPrinterStatus()
    {
        try
        {
            _ = SendPrinterListsToApprovedClientsAsync();
            _ = RegisterCloudPrintersAsync();
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"[Lỗi cập nhật máy in] {ex.Message}");
        }
    }

    private async void Relay_OnConnected(object? sender, EventArgs e)
    {
        OnLog?.Invoke(this, "Đã kết nối Relay Broker. Đang đăng ký mã Pairing Code...");

        // Register Server room
        var reg = new RegisterServerPayload
        {
            ServerDeviceId = _settings.DeviceId,
            ServerDeviceName = _settings.DeviceName,
            PairingCode = _settings.PairingCode,
            SharedPrinter = ActiveSharedPrinter?.IsShared == true ? ActiveSharedPrinter : null,
            SharedPrinters = SharedPrinters.Where(p => p.IsShared).ToList()
        };

        var packet = new NetworkPacket
        {
            Type = MessageType.RegisterServer,
            SenderId = _settings.DeviceId,
            PairingCode = _settings.PairingCode,
            PayloadJson = JsonSerializer.Serialize(reg)
        };

        await _relayClient.SendPacketAsync(packet);
        await SendPrinterListsToApprovedClientsAsync();
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Relay_OnDisconnected(object? sender, string reason)
    {
        foreach (var client in ConnectedClients)
        {
            client.IsOnline = false;
        }
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void Relay_OnPacketReceived(object? sender, NetworkPacket packet)
    {
        try
        {
            switch (packet.Type)
            {
                case MessageType.RegisterServerAck:
                    OnLog?.Invoke(this, $"[Máy Chủ] Sẵn sàng nhận lệnh in với mã kết nối: {_settings.PairingCode}");
                    break;

                case MessageType.ClientJoin:
                    HandleClientJoin(packet);
                    break;

                case MessageType.SubmitPrintJob:
                    await HandleSubmitPrintJobAsync(packet);
                    break;

                case MessageType.PrintStreamStart:
                    await HandlePrintStreamStartAsync(packet);
                    break;

                case MessageType.PrintStreamChunk:
                    await HandlePrintStreamChunkAsync(packet);
                    break;

                case MessageType.PrintStreamEnd:
                    await HandlePrintStreamEndAsync(packet);
                    break;

                case MessageType.Heartbeat:
                    // Reply pong
                    await _relayClient.SendPacketAsync(new NetworkPacket
                    {
                        Type = MessageType.HeartbeatAck,
                        SenderId = _settings.DeviceId,
                        TargetId = packet.SenderId,
                        PairingCode = _settings.PairingCode
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"[Lỗi xử lý gói tin] {ex.Message}");
        }
    }

    private async Task HandlePrintStreamStartAsync(NetworkPacket packet)
    {
        var sender = ConnectedClients.FirstOrDefault(client => client.DeviceId == packet.SenderId);
        if (sender is null || !sender.IsApproved || sender.IsBlocked)
        {
            await SendStreamAckAsync(packet.SenderId, string.Empty, -1, "REJECTED", "Thiết bị chưa được cấp quyền in.");
            return;
        }

        var payload = _relayClient.DecryptPayload<PrintStreamStartPayload>(packet, _settings.EncryptionSecret);
        if (payload is null || string.IsNullOrWhiteSpace(payload.JobId) || payload.JobInfo is null ||
            payload.TotalBytes <= 0 || payload.TotalBytes > PacketSecurity.MaxDirectPrintJobBytes ||
            !string.Equals(payload.JobId, payload.JobInfo.JobId, StringComparison.Ordinal))
        {
            await SendStreamAckAsync(packet.SenderId, payload?.JobId ?? string.Empty, -1, "REJECTED", "Thông tin luồng in không hợp lệ.");
            return;
        }

        if (!DirectPrintAuthorization.TryResolveTargetPrinter(
                sender,
                SharedPrinters,
                payload.JobInfo.TargetPrinter,
                out var targetPrinter,
                out var authorizationError))
        {
            await SendStreamAckAsync(packet.SenderId, payload.JobId, -1, "REJECTED", authorizationError);
            return;
        }

        if (!await ConsumeBackendPrintAuthorizationAsync(payload.AuthorizationId, payload.AuthorizationToken, payload.JobId, sender.DeviceId, payload.AuthorizationPrinterId, targetPrinter))
        {
            await SendStreamAckAsync(packet.SenderId, payload.JobId, -1, "REJECTED", "Lệnh in chưa được máy chủ từ xa cấp quyền hoặc quyền đã hết hạn.");
            return;
        }

        if (string.IsNullOrWhiteSpace(payload.ContentSha256) || payload.ContentSha256.Length != 64 ||
            !payload.ContentSha256.All(Uri.IsHexDigit))
        {
            await SendStreamAckAsync(packet.SenderId, payload.JobId, -1, "REJECTED", "Checksum tài liệu không hợp lệ.");
            return;
        }

        await _queueLock.WaitAsync();
        bool queueAvailable;
        try { queueAvailable = _printQueue.Count < MaxQueuedJobs; }
        finally { _queueLock.Release(); }
        if (!queueAvailable)
        {
            await SendStreamAckAsync(packet.SenderId, payload.JobId, -1, "REJECTED", "Hàng đợi in đang đầy.");
            return;
        }

        lock (_directStreamLock)
        {
            if (_directStreams.ContainsKey(payload.JobId) || PrintJobs.Any(job => job.JobId == payload.JobId))
            {
                _ = SendStreamAckAsync(packet.SenderId, payload.JobId, -1, "REJECTED", "Job đã tồn tại, không tạo bản in thứ hai.");
                return;
            }
        }

        string extension = payload.FileExtension?.ToLowerInvariant() switch
        {
            ".pdf" => ".pdf", ".ps" => ".ps", ".txt" => ".txt", ".prn" => ".prn", _ => ".bin"
        };
        string tempFile = Path.Combine(ConfigManager.GetTempJobsFolder(), $"direct-{Guid.NewGuid():N}{extension}");
        try
        {
            var state = new DirectStreamState(
                payload.JobInfo,
                packet.SenderId,
                payload.TotalBytes,
                payload.ContentSha256,
                new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true),
                tempFile);
            state.Job.SenderDeviceId = sender.DeviceId;
            state.Job.SenderDeviceName = sender.DeviceName;
            state.Job.Department = sender.Department;
            state.Job.TargetPrinter = targetPrinter;
            state.Job.Status = PrintJobStatus.Pending;
            state.Job.StatusMessage = "Đang nhận tài liệu trực tiếp...";

            lock (_directStreamLock)
            {
                if (_directStreams.ContainsKey(payload.JobId) || PrintJobs.Any(job => job.JobId == payload.JobId))
                {
                    state.File.Dispose();
                    File.Delete(tempFile);
                    _ = SendStreamAckAsync(packet.SenderId, payload.JobId, -1, "REJECTED", "Job đã tồn tại, không tạo bản in thứ hai.");
                    return;
                }
                _directStreams[payload.JobId] = state;
            }
            await SendStreamAckAsync(packet.SenderId, payload.JobId, -1, "RECEIVED", "Host đã sẵn sàng nhận tài liệu.");
        }
        catch (Exception ex)
        {
            try { File.Delete(tempFile); } catch { }
            await SendStreamAckAsync(packet.SenderId, payload.JobId, -1, "REJECTED", $"Không thể mở bộ đệm truyền: {ex.Message}");
        }
    }

    private async Task<bool> ConsumeBackendPrintAuthorizationAsync(
        string authorizationId,
        string authorizationToken,
        string jobId,
        string clientId,
        string authorizationPrinterId,
        string targetPrinter)
    {
        bool grantProvided = !string.IsNullOrWhiteSpace(authorizationId) || !string.IsNullOrWhiteSpace(authorizationToken);
        if (!RequiresBackendPrintAuthorization && !grantProvided)
            return true;
        if (string.IsNullOrWhiteSpace(authorizationId) || string.IsNullOrWhiteSpace(authorizationToken) ||
            _cloud.Client is null || !_cloud.IsOnline)
            return false;
        try
        {
            var result = await _cloud.Client.ConsumePrintAuthorizationAsync(authorizationId, authorizationToken);
            return result is { Authorized: true } &&
                string.Equals(result.JobId, jobId, StringComparison.Ordinal) &&
                string.Equals(result.ClientId, clientId, StringComparison.Ordinal) &&
                string.Equals(result.PrinterId, authorizationPrinterId, StringComparison.Ordinal) &&
                string.Equals(result.PrinterId, CloudPrinterId(targetPrinter), StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"[Cloud] Không xác minh được quyền lệnh in: {ex.Message}");
            return false;
        }
    }

    private async Task HandlePrintStreamChunkAsync(NetworkPacket packet)
    {
        var payload = _relayClient.DecryptPayload<PrintStreamChunkPayload>(packet, _settings.EncryptionSecret);
        if (payload is null || string.IsNullOrWhiteSpace(payload.JobId)) return;

        DirectStreamState? state;
        lock (_directStreamLock) _directStreams.TryGetValue(payload.JobId, out state);
        if (state is null)
        {
            await SendStreamAckAsync(packet.SenderId, payload.JobId, payload.Sequence, "REJECTED", "Phiên truyền không còn tồn tại.");
            return;
        }
        if (!string.Equals(state.SenderDeviceId, packet.SenderId, StringComparison.Ordinal)) return;

        string? failure = null;
        await state.Gate.WaitAsync();
        try
        {
            if (payload.Sequence != state.NextSequence || payload.Offset != state.BytesReceived)
                throw new InvalidDataException("Thứ tự hoặc offset của chunk không hợp lệ.");

            byte[] data = Convert.FromBase64String(payload.DataBase64);
            if (data.Length == 0 || data.Length > PacketSecurity.DirectPrintChunkBytes || state.BytesReceived + data.LongLength > state.TotalBytes)
                throw new InvalidDataException("Kích thước chunk không hợp lệ.");

            await state.File.WriteAsync(data);
            state.BytesReceived += data.LongLength;
            state.NextSequence++;
            state.LastActivityUtc = DateTime.UtcNow;
            await SendStreamAckAsync(packet.SenderId, payload.JobId, payload.Sequence, "RECEIVED", string.Empty);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            state.Gate.Release();
        }

        if (failure is not null)
            await RejectDirectStreamAsync(payload.JobId, packet.SenderId, failure);
    }

    private async Task HandlePrintStreamEndAsync(NetworkPacket packet)
    {
        var payload = _relayClient.DecryptPayload<PrintStreamEndPayload>(packet, _settings.EncryptionSecret);
        if (payload is null || string.IsNullOrWhiteSpace(payload.JobId)) return;

        DirectStreamState? state;
        lock (_directStreamLock) _directStreams.TryGetValue(payload.JobId, out state);
        if (state is null)
        {
            await SendStreamAckAsync(packet.SenderId, payload.JobId, -2, "REJECTED", "Phiên truyền không còn tồn tại.");
            return;
        }
        if (!string.Equals(state.SenderDeviceId, packet.SenderId, StringComparison.Ordinal)) return;

        string? failure = null;
        await state.Gate.WaitAsync();
        try
        {
            if (state.BytesReceived != state.TotalBytes || payload.TotalBytes != state.TotalBytes ||
                !string.Equals(payload.ContentSha256, state.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Kích thước hoặc checksum Job không khớp.");

            await state.File.FlushAsync();
            await state.File.DisposeAsync();
            string hash = await ComputeFileSha256Async(state.TempFilePath);
            if (!string.Equals(hash, state.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checksum dữ liệu nhận không khớp.");

            state.Job.ContentSha256 = hash;
            state.Job.FileSizeBytes = state.BytesReceived;
            state.Job.LocalTempFilePath = state.TempFilePath;
            await EnqueueDirectJobAsync(state.Job);
            lock (_directStreamLock) _directStreams.Remove(payload.JobId);
            await SendStreamAckAsync(packet.SenderId, payload.JobId, -2, "RECEIVED", "Host đã nhận đủ tài liệu.");
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            state.Gate.Release();
        }

        if (failure is not null)
            await RejectDirectStreamAsync(payload.JobId, packet.SenderId, failure);
    }

    private async Task EnqueueDirectJobAsync(PrintJob job)
    {
        var sender = ConnectedClients.FirstOrDefault(client => client.DeviceId == job.SenderDeviceId);
        if (sender is null || !sender.IsApproved || sender.IsBlocked)
            throw new InvalidOperationException("Thiết bị gửi không còn quyền in.");

        if (!DirectPrintAuthorization.TryResolveTargetPrinter(
                sender,
                SharedPrinters,
                job.TargetPrinter,
                out var targetPrinter,
                out var authorizationError))
            throw new InvalidOperationException(authorizationError);

        job.TargetPrinter = targetPrinter;
        await _queueLock.WaitAsync();
        try
        {
            if (_printQueue.Count >= MaxQueuedJobs) throw new InvalidOperationException("Hàng đợi in đang đầy.");
            _printQueue.Enqueue(job);
        }
        finally { _queueLock.Release(); }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            PrintJobs.Insert(0, job);
            _settings.PrintHistory.Insert(0, job);
            if (_settings.PrintHistory.Count > 200) _settings.PrintHistory.RemoveAt(_settings.PrintHistory.Count - 1);
            ConfigManager.Save(_settings);
        });
        _ = ProcessPrintQueueAsync();
    }

    private async Task DirectStreamCleanupLoopAsync()
    {
        while (!_cloudWorkerStop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), _cloudWorkerStop.Token);
                var cutoff = DateTime.UtcNow - DirectStreamIdleTimeout;
                List<DirectStreamState> abandoned;
                lock (_directStreamLock)
                {
                    abandoned = _directStreams.Values
                        .Where(state => state.LastActivityUtc < cutoff)
                        .ToList();
                    foreach (var state in abandoned)
                        _directStreams.Remove(state.Job.JobId);
                }

                foreach (var state in abandoned)
                {
                    await DisposeDirectStreamFileAsync(state);
                    OnLog?.Invoke(this, $"[Direct] Đã dọn phiên truyền bỏ dở Job {state.Job.JobId}.");
                }
            }
            catch (OperationCanceledException) when (_cloudWorkerStop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(this, $"[Direct] Không thể dọn phiên truyền bỏ dở: {ex.Message}");
            }
        }
    }

    private async Task RejectDirectStreamAsync(string jobId, string senderId, string message)
    {
        DirectStreamState? state;
        lock (_directStreamLock)
        {
            _directStreams.Remove(jobId, out state);
        }
        if (state is not null)
            await DisposeDirectStreamFileAsync(state);
        await SendStreamAckAsync(senderId, jobId, -2, "REJECTED", message);
    }

    private static async Task DisposeDirectStreamFileAsync(DirectStreamState state)
    {
        await state.Gate.WaitAsync();
        try
        {
            try { await state.File.DisposeAsync(); } catch { }
            try { File.Delete(state.TempFilePath); } catch { }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private Task<bool> SendStreamAckAsync(string targetId, string jobId, long sequence, string status, string message) =>
        _relayClient.SendEncryptedPayloadAsync(
            MessageType.PrintStreamAck,
            _settings.DeviceId,
            targetId,
            _settings.PairingCode,
            new PrintStreamAckPayload { JobId = jobId, Sequence = sequence, Status = status, Message = message },
            _settings.EncryptionSecret);

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class DirectStreamState
    {
        public DirectStreamState(PrintJob job, string senderDeviceId, long totalBytes, string contentSha256, FileStream file, string tempFilePath)
        {
            Job = job;
            SenderDeviceId = senderDeviceId;
            TotalBytes = totalBytes;
            ContentSha256 = contentSha256;
            File = file;
            TempFilePath = tempFilePath;
        }

        public PrintJob Job { get; }
        public string SenderDeviceId { get; }
        public long TotalBytes { get; }
        public string ContentSha256 { get; }
        public FileStream File { get; }
        public string TempFilePath { get; }
        public long BytesReceived { get; set; }
        public long NextSequence { get; set; }
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private void HandleClientJoin(NetworkPacket packet)
    {
        var joinPayload = JsonSerializer.Deserialize<ClientJoinPayload>(packet.PayloadJson);
        if (joinPayload == null) return;

        var client = joinPayload.ClientDevice;
        if (string.IsNullOrWhiteSpace(client.DeviceId))
        {
            client.DeviceId = packet.SenderId;
        }

        var existing = ConnectedClients.FirstOrDefault(c => c.DeviceId == client.DeviceId);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (existing == null)
            {
                client.IsOnline = true;
                client.LastSeen = DateTime.UtcNow;
                ConnectedClients.Add(client);
                existing = client;
            }
            else
            {
                existing.DeviceName = client.DeviceName;
                existing.IpAddress = client.IpAddress;
                existing.OperatingSystem = client.OperatingSystem;
                existing.LastSeen = DateTime.UtcNow;
                existing.IsOnline = true;
                if (existing.IsBlocked)
                {
                    // A fresh join request is allowed to start a new approval cycle.
                    existing.IsApproved = false;
                    existing.IsBlocked = false;
                    existing.ReconnectAllowed = false;
                }
            }

            // Persist and notify on the UI thread so the bound device list
            // refreshes for both new requests and reconnecting clients.
            SaveClientList();
        });

        if (Application.Current == null)
        {
            SaveClientList();
        }

        if (existing != null && existing.IsApproved && !existing.IsBlocked)
        {
            // Auto approve recognized client
            _ = ApproveClientAsync(existing);
        }
        else if (existing != null && !existing.IsBlocked)
        {
            OnLog?.Invoke(this, $"[Yêu cầu kết nối] Máy con '{existing.DeviceName}' xin kết nối.");
            OnClientRequestedApproval?.Invoke(this, existing);
        }
    }

    private async Task HandleSubmitPrintJobAsync(NetworkPacket packet)
    {
        // Find sender
        var senderDevice = ConnectedClients.FirstOrDefault(c => c.DeviceId == packet.SenderId);
        if (senderDevice == null || !senderDevice.IsApproved || senderDevice.IsBlocked)
        {
            OnLog?.Invoke(this, $"[Cảnh báo] Từ chối lệnh in từ thiết bị chưa được cấp quyền: {packet.SenderId}");
            return;
        }

        PrintJobPayload? payload = null;
        if (!string.IsNullOrEmpty(packet.EncryptedPayloadBase64))
        {
            payload = _relayClient.DecryptPayload<PrintJobPayload>(packet, _settings.EncryptionSecret);
        }
        else if (!string.IsNullOrEmpty(packet.PayloadJson))
        {
            payload = JsonSerializer.Deserialize<PrintJobPayload>(packet.PayloadJson);
        }

        if (payload == null || payload.JobInfo == null)
        {
            OnLog?.Invoke(this, "[Lỗi] Dữ liệu tài liệu in không hợp lệ.");
            return;
        }

        if (string.IsNullOrWhiteSpace(payload.DataBase64) ||
            payload.DataBase64.Length > ((PacketSecurity.MaxPrintJobBytes + 2) / 3) * 4 + 4)
        {
            OnLog?.Invoke(this, "[Từ chối] Tài liệu in vượt quá giới hạn kích thước.");
            return;
        }

        var job = payload.JobInfo;
        job.SenderDeviceId = senderDevice.DeviceId;
        job.SenderDeviceName = senderDevice.DeviceName;
        job.Department = senderDevice.Department;

        // Resolve target printer
        string targetPrinterName = !string.IsNullOrWhiteSpace(job.TargetPrinter)
            ? job.TargetPrinter
            : (ActiveSharedPrinter?.Name ?? string.Empty);
        job.TargetPrinter = targetPrinterName;

        if (!await ConsumeBackendPrintAuthorizationAsync(payload.AuthorizationId, payload.AuthorizationToken, job.JobId, senderDevice.DeviceId, payload.AuthorizationPrinterId, targetPrinterName))
        {
            OnLog?.Invoke(this, "[Từ chối] Lệnh in chưa được máy chủ từ xa cấp quyền hoặc quyền đã hết hạn.");
            return;
        }

        // Granular access control check per device
        if (!PrinterAccessPolicy.Allows(senderDevice.PrinterPermissionMode, senderDevice.AllowedPrinters, targetPrinterName))
        {
            OnLog?.Invoke(this, $"[Từ chối] Máy con '{senderDevice.DeviceName}' ({senderDevice.Department}) không có quyền in trên '{targetPrinterName}'.");
            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = $"Thiết bị của bạn không có quyền in trên máy in '{targetPrinterName}'.";
            await NotifyJobProgressAsync(job);
            return;
        }

        // Anti-duplicate protection: check content hash within last 10 minutes
        if (!string.IsNullOrEmpty(job.ContentSha256) && _recentlyPrintedHashes.Contains(job.ContentSha256))
        {
            OnLog?.Invoke(this, $"[Chống in trùng] Đã phát hiện tài liệu trùng lặp gần đây ({job.DocumentName}). Bỏ qua.");
            return;
        }

        byte[] rawBytes;
        try
        {
            rawBytes = Convert.FromBase64String(payload.DataBase64);
        }
        catch (FormatException)
        {
            OnLog?.Invoke(this, "[Từ chối] Dữ liệu tài liệu in không hợp lệ.");
            return;
        }

        if (rawBytes.Length == 0 || rawBytes.Length > PacketSecurity.MaxPrintJobBytes)
        {
            OnLog?.Invoke(this, "[Từ chối] Tài liệu in vượt quá giới hạn kích thước.");
            return;
        }

        await _queueLock.WaitAsync();
        try
        {
            if (_printQueue.Count >= MaxQueuedJobs)
            {
                OnLog?.Invoke(this, "[Từ chối] Hàng đợi in đang đầy.");
                return;
            }

            // Never derive a filesystem path from a client-controlled document name.
            string safeExtension = payload.FileExtension?.ToLowerInvariant() switch
            {
                ".pdf" => ".pdf",
                ".ps" => ".ps",
                ".txt" => ".txt",
                ".prn" => ".prn",
                _ => ".bin"
            };
            string tempFile = Path.Combine(ConfigManager.GetTempJobsFolder(), $"{Guid.NewGuid():N}{safeExtension}");
            await File.WriteAllBytesAsync(tempFile, rawBytes);
            job.LocalTempFilePath = tempFile;
            _printQueue.Enqueue(job);
        }
        finally
        {
            _queueLock.Release();
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            PrintJobs.Insert(0, job);
            _settings.PrintHistory.Insert(0, job);
            if (_settings.PrintHistory.Count > 200)
            {
                _settings.PrintHistory.RemoveAt(_settings.PrintHistory.Count - 1);
            }
            ConfigManager.Save(_settings);
        });

        int queueCount;
        await _queueLock.WaitAsync();
        try { queueCount = _printQueue.Count; }
        finally { _queueLock.Release(); }

        // Alert if queue load exceeds high capacity
        if (queueCount >= 20)
        {
            OnLog?.Invoke(this, $"[Cảnh báo tải cao] Đang có {queueCount} lệnh in trong hàng đợi. Hệ thống đang tự động điều phối theo thứ tự FIFO.");
        }

        _ = ProcessPrintQueueAsync();
    }

    private async Task ProcessPrintQueueAsync()
    {
        if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) != 0) return;

        try
        {
            while (true)
            {
                PrintJob? job = null;
                await _queueLock.WaitAsync();
                try
                {
                    if (_printQueue.Count > 0)
                    {
                        job = _printQueue.Dequeue();
                    }
                }
                finally
                {
                    _queueLock.Release();
                }

                if (job == null) break;

            string destPrinter = !string.IsNullOrWhiteSpace(job.TargetPrinter)
                ? job.TargetPrinter
                : (ActiveSharedPrinter?.Name ?? string.Empty);

            var printerInfo = SharedPrinters.FirstOrDefault(p => string.Equals(p.Name, destPrinter, StringComparison.OrdinalIgnoreCase)) ?? ActiveSharedPrinter;

            if (printerInfo == null || !printerInfo.IsShared)
            {
                job.Status = PrintJobStatus.Failed;
                job.StatusMessage = $"Máy in '{destPrinter}' hiện không khả dụng.";
                await NotifyJobProgressAsync(job);
                continue;
            }

            job.Status = PrintJobStatus.Printing;
            job.StatusMessage = $"Đang gửi đến máy in ({destPrinter})...";
            await NotifyJobProgressAsync(job);

            bool printed = false;
            string errMsg = string.Empty;

            if (!string.IsNullOrEmpty(job.LocalTempFilePath) && File.Exists(job.LocalTempFilePath))
            {
                printed = _printerService.PrintFileDirectly(
                    destPrinter,
                    job.LocalTempFilePath,
                    job.DocumentName,
                    out errMsg
                );
            }

            if (printed)
            {
                job.Status = PrintJobStatus.Completed;
                job.CompletedAt = DateTime.UtcNow;
                job.StatusMessage = "Đã gửi đến máy in. Chưa xác minh được việc in ra giấy.";
                if (!string.IsNullOrEmpty(job.ContentSha256))
                {
                    _recentlyPrintedHashes.Add(job.ContentSha256);
                }

                // Track per-device print job count
                var client = ConnectedClients.FirstOrDefault(c => c.DeviceId == job.SenderDeviceId);
                if (client != null)
                {
                    client.PrintJobsCount++;
                    SaveClientList();
                }

                OnLog?.Invoke(this, $"[Đã gửi đến máy in] {job.DocumentName} từ {job.SenderDeviceName} ({job.Department}) -> {destPrinter}");
            }
            else
            {
                job.Status = PrintJobStatus.Failed;
                job.StatusMessage = $"Lỗi máy in: {errMsg}";
                OnLog?.Invoke(this, $"[Lỗi in] {job.DocumentName}: {errMsg}");
            }

                await NotifyJobProgressAsync(job);
                OnStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            Volatile.Write(ref _isProcessingQueue, 0);
        }
    }

    private async Task CloudWorkerLoopAsync()
    {
        while (!_cloudWorkerStop.IsCancellationRequested)
        {
            try
            {
                if (_cloud.Client is not null && _cloud.IsOnline)
                {
                    var jobs = await _cloud.Client.ListHostJobsAsync("QUEUED", 20, _cloudWorkerStop.Token);
                    foreach (var job in jobs ?? Array.Empty<CloudPrintJob>())
                    {
                        if (_cloudJobsInProgress.Add(job.JobId))
                            _ = ProcessCloudJobAsync(job);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(3), _cloudWorkerStop.Token);
            }
            catch (OperationCanceledException) when (_cloudWorkerStop.IsCancellationRequested) { break; }
            catch (Exception ex) { OnLog?.Invoke(this, $"[Cloud] Worker lỗi: {ex.Message}"); await Task.Delay(5000); }
        }
    }

    private async Task ProcessCloudJobAsync(CloudPrintJob job)
    {
        CloudJobClaim? claim = null;
        string? tempFile = null;
        bool spoolerAccepted = false;
        try
        {
            if (_cloud.Client is null) return;
            claim = await _cloud.Client.ClaimJobAsync(job.JobId, 120, _cloudWorkerStop.Token);
            if (claim is null) return;
            if (!_cloudJobStateStore.TryBegin(job.JobId))
            {
                await _cloud.Client.UpdateJobStatusAsync(job.JobId, new CloudJobStatusUpdate(
                    claim.ClaimToken, "UNKNOWN", null, "HOST_RESTART_RECOVERY",
                    "Job đã từng được Host xử lý; không tự động in lại để tránh in trùng."), _cloudWorkerStop.Token);
                OnLog?.Invoke(this, $"[Cloud] Job {job.JobId} được chuyển UNKNOWN để tránh in trùng sau khởi động lại.");
                return;
            }
            await _cloud.Client.AcknowledgeJobAsync(job.JobId, claim.ClaimToken, _cloudWorkerStop.Token);
            await _cloud.Client.UpdateJobStatusAsync(job.JobId, new CloudJobStatusUpdate(claim.ClaimToken, "DOWNLOADING"), _cloudWorkerStop.Token);
            var encrypted = await _cloud.Client.DownloadDocumentAsync(job.JobId, claim.ClaimToken, _cloudWorkerStop.Token);
            if (job.ContentSize is not null && encrypted.LongLength != job.ContentSize) throw new InvalidDataException("Cloud document size mismatch.");
            if (!string.IsNullOrWhiteSpace(job.ContentSha256) && !string.Equals(RPMCrypto.ComputeSha256(encrypted), job.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cloud document checksum mismatch.");

            byte[] plain = CloudDocumentCrypto.DecryptForThisDevice(encrypted, new CloudDeviceKeyStore());
            if (plain.Length == 0) throw new InvalidDataException("Cloud document is empty.");
            string documentFormat = GetMetadataString(job.Metadata, "documentFormat")?.ToLowerInvariant() ?? "application/octet-stream";
            if (!IsRawPrinterFormat(documentFormat))
                throw new NotSupportedException($"Định dạng '{documentFormat}' chưa có bộ chuyển đổi an toàn cho Windows Spooler.");
            string printerName = SharedPrinters.FirstOrDefault(p => CloudPrinterId(p.Name) == job.PrinterId)?.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(printerName)) throw new InvalidOperationException("Printer is not shared on this Host.");
            string extension = GetSafeCloudExtension(job.Metadata);
            tempFile = Path.Combine(ConfigManager.GetTempJobsFolder(), $"cloud-{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(tempFile, plain, _cloudWorkerStop.Token);
            await _cloud.Client.RenewClaimAsync(job.JobId, claim.ClaimToken, 120, _cloudWorkerStop.Token);
            await _cloud.Client.UpdateJobStatusAsync(job.JobId, new CloudJobStatusUpdate(claim.ClaimToken, "SPOOLING"), _cloudWorkerStop.Token);

            string documentName = GetMetadataString(job.Metadata, "documentName") ?? $"Cloud-{job.JobId}";
            bool accepted = _printerService.PrintFileDirectly(printerName, tempFile, documentName, out var error);
            if (!accepted) throw new InvalidOperationException(error);
            spoolerAccepted = true;
            await _cloud.Client.UpdateJobStatusAsync(job.JobId, new CloudJobStatusUpdate(claim.ClaimToken, "SPOOLER_ACCEPTED"), _cloudWorkerStop.Token);
            // PrintFileDirectly confirms spooler acceptance, not physical completion.
            await _cloud.Client.UpdateJobStatusAsync(job.JobId, new CloudJobStatusUpdate(claim.ClaimToken, "UNKNOWN", null, "SPOOLER_COMPLETION_UNVERIFIED", "Windows Spooler accepted the job; physical completion was not verifiable."), _cloudWorkerStop.Token);
            _cloudJobStateStore.MarkUnknown(job.JobId);
            OnLog?.Invoke(this, $"[Cloud] Đã gửi Job {job.JobId} vào Spooler; chưa xác minh hoàn tất vật lý.");
        }
        catch (Exception ex)
        {
            if (claim is not null && _cloud.Client is not null)
            {
                string status = spoolerAccepted ? "UNKNOWN" : "FAILED";
                string failureCode = spoolerAccepted ? "SPOOLER_COMPLETION_UNVERIFIED" : "HOST_WORKER_ERROR";
                string failureMessage = spoolerAccepted
                    ? "Windows Spooler đã nhận tài liệu nhưng mất kết nối trước khi xác nhận trạng thái cuối. Không tự động in lại."
                    : ex.Message[..Math.Min(500, ex.Message.Length)];
                try { await _cloud.Client.UpdateJobStatusAsync(job.JobId, new CloudJobStatusUpdate(claim.ClaimToken, status, null, failureCode, failureMessage), CancellationToken.None); } catch { }
            }
            _cloudJobStateStore.MarkUnknown(job.JobId);
            OnLog?.Invoke(this, $"[Cloud] Không xử lý Job {job.JobId}: {ex.Message}");
        }
        finally
        {
            if (tempFile is not null) try { File.Delete(tempFile); } catch { }
            lock (_cloudJobsInProgress) _cloudJobsInProgress.Remove(job.JobId);
        }
    }

    private static string? GetMetadataString(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null) return null;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.String) return element.GetString();
        return value.ToString();
    }

    private static string GetSafeCloudExtension(IReadOnlyDictionary<string, object?> metadata) =>
        GetMetadataString(metadata, "fileExtension")?.ToLowerInvariant() switch
        {
            ".pdf" => ".pdf", ".txt" => ".txt", ".ps" => ".ps", ".prn" => ".prn", _ => ".bin"
        };

    private static bool IsRawPrinterFormat(string documentFormat) => documentFormat switch
    {
        "application/octet-stream" => true,
        "application/postscript" => true,
        "application/vnd.hp-pcl" => true,
        "application/vnd.hp-pclxl" => true,
        _ => false
    };

    private async Task NotifyJobProgressAsync(PrintJob job)
    {
        var progress = new PrintJobProgressPayload
        {
            JobId = job.JobId,
            Status = job.Status,
            Message = job.StatusMessage
        };

        var packet = new NetworkPacket
        {
            Type = MessageType.PrintJobProgress,
            SenderId = _settings.DeviceId,
            TargetId = job.SenderDeviceId,
            PairingCode = _settings.PairingCode,
            PayloadJson = JsonSerializer.Serialize(progress)
        };

        await _relayClient.SendPacketAsync(packet);
    }

    public void ClearCompletedJobs()
    {
        var completed = PrintJobs.Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed).ToList();
        foreach (var job in completed)
        {
            PrintJobs.Remove(job);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        _cloudWorkerStop.Cancel();
        _embeddedRelay?.Stop();

        var workers = new[] { _cloudWorkerTask, _cloudPairingWorkerTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (_directStreamCleanupTask is not null)
            workers = workers.Append(_directStreamCleanupTask).ToArray();
        if (workers.Length > 0)
        {
            try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            catch (Exception ex) { OnLog?.Invoke(this, $"[Shutdown] Worker dừng với cảnh báo: {ex.Message}"); }
        }

        List<DirectStreamState> activeStreams;
        lock (_directStreamLock)
        {
            activeStreams = _directStreams.Values.ToList();
            _directStreams.Clear();
        }
        foreach (var state in activeStreams)
            await DisposeDirectStreamFileAsync(state);

        try { await _relayClient.StopAsync(); } catch { }
        _relayClient.Dispose();
        await _cloud.DisposeAsync();
        _queueLock.Dispose();
        _cloudPairingRefreshLock.Dispose();
        _cloudGeofenceLock.Dispose();
        _cloudWorkerStop.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
