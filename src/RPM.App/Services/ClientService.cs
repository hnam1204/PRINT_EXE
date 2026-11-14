using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using RPM.Core.Config;
using RPM.Core.Models;
using RPM.Core.Protocol;
using RPM.Core.Security;
using RPM.Networking;
using RPM.Printing;

namespace RPM.App.Services;

public class ClientService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly RelayClient _relayClient = new();
    private readonly LocalIppServer _ippServer = new();
    private bool _isApproved;
    private string _connectionStatus = "Chưa kết nối";
    private SharedPrinterInfo? _remotePrinter;

    public RelayClient Relay => _relayClient;
    public LocalIppServer IppServer => _ippServer;

    public ObservableCollection<PrintJob> PrintHistory { get; } = new();
    public ObservableCollection<SharedPrinterInfo> AvailablePrinters { get; } = new();
    public SharedPrinterInfo? RemotePrinter => _remotePrinter;
    public bool IsApproved => _isApproved;
    public string ConnectionStatus => _connectionStatus;

    public event EventHandler<string>? OnLog;
    public event EventHandler? OnStateChanged;

    public ClientService(AppSettings settings)
    {
        _settings = settings;

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
        _connectionStatus = "Đang kết nối...";
        OnStateChanged?.Invoke(this, EventArgs.Empty);

        // Start local IPP service
        _ippServer.Start(_settings.LocalIppPort);

        // Connect to relay
        await _relayClient.StartAsync(_settings.RelayServerUrl);
    }

    public async Task ConnectToServerAsync(string pairingCode)
    {
        _settings.PairingCode = pairingCode.Trim();
        ConfigManager.Save(_settings);

        if (_relayClient.IsConnected)
        {
            await SendJoinRequestAsync();
        }
        else
        {
            await _relayClient.StartAsync(_settings.RelayServerUrl);
        }
    }

    private async void Relay_OnConnected(object? sender, EventArgs e)
    {
        _connectionStatus = "Đã kết nối Relay. Đang ghép đôi...";
        OnStateChanged?.Invoke(this, EventArgs.Empty);

        if (!string.IsNullOrWhiteSpace(_settings.PairingCode))
        {
            await SendJoinRequestAsync();
        }
    }

    private void Relay_OnDisconnected(object? sender, string reason)
    {
        _connectionStatus = "Mất kết nối máy chủ";
        OnStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SendJoinRequestAsync()
    {
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

        _connectionStatus = "Đã gửi yêu cầu ghép đôi, chờ duyệt...";
        OnStateChanged?.Invoke(this, EventArgs.Empty);

        await _relayClient.SendPacketAsync(packet);
    }

    private void Relay_OnPacketReceived(object? sender, NetworkPacket packet)
    {
        try
        {
            switch (packet.Type)
            {
                case MessageType.ClientJoinResponse:
                {
                    var res = JsonSerializer.Deserialize<ClientJoinResponsePayload>(packet.PayloadJson);
                    if (res != null)
                    {
                        if (res.Approved)
                        {
                            _isApproved = true;
                            _connectionStatus = "Đã kết nối & Phê duyệt thành công";
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

                            OnLog?.Invoke(this, $"[Thành công] Đã được máy chủ phê duyệt! Nhận danh sách {AvailablePrinters.Count} máy in được cấp quyền.");
                        }
                        else
                        {
                            _isApproved = false;
                            _connectionStatus = res.IsPending ? "Đang chờ máy chủ duyệt..." : "Bị từ chối kết nối.";
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
                    _connectionStatus = "Quyền sử dụng máy in đã bị thu hồi bởi máy chủ.";
                    OnLog?.Invoke(this, "[Cảnh báo] Máy chủ đã thu hồi quyền in của thiết bị này.");
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
            OnLog?.Invoke(this, "[Cảnh báo] Máy con chưa được phê duyệt, không thể gửi lệnh in!");
            return;
        }

        var job = e.Job;
        job.SenderDeviceId = _settings.DeviceId;
        job.SenderDeviceName = _settings.DeviceName;
        job.ContentSha256 = RPMCrypto.ComputeSha256(e.Data);
        job.Status = PrintJobStatus.Pending;
        job.StatusMessage = "Đang truyền tải qua mạng Internet...";

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

    public async Task<bool> SendTestPrintAsync()
    {
        if (!_isApproved || string.IsNullOrEmpty(_settings.EncryptionSecret))
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
            StatusMessage = "Đang gửi trang thử nghiệm..."
        };

        Application.Current?.Dispatcher.Invoke(() =>
        {
            PrintHistory.Insert(0, job);
        });

        var payload = new PrintJobPayload
        {
            JobInfo = job,
            DataBase64 = Convert.ToBase64String(testBytes),
            FileExtension = ".txt"
        };

        return await _relayClient.SendEncryptedPayloadAsync(
            MessageType.SubmitPrintJob,
            _settings.DeviceId,
            string.Empty,
            _settings.PairingCode,
            payload,
            _settings.EncryptionSecret
        );
    }

    public void Dispose()
    {
        _relayClient.Dispose();
        _ippServer.Dispose();
    }
}

