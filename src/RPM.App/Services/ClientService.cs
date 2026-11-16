using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using RPM.Core.Config;
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
    private int _isDisposed;

    public RelayClient Relay => _relayClient;
    public LocalIppServer IppServer => _ippServer;

    public ObservableCollection<PrintJob> PrintHistory { get; } = new();
    public ObservableCollection<SharedPrinterInfo> AvailablePrinters { get; } = new();
    public SharedPrinterInfo? RemotePrinter => _remotePrinter;
    public bool IsApproved => _isApproved;
    public bool IsCloudOnline => _cloud.IsOnline;
    public bool HasStoredCloudToken => _cloud.HasStoredToken;
    public CloudActivationInfo? CloudActivationInfo => _cloud.ActivationInfo;
    public bool IsOnline => _cloud.IsOnline || _relayClient.IsConnected;
    public bool IsConnecting { get; private set; }
    public string ConnectionStatus => _connectionStatus;
    public string ConnectionStatusDisplay => _cloud.IsOnline
        ? "Đã kết nối dịch vụ"
        : _relayClient.IsConnected ? "Đã kết nối nội bộ" : "Mất kết nối dịch vụ";
    public string GeofenceStatusDisplay { get; private set; } = "Chưa xác minh vị trí";

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
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            if (!_cloud.IsOnline) _ = StartRelayFallbackAsync();
        };
        _cloud.OnEnrollmentRevoked += (s, e) => OnEnrollmentRevoked?.Invoke(this, EventArgs.Empty);

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

    public async Task StartAsync()
    {
        _ippServer.Start(_settings.LocalIppPort);
        if (_settings.CloudTransportEnabled || _cloud.HasStoredToken)
        {
            await _cloud.StartAsync();
            if (!_cloud.IsOnline) await StartRelayFallbackAsync();
            return;
        }
        await StartRelayFallbackAsync();
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
            if (!RelayEndpointPolicy.TryValidateForRole(AppRole.Client, _settings.RelayServerUrl, out var reason))
            {
                _connectionStatus = "Chưa kết nối nội bộ";
                OnLog?.Invoke(this, reason + " Hãy thiết lập địa chỉ LAN của máy chủ trong Chẩn đoán.");
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

        if (!RelayEndpointPolicy.TryValidateForRole(AppRole.Client, _settings.RelayServerUrl, out var reason))
        {
            _connectionStatus = "Chưa kết nối";
            _joinRequestCompletion = null;
            OnLog?.Invoke(this, reason + " Hãy thiết lập địa chỉ Host/kết nối nội bộ trong Chẩn đoán.");
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
                await Task.Delay(TimeSpan.FromSeconds(2));
                var api = _cloud.Client;
                if (api is null) return;
                var pairing = await api.GetPairingAsync(pairingId);
                if (pairing is null) continue;
                if (string.Equals(pairing.Status, "APPROVED", StringComparison.OrdinalIgnoreCase))
                {
                    var host = await api.GetDeviceProfileAsync(_settings.CloudHostId);
                    if (host is null || host.IsRevoked || string.IsNullOrWhiteSpace(host.PublicKey))
                    {
                        _connectionStatus = "Máy chủ chưa cung cấp khóa bảo mật";
                        OnLog?.Invoke(this, "Máy chủ đã chấp nhận nhưng chưa cung cấp public key hợp lệ; chưa thể gửi tài liệu.");
                        OnStateChanged?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    string hostFingerprint = CloudDocumentCrypto.ComputePublicKeyFingerprint(host.PublicKey);
                    if (!string.IsNullOrWhiteSpace(_settings.CloudHostPublicKeyFingerprint) &&
                        !string.Equals(_settings.CloudHostPublicKeyFingerprint, hostFingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        _connectionStatus = "Khóa bảo mật máy chủ đã thay đổi";
                        OnLog?.Invoke(this, "Từ chối ghép nối: public key máy chủ không khớp khóa đã tin cậy trên thiết bị.");
                        OnStateChanged?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    _settings.CloudHostPublicKey = host.PublicKey;
                    _settings.CloudHostPublicKeyFingerprint = hostFingerprint;
                    ConfigManager.Save(_settings);
                    _isApproved = true;
                    _connectionStatus = "Đã được chấp nhận";
                    var printers = await api.ListPrintersAsync(_settings.CloudHostId);
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        AvailablePrinters.Clear();
                        if (printers is not null)
                        {
                            foreach (var printer in printers.Where(p => p.IsShared))
                            {
                                _cloudPrinterIds[printer.DisplayName] = printer.PrinterId;
                                AvailablePrinters.Add(new SharedPrinterInfo { Name = printer.DisplayName, IsShared = printer.IsShared, Status = printer.ReportedStatus });
                            }
                            _remotePrinter = AvailablePrinters.FirstOrDefault();
                            if (_remotePrinter is not null) _ippServer.PrinterName = $"{_remotePrinter.Name} (Remote)";
                        }
                    });
                    OnLog?.Invoke(this, $"[Thành công] Đã được máy chủ chấp nhận, đồng bộ {AvailablePrinters.Count} máy in.");
                    OnStateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
                if (string.Equals(pairing.Status, "REJECTED", StringComparison.OrdinalIgnoreCase))
                {
                    _connectionStatus = "Máy chủ đã từ chối";
                    OnStateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
            catch (Exception ex) { OnLog?.Invoke(this, $"Máy chủ từ xa chưa phản hồi: {ex.Message}"); }
        }
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
                            _remotePrinter = res.SharedPrinter;

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
                                else if (_remotePrinter != null)
                                {
                                    AvailablePrinters.Add(_remotePrinter);
                                }
                            });

                            ConfigManager.Save(_settings);

                            if (_remotePrinter != null)
                            {
                                _ippServer.PrinterName = $"{_remotePrinter.Name} (Remote)";
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
                            if (_remotePrinter != null)
                            {
                                _remotePrinter = AvailablePrinters.FirstOrDefault(p => p.Name == _remotePrinter.Name) ?? AvailablePrinters.FirstOrDefault();
                            }
                            else
                            {
                                _remotePrinter = AvailablePrinters.FirstOrDefault();
                            }
                        });
                        OnStateChanged?.Invoke(this, EventArgs.Empty);
                    }
                    break;
                }

                case MessageType.PrinterStatusUpdate:
                {
                    var updated = JsonSerializer.Deserialize<SharedPrinterInfo>(packet.PayloadJson);
                    if (updated != null)
                    {
                        _remotePrinter = updated;
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

                case MessageType.DeviceRevoked:
                {
                    _isApproved = false;
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
        if (!_isApproved)
        {
            OnLog?.Invoke(this, "[Cảnh báo] Máy con chưa được chấp nhận, không thể gửi lệnh in.");
            return;
        }

        var job = e.Job;
        job.SenderDeviceId = _settings.DeviceId;
        job.SenderDeviceName = _settings.DeviceName;
        job.ContentSha256 = RPMCrypto.ComputeSha256(e.Data);
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

        if (_settings.CloudTransportEnabled && _cloud.IsOnline)
        {
            try
            {
                await SubmitCloudJobAsync(job, e.Data, Path.GetExtension(job.DocumentName));
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
            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = "Không thể kết nối máy chủ nội bộ.";
            OnLog?.Invoke(this, $"[Lỗi LAN] Không thể gửi tài liệu '{job.DocumentName}' vì Relay chưa kết nối.");
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var payload = new PrintJobPayload
        {
            JobInfo = job,
            DataBase64 = Convert.ToBase64String(e.Data)
        };

        // Encrypt with AES-256-GCM End-to-End
        bool sent = await _relayClient.SendEncryptedPayloadAsync(
            MessageType.SubmitPrintJob,
            _settings.DeviceId,
            string.Empty, // to server
            _settings.PairingCode,
            payload,
            _settings.EncryptionSecret
        );

        if (!sent)
        {
            job.Status = PrintJobStatus.Failed;
            job.StatusMessage = "Không thể gửi dữ liệu tới máy chủ.";
            OnLog?.Invoke(this, $"[Lỗi] Gửi tài liệu '{job.DocumentName}' thất bại!");
        }

        OnStateChanged?.Invoke(this, EventArgs.Empty);
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

        if (_settings.CloudTransportEnabled && _cloud.IsOnline)
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
        await _cloud.DisposeAsync();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

