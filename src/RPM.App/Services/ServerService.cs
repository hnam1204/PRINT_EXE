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

public class ServerService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly RelayClient _relayClient = new();
    private readonly PrinterService _printerService = new();
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

    public event EventHandler<string>? OnLog;
    public event EventHandler<DeviceInfo>? OnClientRequestedApproval;
    public event EventHandler? OnStatusChanged;

    public ServerService(AppSettings settings)
    {
        _settings = settings;

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
        if (!PacketSecurity.IsValidPairingCode(_settings.PairingCode))
        {
            _settings.PairingCode = Core.Security.RPMCrypto.GeneratePairingCode();
            ConfigManager.Save(_settings);
        }

        _relayClient.PairingCode = _settings.PairingCode;

        if (string.IsNullOrWhiteSpace(_settings.EncryptionSecret))
        {
            _settings.EncryptionSecret = Guid.NewGuid().ToString("N");
            ConfigManager.Save(_settings);
        }

        // Load and detect all installed printers and shared configuration
        LoadPrinters();

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

    public void Dispose()
    {
        _relayClient.Dispose();
        _queueLock.Dispose();
        _embeddedRelay?.Stop();
    }
}
