using System.Collections.ObjectModel;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Windows;
using RPM.Core.Config;
using RPM.Core.Connection;
using RPM.Core.Cloud;
using RPM.Core.Models;
using RPM.Core.Protocol;
using RPM.Core.Security;
using RPM.Core.Networking;
using RPM.Networking;
using RPM.Printing;

namespace RPM.App.Services;

public class ClientService : IDisposable, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly RelayClient _relayClient = new();
    private readonly LocalIppServer _ippServer = new();
    private readonly CloudConnectionService _cloud;
    private readonly WindowsLocationService _locationService = new();
    private readonly SemaphoreSlim _relayStartLock = new(1, 1);
    private bool _isApproved;
    private string _connectionStatus = "Chưa kết nối";
    private SharedPrinterInfo? _remotePrinter;
    private readonly Dictionary<string, string> _cloudPrinterIds = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<bool>? _joinRequestCompletion;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PrintStreamAckPayload>> _streamAcks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cloudApprovalLock = new(1, 1);
    private volatile bool _remotePrinterInstalled;
    private volatile bool _startupReady;
    private int _isDisposed;

    public RelayClient Relay => _relayClient;
    public LocalIppServer IppServer => _ippServer;

    public ObservableCollection<PrintJob> PrintHistory { get; } = new();
    public ObservableCollection<SharedPrinterInfo> AvailablePrinters { get; } = new();
    public SharedPrinterInfo? RemotePrinter => _remotePrinter;
    public bool IsApproved => _isApproved;
    public bool IsCloudOnline => _cloud.IsOnline;
    public ActivationStatus ActivationStatus => _cloud.ActivationStatus;
    public bool HasStoredCloudToken => _cloud.HasStoredToken;
    public CloudActivationInfo? CloudActivationInfo => _cloud.ActivationInfo;
    public bool IsOnline => _cloud.IsOnline || _relayClient.IsConnected;
    public bool IsInternalConnectionReady => _relayClient.IsConnected;
    public bool IsConnecting { get; private set; }
    public string RelayEndpoint => _settings.RelayServerUrl;
    public bool IsRemotePrinterInstalled => _remotePrinterInstalled;
    public string ConnectionStatus => ConnectionStatusInfo.PrimaryText;
    public string DetailedConnectionStatus => _connectionStatus;
    public ConnectionStatusSnapshot ConnectionStatusInfo => ConnectionStatusResolver.Resolve(
        _cloud.ActivationStatus,
        _cloud.IsOnline,
        _relayClient.IsConnected,
        _cloud.HasPreviouslyVerifiedActivation,
        _startupReady);
    public ConnectionMode ConnectionMode => ConnectionStatusInfo.ConnectionMode;
    public TransportStatus TransportStatus => ConnectionStatusInfo.TransportStatus;
    public string ConnectionStatusDisplay => ConnectionStatusInfo.PrimaryText;
    public string ConnectionStatusSecondaryDisplay => ConnectionStatusInfo.SecondaryText ?? string.Empty;
    public string ActivationStatusDisplay => ConnectionStatusResolver.GetActivationText(_cloud.ActivationStatus);
    public string GeofenceStatusDisplay { get; private set; } = "Chưa xác minh vị trí";
    public string PrintQueueSetupMessage { get; private set; } = "Chưa kiểm tra hàng đợi Windows.";
    private bool RequiresBackendPrintAuthorization => _cloud.IsCustomerActivation;

    public event EventHandler<string>? OnLog;
    public event EventHandler? OnStateChanged;
    public event EventHandler? OnEnrollmentRevoked;

    public ClientService(AppSettings settings)
    {
        _settings = settings;
        _cloud = new CloudConnectionService(settings);
        _cloud.OnLog += (s, msg) => OnLog?.Invoke(this, msg);
        _cloud.OnStateChanged += (s, e) =>
        {
            if (_startupReady)
                OnStateChanged?.Invoke(this, EventArgs.Empty);
            if (_cloud.IsOnline) _ = ResyncCloudPairingAsync();
            if (!_cloud.IsOnline) _ = StartRelayFallbackAsync();
        };
        _cloud.OnEnrollmentRevoked += (s, e) => OnEnrollmentRevoked?.Invoke(this, EventArgs.Empty);
        _cloud.OnRealtimeEventAsync += Cloud_OnRealtimeEventAsync;

        // Restore history
        foreach (var job in _settings.PrintHistory.Take(50))
        {
            PrintHistory.Add(job);
        }

        _relayClient.OnConnected += Relay_OnConnected;
        _relayClient.OnDisconnected += Relay_OnDisconnected;
        _relayClient.OnPacketReceived += Relay_OnPacketReceived;
        _relayClient.OnLog += (s, msg) => OnLog?.Invoke(this, msg);

        _ippServer.OnJobReceived += IppServer_OnJobReceived;
        _ippServer.OnLog += (s, msg) => OnLog?.Invoke(this, msg);
    }

    private async Task Cloud_OnRealtimeEventAsync(CloudRealtimeEvent realtimeEvent)
    {
        if (!string.IsNullOrWhiteSpace(realtimeEvent.PairingId) &&
            (realtimeEvent.EventType.Equals("PAIRING_APPROVED", StringComparison.OrdinalIgnoreCase) ||
             realtimeEvent.EventType.Equals("PAIRING_REJECTED", StringComparison.OrdinalIgnoreCase) ||
              realtimeEvent.EventType.Equals("PAIRING_REVOKED", StringComparison.OrdinalIgnoreCase) ||
              realtimeEvent.EventType.Equals("PAIRING_PRINTER_PERMISSIONS_UPDATED", StringComparison.OrdinalIgnoreCase) ||
              realtimeEvent.EventType.Equals("PRINTER_CATALOG_UPDATED", StringComparison.OrdinalIgnoreCase)))
        {
            await ApplyRealtimePairingDecisionAsync(realtimeEvent.PairingId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(realtimeEvent.JobId) && realtimeEvent.EventType.StartsWith("PRINT_JOB_", StringComparison.OrdinalIgnoreCase))
            ApplyRealtimeJobStatus(realtimeEvent);
    }

    private async Task ApplyRealtimePairingDecisionAsync(string pairingId)
    {
        await _cloudApprovalLock.WaitAsync();
        try { await ApplyCloudPairingStatusAsync(pairingId, CancellationToken.None); }
        catch (Exception ex) { OnLog?.Invoke(this, $"[Realtime] Không đồng bộ quyết định ghép nối: {ex.Message}"); }
        finally { _cloudApprovalLock.Release(); }
    }

    private void ApplyRealtimeJobStatus(CloudRealtimeEvent realtimeEvent)
    {
        if (!realtimeEvent.Payload.TryGetValue("status", out var statusValue)) return;
        string? status = statusValue.ValueKind == JsonValueKind.String ? statusValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(status)) return;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var job = PrintHistory.FirstOrDefault(item => item.JobId == realtimeEvent.JobId);
            if (job is null) return;
            job.Status = PrintJobStatusMapper.FromCloud(status, job.Status);
            job.StatusMessage = PrintJobStatusMapper.MessageFromCloud(status);
            OnStateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public async Task StartAsync()
    {
        _startupReady = false;
        try
        {
            if (_settings.CloudTransportEnabled || _cloud.HasStoredToken)
            {
                // Verify the stored activation before the optional Windows printer
                // setup so the dashboard can resolve the service state promptly.
                await _cloud.StartAsync();
            }

            _ippServer.Start(_settings.LocalIppPort);
            _remotePrinterInstalled = string.Equals(
                VirtualPrinterManager.FindPrinterBoundToLocalIpp(_settings.LocalIppPort),
                VirtualPrinterManager.DefaultPrinterName,
                StringComparison.OrdinalIgnoreCase);
            await EnsureRemotePrinterAsync();

            // Direct printing is the selected transport when enabled. Cloud remains
            // available as the legacy rollback path when direct streaming is disabled.
            if (_settings.UsesDirectTransport || !_cloud.IsOnline)
                await StartRelayFallbackAsync();
        }
        finally
        {
            _startupReady = true;
            OnStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task EnsureRemotePrinterAsync()
    {
        if (!_ippServer.IsRunning)
        {
            PrintQueueSetupMessage = "Local IPP Server chưa mở; sẽ thử cấp quyền bind và khởi động lại trong quá trình cài hàng đợi.";
            OnLog?.Invoke(this, $"[IPP] {PrintQueueSetupMessage}");
        }

        if (IsRemotePrinterInstalled && _ippServer.IsRunning && _ippServer.IsLanBindingActive)
        {
            PrintQueueSetupMessage = "Hàng đợi 'Remote Print Manager' đã sẵn sàng cho Ctrl + P.";
            OnLog?.Invoke(this, "[IPP] Hàng đợi 'Remote Print Manager' đã sẵn sàng cho Ctrl + P.");
            return;
        }

        var result = await VirtualPrinterManager.InstallVirtualPrinterAsync(
            VirtualPrinterManager.DefaultPrinterName,
            _settings.LocalIppPort,
            () =>
            {
                _ippServer.Restart(_settings.LocalIppPort);
                return _ippServer.IsRunning && _ippServer.IsLanBindingActive;
            });
        _remotePrinterInstalled = result.Success &&
            string.Equals(
                VirtualPrinterManager.FindPrinterBoundToLocalIpp(_settings.LocalIppPort),
                VirtualPrinterManager.DefaultPrinterName,
                StringComparison.OrdinalIgnoreCase);
        PrintQueueSetupMessage = result.Success
            ? _remotePrinterInstalled
                ? "[IPP] Đã chuẩn bị hàng đợi 'Remote Print Manager'. Trong cửa sổ Ctrl + P, hãy chọn đúng hàng đợi này."
                : "[IPP] Windows đã tạo hàng đợi nhưng chưa xác nhận đúng cổng IPP; Ctrl + P chưa được coi là sẵn sàng."
            : $"[IPP] Chưa chuẩn bị được hàng đợi Ctrl + P: {result.Message}";
        OnLog?.Invoke(this, PrintQueueSetupMessage);
    }

    public async Task<bool> PrepareWindowsPrintQueueAsync()
    {
        await EnsureRemotePrinterAsync();
        return _remotePrinterInstalled;
    }

    public async Task ConnectToServerAsync(string pairingCode)
    {
        string code = pairingCode.Trim().ToUpperInvariant();
        bool isCloudCode = code.Length == 6 && !code.StartsWith("RPM-", StringComparison.OrdinalIgnoreCase);
        if ((_settings.CloudTransportEnabled || _cloud.HasStoredToken) && isCloudCode)
        {
            if (await ConnectToCloudAsync(code)) return;

            // Cloud pairing codes are not valid Relay credentials. Fall back
            // only to the separately stored LAN code when it is available.
            if (PacketSecurity.IsValidPairingCode(_settings.PairingCode))
            {
                await ConnectToRelayAsync(_settings.PairingCode);
            }
            return;
        }
        await ConnectToRelayAsync(code);
    }

    private async Task StartRelayFallbackAsync()
    {
        if (!PacketSecurity.IsValidPairingCode(_settings.PairingCode))
        {
            OnLog?.Invoke(this, "Cloud đang ngoại tuyến. Kết nối nội bộ cần mã LAN dạng RPM-XXXXXX và địa chỉ LAN của máy chủ.");
            return;
        }

        await _relayStartLock.WaitAsync();
        try
        {
            if (_relayClient.IsConnected) return;
            if (!await EnsureRelayEndpointAsync())
            {
                _connectionStatus = "Chưa kết nối nội bộ";
                OnLog?.Invoke(this, "Không phát hiện được máy chủ trong mạng LAN. Hãy kiểm tra cùng mạng, tường lửa và cổng Relay.");
                OnStateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            _connectionStatus = "Đang kết nối nội bộ...";
            _relayClient.PairingCode = _settings.PairingCode;
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            await _relayClient.StartAsync(_settings.RelayServerUrl);
        }
        finally
        {
            _relayStartLock.Release();
        }
    }

    private async Task<bool> EnsureRelayEndpointAsync()
    {
        bool currentEndpointValid = RelayEndpointPolicy.TryValidateForRole(AppRole.Client, _settings.RelayServerUrl, out _);
        if (currentEndpointValid && !_settings.RelayServerAutoDiscovered)
            return true;

        int relayPort = 9090;
        if (Uri.TryCreate(_settings.RelayServerUrl, UriKind.Absolute, out var configured) && configured.Port > 0)
            relayPort = configured.Port;

        try
        {
            var endpoints = await LanRelayDiscovery.DiscoverAsync(
                relayPort,
                relayPort + 1,
                TimeSpan.FromSeconds(2));
            string? endpoint = endpoints.FirstOrDefault(item =>
                RelayEndpointPolicy.TryValidateForRole(AppRole.Client, item, out _));
            if (endpoint is null) return currentEndpointValid;

            _settings.RelayServerUrl = endpoint;
            _settings.RelayServerAutoDiscovered = true;
            ConfigManager.Save(_settings);
            OnLog?.Invoke(this, $"Đã tự phát hiện máy chủ LAN: {endpoint}");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"Không thể tự phát hiện máy chủ LAN: {ex.Message}");
            return currentEndpointValid;
        }
    }

    private async Task<bool> EnsureRelayConnectedAsync(TimeSpan timeout)
    {
        if (_relayClient.IsConnected) return true;
        if (!PacketSecurity.IsValidPairingCode(_settings.PairingCode)) return false;
        await StartRelayFallbackAsync();

        DateTime deadline = DateTime.UtcNow + timeout;
        while (!_relayClient.IsConnected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        return _relayClient.IsConnected;
    }

    private async Task ConnectToRelayAsync(string pairingCode)
    {
        string normalizedCode = pairingCode.Trim().ToUpperInvariant();
        if (!PacketSecurity.IsValidPairingCode(normalizedCode))
        {
            OnLog?.Invoke(this, "Mã kết nối nội bộ không đúng. Hãy nhập mã dạng RPM-XXXXXX.");
            return;
        }
        _settings.PairingCode = normalizedCode;
        _relayClient.PairingCode = _settings.PairingCode;
        ConfigManager.Save(_settings);
        IsConnecting = true;
        _connectionStatus = "Đang tìm máy chủ...";
        OnStateChanged?.Invoke(this, EventArgs.Empty);

        var joinCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _joinRequestCompletion = joinCompletion;

        if (!await EnsureRelayEndpointAsync())
        {
            _connectionStatus = "Chưa kết nối";
            _joinRequestCompletion = null;
            OnLog?.Invoke(this, "Không tìm thấy địa chỉ máy chủ LAN. Hãy bảo đảm hai máy cùng mạng và máy chủ đã mở Relay.");
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            if (_relayClient.IsConnected)
            {
                bool sent = await SendJoinRequestAsync();
                joinCompletion.TrySetResult(sent);
            }
            else
            {
                await _relayClient.StartAsync(_settings.RelayServerUrl);
            }

            var completed = await Task.WhenAny(joinCompletion.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != joinCompletion.Task)
            {
                _connectionStatus = "Không tìm thấy máy chủ";
                OnLog?.Invoke(this, "Không nhận được phản hồi từ máy chủ trong 15 giây.");
                OnStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _connectionStatus = "Không thể kết nối máy chủ";
            OnLog?.Invoke(this, $"Lỗi tìm máy chủ: {ex.Message}");
            OnStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _joinRequestCompletion = null;
            IsConnecting = false;
            OnStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<bool> EnrollCloudAsync(string enrollmentSecret, CancellationToken cancellationToken = default)
    {
        bool enrolled = await _cloud.EnrollAsync(enrollmentSecret, cancellationToken);
        if (enrolled)
        {
            _settings.CloudTransportEnabled = true;
            ConfigManager.Save(_settings);
            await _cloud.StartAsync(cancellationToken);
        }
        return enrolled;
    }

    private async Task<bool> ConnectToCloudAsync(string pairingCode)
    {
        string code = pairingCode.Trim().ToUpperInvariant();
        if (code.Length != 6)
        {
            OnLog?.Invoke(this, "Mã kết nối phải có 6 ký tự.");
            return false;
        }
        IsConnecting = true;
        _connectionStatus = "Đang tìm máy chủ...";
        OnStateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            if (!_cloud.IsOnline || !await _cloud.ProbeAsync())
            {
                _connectionStatus = "Mất kết nối dịch vụ; đang thử kết nối nội bộ";
                return false;
            }
            var location = await _locationService.TryGetCurrentAsync();
            var resolution = await _cloud.Client!.ResolvePairingCodeAsync(code, location);
            if (resolution is null)
            {
                _connectionStatus = "Không tìm thấy máy chủ";
                return false;
            }
            _settings.CloudHostId = resolution.HostId;
            _settings.CloudPairingCode = code;
            _settings.CloudPairingId = resolution.PairingId;
            ConfigManager.Save(_settings);
            GeofenceStatusDisplay = resolution.LocationStatus switch
            {
                "VERIFIED" => "Trong giới hạn khoảng cách",
                "OUT_OF_RANGE" => "Ngoài giới hạn khoảng cách",
                _ => "Chưa xác minh vị trí"
            };
            _connectionStatus = "Đang gửi yêu cầu kết nối...";
            OnLog?.Invoke(this, "Đã gửi yêu cầu kết nối. Chờ máy chủ chấp nhận trước khi in.");
            _ = WaitForCloudApprovalAsync(resolution.PairingId);
            return true;
        }
        catch (Exception ex)
        {
            _connectionStatus = "Mất kết nối dịch vụ; đang thử kết nối nội bộ";
            OnLog?.Invoke(this, $"Không thể kết nối máy chủ từ xa: {ex.Message}");
            return false;
        }
        finally { IsConnecting = false; OnStateChanged?.Invoke(this, EventArgs.Empty); }
    }

    private async Task WaitForCloudApprovalAsync(string pairingId)
    {
        for (var attempt = 0; attempt < 30 && (_settings.CloudTransportEnabled || _cloud.HasStoredToken); attempt++)
        {
            try
            {
                await Task.Delay(_settings.RealtimeEnabled ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(2));
                var api = _cloud.Client;
                if (api is null) return;
                if (await ApplyCloudPairingStatusAsync(pairingId, CancellationToken.None)) return;
            }
            catch (Exception ex) { OnLog?.Invoke(this, $"Máy chủ từ xa chưa phản hồi: {ex.Message}"); }
        }
    }

    private async Task<bool> ApplyCloudPairingStatusAsync(string pairingId, CancellationToken cancellationToken)
    {
        var api = _cloud.Client;
        if (api is null) return false;
        var pairing = await api.GetPairingAsync(pairingId, cancellationToken);
        if (pairing is null) return false;
        if (string.Equals(pairing.Status, "REJECTED", StringComparison.OrdinalIgnoreCase) || string.Equals(pairing.Status, "REVOKED", StringComparison.OrdinalIgnoreCase))
        {
            _isApproved = false;
            ClearCloudPairingState();
            _connectionStatus = string.Equals(pairing.Status, "REVOKED", StringComparison.OrdinalIgnoreCase) ? "Máy chủ đã thu hồi quyền" : "Máy chủ đã từ chối";
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        if (!string.Equals(pairing.Status, "APPROVED", StringComparison.OrdinalIgnoreCase)) return false;
        var host = await api.GetDeviceProfileAsync(_settings.CloudHostId, cancellationToken);
        if (host is null || host.IsRevoked || string.IsNullOrWhiteSpace(host.PublicKey))
        {
            _connectionStatus = "Máy chủ chưa cung cấp khóa bảo mật";
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        string hostFingerprint = CloudDocumentCrypto.ComputePublicKeyFingerprint(host.PublicKey);
        if (!string.IsNullOrWhiteSpace(_settings.CloudHostPublicKeyFingerprint) && !string.Equals(_settings.CloudHostPublicKeyFingerprint, hostFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _connectionStatus = "Khóa bảo mật máy chủ đã thay đổi";
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        _settings.CloudHostPublicKey = host.PublicKey;
        _settings.CloudHostPublicKeyFingerprint = hostFingerprint;
        ConfigManager.Save(_settings);
        _isApproved = true;
        _connectionStatus = "Đã được chấp nhận";
        var printers = await api.ListPrintersAsync(_settings.CloudHostId, cancellationToken);
        _cloudPrinterIds.Clear();
        Application.Current?.Dispatcher.Invoke(() =>
        {
            AvailablePrinters.Clear();
            foreach (var printer in printers?.Where(p => p.IsShared) ?? Array.Empty<CloudPrinter>())
            {
                _cloudPrinterIds[printer.DisplayName] = printer.PrinterId;
                AvailablePrinters.Add(new SharedPrinterInfo { Name = printer.DisplayName, IsShared = printer.IsShared, Status = printer.ReportedStatus });
            }
            _remotePrinter = AvailablePrinters.FirstOrDefault();
            if (_remotePrinter is not null) _ippServer.PrinterName = $"{_remotePrinter.Name} (Remote)";
        });
        OnLog?.Invoke(this, $"[Thành công] Đã được máy chủ chấp nhận, đồng bộ {AvailablePrinters.Count} máy in.");
        OnStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task ResyncCloudPairingAsync()
    {
        if (!_cloud.IsOnline || string.IsNullOrWhiteSpace(_settings.CloudPairingId)) return;

        await _cloudApprovalLock.WaitAsync();
        try
        {
            await ApplyCloudPairingStatusAsync(_settings.CloudPairingId, CancellationToken.None);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _isApproved = false;
            ClearCloudPairingState();
            _connectionStatus = "Phiên kết nối máy chủ không còn tồn tại";
            OnStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"[Cloud] Chưa đồng bộ lại trạng thái ghép nối: {ex.Message}");
        }
        finally
        {
            _cloudApprovalLock.Release();
        }
    }

    private void ClearCloudPairingState()
    {
        _settings.CloudHostId = string.Empty;
        _settings.CloudHostPublicKey = string.Empty;
        _settings.CloudHostPublicKeyFingerprint = string.Empty;
        _settings.CloudPairingCode = string.Empty;
        _settings.CloudPairingExpiresAt = null;
        _settings.CloudPairingSessionId = string.Empty;
        _settings.CloudPairingId = string.Empty;
        ConfigManager.Save(_settings);
        _cloudPrinterIds.Clear();

        void ClearPrinters()
        {
            AvailablePrinters.Clear();
            _remotePrinter = null;
            _ippServer.PrinterName = string.Empty;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.Invoke(ClearPrinters);
        else ClearPrinters();
    }

    private async void Relay_OnConnected(object? sender, EventArgs e)
    {
        _connectionStatus = "Đã kết nối nội bộ. Đang gửi yêu cầu kết nối...";
        OnStateChanged?.Invoke(this, EventArgs.Empty);

        if (!string.IsNullOrWhiteSpace(_settings.PairingCode))
        {
            bool sent = await SendJoinRequestAsync();
            _joinRequestCompletion?.TrySetResult(sent);
        }
    }

    private void Relay_OnDisconnected(object? sender, string reason)
    {
        _connectionStatus = "Mất kết nối máy chủ";
        OnStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> SendJoinRequestAsync()
    {
        if (!PacketSecurity.IsValidPairingCode(_settings.PairingCode))
        {
            OnLog?.Invoke(this, "Không thể gửi yêu cầu: mã kết nối không đúng.");
            return false;
        }
        var device = new DeviceInfo
        {
            DeviceId = _settings.DeviceId,
            DeviceName = _settings.DeviceName,
            OperatingSystem = Environment.OSVersion.ToString(),
            IsOnline = true
        };

        var join = new ClientJoinPayload
        {
            ClientDevice = device,
            PairingCode = _settings.PairingCode
        };

        var packet = new NetworkPacket
        {
            Type = MessageType.ClientJoin,
            SenderId = _settings.DeviceId,
            PairingCode = _settings.PairingCode,
            PayloadJson = JsonSerializer.Serialize(join)
        };

        _connectionStatus = "Đang gửi yêu cầu kết nối...";
        OnStateChanged?.Invoke(this, EventArgs.Empty);

        bool sent = await _relayClient.SendPacketAsync(packet);
        if (!sent)
        {
            _connectionStatus = "Không thể gửi yêu cầu";
            OnStateChanged?.Invoke(this, EventArgs.Empty);
        }

        return sent;
    }

    private void Relay_OnPacketReceived(object? sender, NetworkPacket packet)
    {
        try
        {
            switch (packet.Type)
            {
                case MessageType.ClientJoinResponse:
                {
                    var res = _relayClient.DecryptPayload<ClientJoinResponsePayload>(packet, _settings.PairingCode);
                    if (res != null)
                    {
                        if (res.Approved)
                        {
                            _isApproved = true;
                            _connectionStatus = "Đã được chấp nhận";
                            _settings.EncryptionSecret = res.EncryptionSecret;
                            _remotePrinter = res.AvailablePrinters.FirstOrDefault();

                            Application.Current?.Dispatcher.Invoke(() =>
                            {
                                AvailablePrinters.Clear();
                                if (res.AvailablePrinters != null && res.AvailablePrinters.Count > 0)
                                {
                                    foreach (var p in res.AvailablePrinters)
                                    {
                                        AvailablePrinters.Add(p);
                                    }
                                }
                                else if (_remotePrinter != null) AvailablePrinters.Add(_remotePrinter);
                            });

                            ConfigManager.Save(_settings);

                            if (_remotePrinter != null)
                            {
                                _ippServer.PrinterName = $"{_remotePrinter.Name} (Remote)";
                            }
                            else
                            {
                                _ippServer.PrinterName = string.Empty;
                            }

                            OnLog?.Invoke(this, $"[Thành công] Đã được máy chủ chấp nhận. Nhận danh sách {AvailablePrinters.Count} máy in được phép sử dụng.");
                        }
                        else
                        {
                            _isApproved = false;
                            _connectionStatus = res.IsPending ? "Chờ máy chủ chấp nhận" : "Máy chủ đã từ chối";
                            OnLog?.Invoke(this, $"[Thông báo] {res.Message}");
                        }
                        OnStateChanged?.Invoke(this, EventArgs.Empty);
                    }
                    break;
                }

                case MessageType.PrintersListUpdate:
                {
                    var updatedList = JsonSerializer.Deserialize<PrintersListUpdatePayload>(packet.PayloadJson);
                    if (updatedList?.Printers != null)
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            AvailablePrinters.Clear();
                            foreach (var p in updatedList.Printers)
                            {
                                AvailablePrinters.Add(p);
                            }
                            _remotePrinter = AvailablePrinters.FirstOrDefault();
                            _ippServer.PrinterName = _remotePrinter is null ? string.Empty : $"{_remotePrinter.Name} (Remote)";
                        });
                        OnStateChanged?.Invoke(this, EventArgs.Empty);
                    }
                    break;
                }

                case MessageType.PrinterStatusUpdate:
                {
                    var updated = JsonSerializer.Deserialize<SharedPrinterInfo>(packet.PayloadJson);
                    if (updated != null && updated.IsShared)
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            var permitted = AvailablePrinters.FirstOrDefault(p => string.Equals(p.Name, updated.Name, StringComparison.OrdinalIgnoreCase));
                            if (permitted is null) return;
                            permitted.Status = updated.Status;
                            permitted.PortName = updated.PortName;
                            permitted.IsShared = true;
                            _remotePrinter = permitted;
                        });
                        OnStateChanged?.Invoke(this, EventArgs.Empty);
                    }
                    break;
                }

                case MessageType.PrintJobProgress:
                {
                    var progress = JsonSerializer.Deserialize<PrintJobProgressPayload>(packet.PayloadJson);
                    if (progress != null)
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            var target = PrintHistory.FirstOrDefault(j => j.JobId == progress.JobId);
                            if (target != null)
                            {
                                target.Status = progress.Status;
                                target.StatusMessage = progress.Message;
                                if (progress.Status == PrintJobStatus.Completed)
                                {
                                    target.CompletedAt = DateTime.UtcNow;
                                }
                            }
                        });
                        OnStateChanged?.Invoke(this, EventArgs.Empty);
                    }
                    break;
                }

                case MessageType.PrintStreamAck:
                {
                    var ack = _relayClient.DecryptPayload<PrintStreamAckPayload>(packet, _settings.EncryptionSecret);
                    if (ack != null && !string.IsNullOrWhiteSpace(ack.JobId))
                    {
                        _streamAcks.TryGetValue(StreamAckKey(ack.JobId, ack.Sequence), out var completion);
                        completion?.TrySetResult(ack);
                    }
                    break;
                }

                case MessageType.DeviceRevoked:
                {
                    _isApproved = false;
                    AvailablePrinters.Clear();
                    _remotePrinter = null;
                    _ippServer.PrinterName = string.Empty;
                    _connectionStatus = "Máy chủ đã thu hồi quyền kết nối";
                    OnLog?.Invoke(this, "[Cảnh báo] Máy chủ đã thu hồi quyền kết nối của máy này.");
                    OnStateChanged?.Invoke(this, EventArgs.Empty);
                    break;
                }

                case MessageType.Error:
                {
                    _connectionStatus = "Lỗi kết nối máy chủ";
                    OnLog?.Invoke(this, $"[Lỗi] {packet.PayloadJson}");
                    OnStateChanged?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"[Lỗi nhận tin] {ex.Message}");
        }
    }

    private async void IppServer_OnJobReceived(object? sender, PrintJobReceivedEventArgs e)
    {
        try
        {
            await ProcessIppServerJobAsync(e);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(e.TempFilePath))
            {
                try { File.Delete(e.TempFilePath); } catch { }
            }
        }
    }

    private async Task ProcessIppServerJobAsync(PrintJobReceivedEventArgs e)
    {
        if (!_isApproved)
        {
            OnLog?.Invoke(this, "[Cảnh báo] Máy con chưa được chấp nhận, không thể gửi lệnh in.");
            return;
        }

        var job = e.Job;
        byte[] data = e.Data;
        job.SenderDeviceId = _settings.DeviceId;
        job.SenderDeviceName = _settings.DeviceName;
        job.FileSizeBytes = e.DataLength > 0 ? e.DataLength : data.LongLength;
        job.ContentSha256 = data.Length > 0
            ? RPMCrypto.ComputeSha256(data)
            : await ComputeFileSha256Async(e.TempFilePath!, e.DataOffset, e.DataLength);
        job.DocumentFormat = string.IsNullOrWhiteSpace(job.DocumentFormat) ? "application/octet-stream" : job.DocumentFormat;
        job.Status = PrintJobStatus.Pending;
        job.StatusMessage = "Đang nhận tài liệu...";

        Application.Current?.Dispatcher.Invoke(() =>
        {
            PrintHistory.Insert(0, job);
            _settings.PrintHistory.Insert(0, job);
            if (_settings.PrintHistory.Count > 100)
            {
                _settings.PrintHistory.RemoveAt(_settings.PrintHistory.Count - 1);
            }
            ConfigManager.Save(_settings);
        });

        OnLog?.Invoke(this, $"[Đang gửi] Bắt đầu truyền lệnh in '{job.DocumentName}' tới máy chủ...");

        if (RequiresBackendPrintAuthorization && !_cloud.IsOnline)
        {
            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = "Mã kích hoạt yêu cầu máy chủ từ xa để xác minh quyền và phạm vi trước khi in.";
            OnLog?.Invoke(this, "[Cloud] Từ chối đường truyền nội bộ vì chưa có xác minh quyền lệnh in từ máy chủ từ xa.");
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_settings.EffectiveTransportMode == PrintTransportMode.LegacyCloud && _settings.CloudTransportEnabled && _cloud.IsOnline)
        {
            try
            {
                data = await EnsureDataBytesAsync(data, e.TempFilePath, e.DataOffset, e.DataLength);
                await SubmitCloudJobAsync(job, data, Path.GetExtension(job.DocumentName));
            }
            catch (Exception ex)
            {
                job.Status = PrintJobStatus.Failed;
                job.StatusMessage = "Không thể tải tài liệu lên máy chủ từ xa.";
                OnLog?.Invoke(this, $"[Lỗi Cloud] Gửi tài liệu '{job.DocumentName}' thất bại: {ex.Message}");
            }
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!await EnsureRelayConnectedAsync(TimeSpan.FromSeconds(10)))
        {
            if (_settings.EffectiveTransportMode == PrintTransportMode.LegacyCloud && _settings.CloudTransportEnabled && _cloud.IsOnline)
            {
                try
                {
                    data = await EnsureDataBytesAsync(data, e.TempFilePath, e.DataOffset, e.DataLength);
                    await SubmitCloudJobAsync(job, data, Path.GetExtension(job.DocumentName));
                    OnStateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
                catch (Exception ex)
                {
                    job.Status = PrintJobStatus.Failed;
                    job.StatusMessage = "Không thể truyền tài liệu qua đường trực tiếp hoặc máy chủ từ xa.";
                    OnLog?.Invoke(this, $"[Lỗi fallback] Gửi tài liệu '{job.DocumentName}' thất bại: {ex.Message}");
                    OnStateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = "Không thể kết nối máy chủ nội bộ.";
            OnLog?.Invoke(this, $"[Lỗi LAN] Không thể gửi tài liệu '{job.DocumentName}' vì Relay chưa kết nối.");
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        bool sent = _settings.UsesDirectTransport
            ? e.TempFilePath is not null
                ? await SendDirectPrintJobFromFileAsync(job, e.TempFilePath, e.DataOffset, e.DataLength, Path.GetExtension(job.DocumentName))
                : await SendDirectPrintJobAsync(job, data, Path.GetExtension(job.DocumentName))
            : await SendLegacyRelayPrintJobAsync(job, await EnsureDataBytesAsync(data, e.TempFilePath, e.DataOffset, e.DataLength));

        if (!sent)
        {
            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = "Không thể gửi dữ liệu tới máy chủ.";
            OnLog?.Invoke(this, $"[Lỗi] Gửi tài liệu '{job.DocumentName}' thất bại!");
        }

        OnStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<byte[]> EnsureDataBytesAsync(byte[] data, string? tempFilePath, long offset, long length)
    {
        if (data.Length > 0) return data;
        if (string.IsNullOrWhiteSpace(tempFilePath)) return Array.Empty<byte>();
        if (length <= 0 || length > PacketSecurity.MaxPrintJobBytes)
            throw new InvalidDataException("Tài liệu vượt giới hạn bộ nhớ của đường Cloud cũ.");
        byte[] result = new byte[(int)length];
        await using var stream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        stream.Position = offset;
        int total = 0;
        while (total < result.Length)
        {
            int read = await stream.ReadAsync(result.AsMemory(total, result.Length - total));
            if (read == 0) break;
            total += read;
        }
        if (total != result.Length) throw new InvalidDataException("Không đọc đủ dữ liệu tài liệu in.");
        return result;
    }

    private static async Task<string> ComputeFileSha256Async(string path, long offset, long length)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        stream.Position = offset;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0) throw new InvalidDataException("Không đọc đủ dữ liệu tài liệu in.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<bool> SendLegacyRelayPrintJobAsync(PrintJob job, byte[] data)
    {
        var authorization = await IssueBackendPrintAuthorizationAsync(job);
        if (authorization is null)
            return false;
        var payload = new PrintJobPayload
        {
            JobInfo = job,
            DataBase64 = Convert.ToBase64String(data),
            FileExtension = Path.GetExtension(job.DocumentName),
            AuthorizationId = authorization.Value.AuthorizationId,
            AuthorizationToken = authorization.Value.AuthorizationToken,
            AuthorizationPrinterId = authorization.Value.PrinterId,
        };

        return await _relayClient.SendEncryptedPayloadAsync(
            MessageType.SubmitPrintJob,
            _settings.DeviceId,
            string.Empty,
            _settings.PairingCode,
            payload,
            _settings.EncryptionSecret);
    }

    private async Task<bool> SendDirectPrintJobAsync(PrintJob job, byte[] data, string fileExtension)
    {
        if (data.LongLength > Math.Min(_settings.DirectPrintMaxBytes, PacketSecurity.MaxDirectPrintJobBytes))
        {
            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = "Tài liệu vượt quá giới hạn truyền trực tiếp.";
            return false;
        }

        string digest = RPMCrypto.ComputeSha256(data);
        await using var source = new MemoryStream(data, writable: false);
        return await SendDirectPrintStreamAsync(job, source, data.LongLength, digest, fileExtension);
    }

    private async Task<bool> SendDirectPrintJobFromFileAsync(
        PrintJob job,
        string path,
        long offset,
        long length,
        string fileExtension)
    {
        long maximum = Math.Min(_settings.DirectPrintMaxBytes, PacketSecurity.MaxDirectPrintJobBytes);
        if (length <= 0 || length > maximum)
        {
            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = "Tài liệu vượt quá giới hạn truyền trực tiếp.";
            return false;
        }

        string digest = await ComputeFileSha256Async(path, offset, length);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        source.Position = offset;
        return await SendDirectPrintStreamAsync(job, source, length, digest, fileExtension);
    }

    private async Task<bool> SendDirectPrintStreamAsync(
        PrintJob job,
        Stream source,
        long totalBytes,
        string digest,
        string fileExtension)
    {
        var authorization = await IssueBackendPrintAuthorizationAsync(job);
        if (authorization is null)
            return false;

        int chunkBytes = Math.Clamp(_settings.DirectPrintChunkBytes, 16 * 1024, 512 * 1024);
        var start = new PrintStreamStartPayload
        {
            JobId = job.JobId,
            JobInfo = job,
            FileExtension = fileExtension,
            TotalBytes = totalBytes,
            ContentSha256 = digest,
            ChunkBytes = chunkBytes,
            AuthorizationId = authorization.Value.AuthorizationId,
            AuthorizationToken = authorization.Value.AuthorizationToken,
            AuthorizationPrinterId = authorization.Value.PrinterId,
        };

        job.StatusMessage = "Đang truyền tài liệu trực tiếp...";
        if (!await SendStreamPacketAndWaitAckAsync(MessageType.PrintStreamStart, start, job.JobId, -1))
            return false;

        await foreach (var chunk in RPM.Core.Printing.PrintStreamChunker.ReadAsync(source, chunkBytes))
        {
            var payload = new PrintStreamChunkPayload
            {
                JobId = job.JobId,
                Sequence = chunk.Sequence,
                Offset = chunk.Offset,
                DataBase64 = Convert.ToBase64String(chunk.Data)
            };
            if (!await SendStreamPacketAndWaitAckAsync(MessageType.PrintStreamChunk, payload, job.JobId, chunk.Sequence))
                return false;
        }

        bool completed = await SendStreamPacketAndWaitAckAsync(
            MessageType.PrintStreamEnd,
            new PrintStreamEndPayload { JobId = job.JobId, TotalBytes = totalBytes, ContentSha256 = digest },
            job.JobId,
            -2);
        if (completed) job.StatusMessage = "Đã gửi đến máy chủ, đang chờ máy in.";
        return completed;
    }

    private async Task<bool> SendStreamPacketAndWaitAckAsync(MessageType type, object payload, string jobId, long sequence)
    {
        string key = StreamAckKey(jobId, sequence);
        var completion = new TaskCompletionSource<PrintStreamAckPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _streamAcks[key] = completion;
        try
        {
            bool sent = await _relayClient.SendEncryptedPayloadAsync(
                type,
                _settings.DeviceId,
                string.Empty,
                _settings.PairingCode,
                payload,
                _settings.EncryptionSecret);
            if (!sent) return false;

            var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (finished != completion.Task)
            {
                OnLog?.Invoke(this, $"[Direct] Hết thời gian chờ xác nhận Job {jobId} chunk {sequence}.");
                return false;
            }

            var ack = await completion.Task;
            if (!string.Equals(ack.Status, "RECEIVED", StringComparison.OrdinalIgnoreCase))
            {
                OnLog?.Invoke(this, $"[Direct] Host từ chối Job {jobId}: {ack.Message}");
                return false;
            }
            return true;
        }
        finally
        {
            _streamAcks.TryRemove(key, out _);
        }
    }

    private static string StreamAckKey(string jobId, long sequence) => $"{jobId}:{sequence}";

    private async Task<(string AuthorizationId, string AuthorizationToken, string PrinterId)?> IssueBackendPrintAuthorizationAsync(PrintJob job)
    {
        if (!RequiresBackendPrintAuthorization)
            return (string.Empty, string.Empty, string.Empty);
        if (!_cloud.IsOnline || _cloud.Client is null || string.IsNullOrWhiteSpace(_settings.CloudHostId))
        {
            OnLog?.Invoke(this, "[Cloud] Không thể cấp quyền lệnh in khi máy chủ từ xa đang ngoại tuyến.");
            return null;
        }

        string printerName = job.TargetPrinter;
        if (string.IsNullOrWhiteSpace(printerName))
            printerName = AvailablePrinters.FirstOrDefault(p => p.IsDefault)?.Name ?? AvailablePrinters.FirstOrDefault()?.Name ?? string.Empty;
        if (!_cloudPrinterIds.TryGetValue(printerName, out var printerId))
        {
            OnLog?.Invoke(this, "[Cloud] Không xác định được máy in đã được cấp quyền để cấp quyền lệnh in.");
            return null;
        }

        var location = await _locationService.TryGetCurrentAsync();
        try
        {
            var grant = await _cloud.Client.IssuePrintAuthorizationAsync(new CloudPrintAuthorizationRequest(
                job.JobId,
                _settings.CloudHostId,
                printerId,
                location?.Latitude,
                location?.Longitude,
                location?.AccuracyMeters,
                location?.LocationTimestamp));
            if (grant is null || string.IsNullOrWhiteSpace(grant.AuthorizationId) || string.IsNullOrWhiteSpace(grant.AuthorizationToken))
            {
                OnLog?.Invoke(this, "[Cloud] Máy chủ từ xa không cấp quyền cho lệnh in.");
                return null;
            }
            return (grant.AuthorizationId, grant.AuthorizationToken, printerId);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"[Cloud] Không thể xác minh phạm vi/quyền in: {ex.Message}");
            return null;
        }
    }

    private async Task SubmitCloudJobAsync(PrintJob job, byte[] data, string fileExtension)
    {
        if (!_cloud.IsOnline || _cloud.Client is null || string.IsNullOrWhiteSpace(_settings.CloudHostId) || string.IsNullOrWhiteSpace(_settings.CloudHostPublicKey))
        {
            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = "Máy chủ chưa sẵn sàng. Vui lòng thử lại sau.";
            return;
        }
        var encrypted = CloudDocumentCrypto.EncryptForHost(data, _settings.CloudHostPublicKey);
        string hash = RPMCrypto.ComputeSha256(encrypted);
        string printerName = AvailablePrinters.FirstOrDefault(p => p.IsDefault)?.Name ?? AvailablePrinters.FirstOrDefault()?.Name ?? string.Empty;
        if (!_cloudPrinterIds.TryGetValue(printerName, out var printerId))
            throw new InvalidOperationException("Không xác định được máy in từ xa đã được cấp quyền.");
        var location = await _locationService.TryGetCurrentAsync();
        var request = new CloudPrintJobRequest(_settings.CloudHostId, printerId, new Dictionary<string, object?>
        {
            ["documentName"] = job.DocumentName, ["fileExtension"] = fileExtension,
            ["documentFormat"] = job.DocumentFormat,
            ["senderDeviceName"] = _settings.DeviceName, ["pageCount"] = job.PageCount,
            ["copies"] = job.Copies, ["contentType"] = "application/octet-stream"
        }, null, hash, encrypted.Length, "application/octet-stream",
            location?.Latitude, location?.Longitude, location?.AccuracyMeters, location?.LocationTimestamp);
        var created = await _cloud.Client.CreateJobAsync(request, job.JobId);
        if (created is null) throw new InvalidOperationException("Máy chủ từ xa không tạo được lệnh in.");
        try
        {
            await _cloud.Client.UploadDocumentAsync(created.JobId, encrypted);
        }
        catch
        {
            try
            {
                await _cloud.Client.CancelJobAsync(created.JobId, "DOCUMENT_UPLOAD_FAILED");
            }
            catch { }
            throw;
        }
        job.StatusMessage = "Đã gửi đến máy chủ, đang chờ xử lý.";
    }

    public async Task<bool> SendTestPrintAsync()
    {
        if (!_isApproved || (!_cloud.IsOnline && string.IsNullOrEmpty(_settings.EncryptionSecret)))
        {
            return false;
        }

        // Create a lightweight test print document
        string testText = $"REMOTE PRINT MANAGER - TEST JOB\nFrom: {_settings.DeviceName}\nDate: {DateTime.Now}\nPairing: {_settings.PairingCode}\n";
        byte[] testBytes = System.Text.Encoding.UTF8.GetBytes(testText);

        var job = new PrintJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            DocumentName = $"Test_Print_{DateTime.Now:HHmmss}.txt",
            SenderDeviceId = _settings.DeviceId,
            SenderDeviceName = _settings.DeviceName,
            ContentSha256 = RPMCrypto.ComputeSha256(testBytes),
            FileSizeBytes = testBytes.Length,
            SubmittedAt = DateTime.UtcNow,
            Status = PrintJobStatus.Pending,
            StatusMessage = "Đang gửi trang thử nghiệm...",
            DocumentFormat = "application/octet-stream"
        };

        Application.Current?.Dispatcher.Invoke(() =>
        {
            PrintHistory.Insert(0, job);
        });

        if (_settings.EffectiveTransportMode == PrintTransportMode.LegacyCloud && _settings.CloudTransportEnabled && _cloud.IsOnline)
        {
            try
            {
                await SubmitCloudJobAsync(job, testBytes, ".txt");
            }
            catch (Exception ex)
            {
                job.Status = PrintJobStatus.Failed;
                job.StatusMessage = "Không thể tải trang thử nghiệm lên máy chủ từ xa.";
                OnLog?.Invoke(this, $"[Lỗi Cloud] Gửi trang thử nghiệm thất bại: {ex.Message}");
            }
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return job.Status != PrintJobStatus.Failed;
        }

        if (!await EnsureRelayConnectedAsync(TimeSpan.FromSeconds(10)))
        {
            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = "Không thể kết nối máy chủ nội bộ.";
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        var payload = new PrintJobPayload
        {
            JobInfo = job,
            DataBase64 = Convert.ToBase64String(testBytes),
            FileExtension = ".txt"
        };

        bool sent = await _relayClient.SendEncryptedPayloadAsync(
            MessageType.SubmitPrintJob,
            _settings.DeviceId,
            string.Empty,
            _settings.PairingCode,
            payload,
            _settings.EncryptionSecret
        );
        if (!sent)
        {
            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = "Không thể gửi dữ liệu tới máy chủ nội bộ.";
            OnStateChanged?.Invoke(this, EventArgs.Empty);
        }
        return sent;
    }

    public async Task DisconnectAsync()
    {
        _connectionStatus = "Đã ngắt kết nối";
        _isApproved = false;
        _remotePrinter = null;
        await _relayClient.StopAsync();
        OnStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearCompletedHistory()
    {
        var completed = PrintHistory.Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed).ToList();
        foreach (var job in completed)
        {
            PrintHistory.Remove(job);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        try { await _relayClient.StopAsync(); } catch { }
        _relayClient.Dispose();
        _ippServer.Dispose();
        _cloudApprovalLock.Dispose();
        await _cloud.DisposeAsync();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

