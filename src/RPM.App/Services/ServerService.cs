using System.Collections.ObjectModel;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using RPM.Core.Cloud;
using RPM.Core.Config;
using RPM.Core.Models;
using RPM.Core.Protocol;
using RPM.Core.Security;
using RPM.Networking;
using RPM.Printing;

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
    private readonly SemaphoreSlim _cloudPairingRefreshLock = new(1, 1);
    private int _cloudWorkersStarted;
    private Task? _cloudWorkerTask;
    private Task? _cloudPairingWorkerTask;
    private int _isDisposed;
    private readonly Queue<PrintJob> _printQueue = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly HashSet<string> _recentlyPrintedHashes = new();
    private const int MaxQueuedJobs = 100;
    private RelayServer? _embeddedRelay;
    private bool _isProcessingQueue;

    public RelayClient Relay => _relayClient;
    public PrinterService PrinterService => _printerService;

    public ObservableCollection<DeviceInfo> ConnectedClients { get; } = new();
    public ObservableCollection<PrintJob> PrintJobs { get; } = new();
    public ObservableCollection<SharedPrinterInfo> SharedPrinters { get; } = new();
    public SharedPrinterInfo? ActiveSharedPrinter { get; private set; }
    public bool IsCloudOnline => _cloud.IsOnline;
    public bool HasStoredCloudToken => _cloud.HasStoredToken;
    public CloudActivationInfo? CloudActivationInfo => _cloud.ActivationInfo;
    public bool IsOnline => _cloud.IsOnline || _relayClient.IsConnected;
    public string ConnectionStatus => _cloud.IsOnline
        ? "Đã kết nối máy chủ từ xa"
        : _relayClient.IsConnected ? "Đã kết nối nội bộ" : "Ngoại tuyến";
    public int CloudApprovedClientCount { get; private set; }
    public int CloudOnlineClientCount { get; private set; }
    public int CloudMaxClientCount { get; private set; } = 15;
    public bool CloudCapacityLoaded { get; private set; }
    public string GeofenceStatusDisplay { get; private set; } = "Chưa bật";

    public event EventHandler<string>? OnLog;
    public event EventHandler<DeviceInfo>? OnClientRequestedApproval;
    public event EventHandler? OnStatusChanged;

    public ServerService(AppSettings settings)
    {
        _settings = settings;
        _cloud = new CloudConnectionService(settings);
        _cloud.OnLog += (s, msg) => OnLog?.Invoke(this, msg);
        _cloud.OnStateChanged += (s, e) =>
        {
            if (_settings.CloudTransportEnabled && _cloud.IsOnline)
                _ = EnsureCloudWorkersAsync();
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

    public async Task StartAsync()
    {
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
            }
        }

        if (cloudReady)
        {
            OnLog?.Invoke(this, "[Cloud] Host worker đã khởi động; Relay giữ ở trạng thái dự phòng.");
            return;
        }

        // Auto-start embedded relay if server is configured for local address
        if (_settings.RelayServerUrl.Contains("127.0.0.1") || _settings.RelayServerUrl.Contains("localhost"))
        {
            try
            {
                int port = 9090;
                if (Uri.TryCreate(_settings.RelayServerUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
                {
                    port = uri.Port;
                }
                _embeddedRelay = new RelayServer();
                _embeddedRelay.Start(port);
                OnLog?.Invoke(this, $"[Relay] Đã tự động kích hoạt máy chủ trung chuyển nội bộ trên cổng {port}.");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(this, $"[Relay] Khởi động trung chuyển nội bộ: {ex.Message}");
            }
        }

        await _relayClient.StartAsync(_settings.RelayServerUrl);
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
        return true;
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
            OnLog?.Invoke(this, $"[Cloud] Mã ghép nối: {code.Code}");
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
            catch (Exception ex) { OnLog?.Invoke(this, $"[Cloud] Lỗi kiểm tra yêu cầu ghép đôi: {ex.Message}"); await Task.Delay(2000); }
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
            if (summary is not null)
            {
                CloudApprovedClientCount = summary.ApprovedCount;
                CloudOnlineClientCount = summary.OnlineCount;
                CloudMaxClientCount = summary.MaxClients;
                CloudCapacityLoaded = true;
            }

            var geofence = await client.GetGeofenceAsync(cancellationToken);
            if (geofence is not null)
            {
                GeofenceStatusDisplay = geofence.Enabled
                    ? $"Bán kính {geofence.RadiusMeters:0} m"
                    : "Chưa bật";
            }

            foreach (var item in pending ?? Array.Empty<CloudPendingPairing>())
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
                        IsOnline = true,
                        LastSeen = DateTime.UtcNow
                    };
                    Application.Current?.Dispatcher.Invoke(() => ConnectedClients.Add(device));
                    SaveClientList();
                }

                if (isNewPairing) OnClientRequestedApproval?.Invoke(this, device);
            }

            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _cloudPairingRefreshLock.Release();
        }
    }

    public void LoadPrinters()
    {
        ApplyPrinterScan(ScanPrinters());
    }

    public List<SharedPrinterInfo> ScanPrinters()
    {
        var installed = _printerService.GetInstalledPrinters();
        foreach (var p in installed)
        {
            p.IsShared = _settings.SharedPrinterNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(_settings.SelectedPrinterName) && string.Equals(p.Name, _settings.SelectedPrinterName, StringComparison.OrdinalIgnoreCase));
            p.IsDefault = !string.IsNullOrWhiteSpace(_settings.SelectedPrinterName)
                && string.Equals(p.Name, _settings.SelectedPrinterName, StringComparison.OrdinalIgnoreCase);
        }
        return installed;
    }

    public void ApplyPrinterScan(IEnumerable<SharedPrinterInfo> printers)
    {
        ActiveSharedPrinter = null;
        SharedPrinters.Clear();
        foreach (var p in printers)
        {
            if (p.IsShared && ActiveSharedPrinter == null)
            {
                ActiveSharedPrinter = p;
            }
            SharedPrinters.Add(p);
        }

        if (ActiveSharedPrinter == null && SharedPrinters.Any(p => p.IsShared))
        {
            ActiveSharedPrinter = SharedPrinters.First(p => p.IsShared);
        }
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
                if (ActiveSharedPrinter?.Name == printer.Name)
                {
                    ActiveSharedPrinter = SharedPrinters.FirstOrDefault(p => p.IsShared);
                    _settings.SelectedPrinterName = ActiveSharedPrinter?.Name ?? string.Empty;
                }
                foreach (var item in SharedPrinters)
                {
                    item.IsDefault = ActiveSharedPrinter != null && string.Equals(item.Name, ActiveSharedPrinter.Name, StringComparison.OrdinalIgnoreCase);
                }
                OnLog?.Invoke(this, $"[Máy In] Đã TẮT chia sẻ máy in: {printer.Name}");
            }
            ConfigManager.Save(_settings);
            BroadcastPrinterStatus();
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetDefaultPrinter(string printerName)
    {
        var target = SharedPrinters.FirstOrDefault(p => p.Name == printerName);
        if (target != null)
        {
            target.IsShared = true;
            if (!_settings.SharedPrinterNames.Contains(printerName, StringComparer.OrdinalIgnoreCase))
            {
                _settings.SharedPrinterNames.Add(printerName);
            }
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
        var shared = SharedPrinters.Where(p => p.IsShared).ToList();
        if (shared.Count == 0 && ActiveSharedPrinter != null && ActiveSharedPrinter.IsShared)
        {
            shared.Add(ActiveSharedPrinter);
        }

        if (client.AllowedPrinters == null || client.AllowedPrinters.Count == 0)
        {
            return shared;
        }

        return shared.Where(p => client.AllowedPrinters.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public async Task ApproveClientAsync(DeviceInfo client)
    {
        client.IsApproved = true;
        client.IsBlocked = false;
        SaveClientList();

        var permittedPrinters = GetPrintersForClient(client);

        if (_settings.CloudTransportEnabled && _cloud.Client is not null && _cloudPairingIds.TryGetValue(client.DeviceId, out var cloudPairingId))
        {
            await _cloud.Client.DecidePairingAsync(cloudPairingId, true, permittedPrinters.Select(p => CloudPrinterId(p.Name)).ToList());
            OnLog?.Invoke(this, $"[Cloud] Đã duyệt thiết bị: {client.DeviceName}");
            return;
        }

        var response = new ClientJoinResponsePayload
        {
            Approved = true,
            IsPending = false,
            Message = "Máy chủ đã chấp thuận thiết bị của bạn.",
            EncryptionSecret = _settings.EncryptionSecret,
            SharedPrinter = permittedPrinters.FirstOrDefault() ?? ActiveSharedPrinter,
            AvailablePrinters = permittedPrinters
        };

        await _relayClient.SendEncryptedPayloadAsync(
            MessageType.ClientJoinResponse,
            _settings.DeviceId,
            client.DeviceId,
            _settings.PairingCode,
            response,
            _settings.PairingCode);
        OnLog?.Invoke(this, $"[Duyệt] Đã chấp thuận máy con: {client.DeviceName} (Phòng: {client.Department}) - Quyền: {client.AllowedPrintersSummary}");
    }

    public async Task RevokeClientAsync(DeviceInfo client)
    {
        client.IsApproved = false;
        client.IsBlocked = true;
        SaveClientList();

        if (_settings.CloudTransportEnabled && _cloud.Client is not null && _cloudPairingIds.TryGetValue(client.DeviceId, out var cloudPairingId))
        {
            await _cloud.Client.DecidePairingAsync(cloudPairingId, false, Array.Empty<string>());
            OnLog?.Invoke(this, $"[Cloud] Đã từ chối thiết bị: {client.DeviceName}");
            return;
        }

        var packet = new NetworkPacket
        {
            Type = MessageType.DeviceRevoked,
            SenderId = _settings.DeviceId,
            TargetId = client.DeviceId,
            PairingCode = _settings.PairingCode,
            PayloadJson = "{\"status\":\"revoked\"}"
        };

        await _relayClient.SendPacketAsync(packet);
        OnLog?.Invoke(this, $"[Thu hồi] Đã thu hồi quyền máy con: {client.DeviceName}");
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

    public void SetClientAllowedPrinters(string deviceId, List<string> allowedPrinters)
    {
        var client = ConnectedClients.FirstOrDefault(c => c.DeviceId == deviceId);
        if (client != null)
        {
            client.AllowedPrinters = allowedPrinters;
            SaveClientList();
            OnLog?.Invoke(this, $"[Phân quyền] Cập nhật máy in cho '{client.DeviceName}': {client.AllowedPrintersSummary}");
            if (client.IsApproved && client.IsOnline)
            {
                _ = ApproveClientAsync(client);
            }
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
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
            var sharedList = SharedPrinters.Where(p => p.IsShared).ToList();
            var payload = new PrintersListUpdatePayload
            {
                Printers = sharedList
            };

            var packet = new NetworkPacket
            {
                Type = MessageType.PrintersListUpdate,
                SenderId = _settings.DeviceId,
                PairingCode = _settings.PairingCode,
                PayloadJson = JsonSerializer.Serialize(payload)
            };

            _ = _relayClient.SendPacketAsync(packet);

            if (ActiveSharedPrinter != null)
            {
                _ = _relayClient.SendPacketAsync(new NetworkPacket
                {
                    Type = MessageType.PrinterStatusUpdate,
                    SenderId = _settings.DeviceId,
                    PairingCode = _settings.PairingCode,
                    PayloadJson = JsonSerializer.Serialize(ActiveSharedPrinter)
                });
            }
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
            SharedPrinter = ActiveSharedPrinter,
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

        // Granular access control check per device
        if (senderDevice.AllowedPrinters != null && senderDevice.AllowedPrinters.Count > 0)
        {
            if (!senderDevice.AllowedPrinters.Contains(targetPrinterName, StringComparer.OrdinalIgnoreCase))
            {
                OnLog?.Invoke(this, $"[Từ chối] Máy con '{senderDevice.DeviceName}' ({senderDevice.Department}) không có quyền in trên '{targetPrinterName}'.");
                job.Status = PrintJobStatus.Failed;
                job.StatusMessage = $"Thiết bị của bạn không có quyền in trên máy in '{targetPrinterName}'.";
                await NotifyJobProgressAsync(job);
                return;
            }
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
        if (_isProcessingQueue) return;
        _isProcessingQueue = true;

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
                job.StatusMessage = $"Máy in '{destPrinter}' đang tạm dừng chia sẻ.";
                await NotifyJobProgressAsync(job);
                continue;
            }

            job.Status = PrintJobStatus.Printing;
            job.StatusMessage = $"Đang gửi dữ liệu tới máy in USB ({destPrinter})...";
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
                job.StatusMessage = "Đã xuất tài liệu ra máy in thành công!";
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

                OnLog?.Invoke(this, $"[In thành công] {job.DocumentName} từ {job.SenderDeviceName} ({job.Department}) -> {destPrinter}");
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

        _isProcessingQueue = false;
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
                try { await _cloud.Client.UpdateJobStatusAsync(job.JobId, new CloudJobStatusUpdate(claim.ClaimToken, "FAILED", null, "HOST_WORKER_ERROR", ex.Message[..Math.Min(500, ex.Message.Length)]), CancellationToken.None); } catch { }
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
        if (workers.Length > 0)
        {
            try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            catch (Exception ex) { OnLog?.Invoke(this, $"[Shutdown] Worker dừng với cảnh báo: {ex.Message}"); }
        }

        try { await _relayClient.StopAsync(); } catch { }
        _relayClient.Dispose();
        await _cloud.DisposeAsync();
        _queueLock.Dispose();
        _cloudPairingRefreshLock.Dispose();
        _cloudWorkerStop.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
