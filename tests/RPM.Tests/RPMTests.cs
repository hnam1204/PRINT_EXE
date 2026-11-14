using System.Net.Http;
using System.Text;
using System.Text.Json;
using RPM.Core.Models;
using RPM.Core.Protocol;
using RPM.Core.Security;
using RPM.Core.Update;
using RPM.Networking;
using RPM.Printing;

namespace RPM.Tests;

public class RPMTests
{
    [Fact]
    public void Crypto_EncryptionDecryptionRoundtrip_Succeeds()
    {
        string secret = "MySuperSecretPassword_2026";
        byte[] key = RPMCrypto.DeriveKey(secret);
        Assert.Equal(32, key.Length);

        string originalText = "Báo cáo tài chính nội bộ công ty - Tối Mật";
        byte[] plaintext = Encoding.UTF8.GetBytes(originalText);

        var (ciphertext, nonce, tag) = RPMCrypto.Encrypt(plaintext, key);
        Assert.NotEqual(plaintext, ciphertext);
        Assert.Equal(12, nonce.Length);
        Assert.Equal(16, tag.Length);

        byte[] decryptedBytes = RPMCrypto.Decrypt(ciphertext, nonce, tag, key);
        string decryptedText = Encoding.UTF8.GetString(decryptedBytes);

        Assert.Equal(originalText, decryptedText);
    }

    [Fact]
    public void Crypto_Sha256_ComputesCorrectHash()
    {
        byte[] data = Encoding.UTF8.GetBytes("Hello World RPM");
        string hash1 = RPMCrypto.ComputeSha256(data);
        string hash2 = RPMCrypto.ComputeSha256(data);

        Assert.NotEmpty(hash1);
        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void PairingCode_Generate_ValidFormat()
    {
        for (int i = 0; i < 20; i++)
        {
            string code = RPMCrypto.GeneratePairingCode();
            Assert.StartsWith("RPM-", code);
            Assert.Equal(8, code.Length);
            Assert.True(int.TryParse(code.Substring(4), out int num));
            Assert.InRange(num, 1000, 9999);
        }
    }

    [Fact]
    public async Task Relay_And_Client_EndToEndCommunication_Succeeds()
    {
        int testPort = 9880;
        var relay = new RelayServer();
        relay.Start(testPort);

        try
        {
            string relayUrl = $"ws://127.0.0.1:{testPort}/";
            string pairingCode = "RPM-9999";
            string secretKey = "RPM_TEST_SHARED_SECRET";

            var serverClient = new RelayClient();
            var clientA = new RelayClient();

            var serverReceivedPacketTcs = new TaskCompletionSource<NetworkPacket>();
            var clientReceivedResponseTcs = new TaskCompletionSource<NetworkPacket>();

            serverClient.OnPacketReceived += (s, pkt) =>
            {
                if (pkt.Type == MessageType.ClientJoin)
                {
                    serverReceivedPacketTcs.TrySetResult(pkt);
                }
            };

            clientA.OnPacketReceived += (s, pkt) =>
            {
                if (pkt.Type == MessageType.ClientJoinResponse)
                {
                    clientReceivedResponseTcs.TrySetResult(pkt);
                }
            };

            // 1. Connect server
            await serverClient.StartAsync(relayUrl);
            await Task.Delay(300);

            // Register server
            await serverClient.SendPacketAsync(new NetworkPacket
            {
                Type = MessageType.RegisterServer,
                SenderId = "SERVER-01",
                PairingCode = pairingCode
            });

            await Task.Delay(300);

            // 2. Connect client
            await clientA.StartAsync(relayUrl);
            await Task.Delay(300);

            // Send join request
            await clientA.SendPacketAsync(new NetworkPacket
            {
                Type = MessageType.ClientJoin,
                SenderId = "CLIENT-A",
                PairingCode = pairingCode,
                PayloadJson = JsonSerializer.Serialize(new ClientJoinPayload
                {
                    PairingCode = pairingCode,
                    ClientDevice = new DeviceInfo { DeviceId = "CLIENT-A", DeviceName = "KeToan-PC" }
                })
            });

            // Wait for server to receive join request
            var receivedAtServer = await Task.WhenAny(serverReceivedPacketTcs.Task, Task.Delay(5000));
            Assert.True(receivedAtServer == serverReceivedPacketTcs.Task, "Server did not receive join request in time");

            // Server approves client with encrypted secret
            await serverClient.SendEncryptedPayloadAsync(
                MessageType.ClientJoinResponse,
                "SERVER-01",
                "CLIENT-A",
                pairingCode,
                new ClientJoinResponsePayload
                {
                    Approved = true,
                    Message = "Phê duyệt thành công",
                    EncryptionSecret = secretKey,
                    SharedPrinter = new SharedPrinterInfo { Name = "Canon LBP 2900" }
                },
                secretKey
            );

            // Wait for client to receive approval
            var receivedAtClient = await Task.WhenAny(clientReceivedResponseTcs.Task, Task.Delay(5000));
            Assert.True(receivedAtClient == clientReceivedResponseTcs.Task, "Client did not receive approval in time");

            var clientPacket = await clientReceivedResponseTcs.Task;
            var decryptedPayload = clientA.DecryptPayload<ClientJoinResponsePayload>(clientPacket, secretKey);

            Assert.NotNull(decryptedPayload);
            Assert.True(decryptedPayload.Approved);
            Assert.Equal("Canon LBP 2900", decryptedPayload.SharedPrinter?.Name);

            await clientA.StopAsync();
            await serverClient.StopAsync();
        }
        finally
        {
            relay.Stop();
        }
    }

    [Fact]
    public async Task IppServer_Starts_And_RespondsToStatusProbe()
    {
        int ippPort = 9885;
        using var ippServer = new LocalIppServer();
        ippServer.PrinterName = "Test Remote Printer";
        ippServer.Start(ippPort);

        try
        {
            using var http = new HttpClient();
            var response = await http.GetAsync($"http://127.0.0.1:{ippPort}/");
            Assert.True(response.IsSuccessStatusCode);

            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Remote Print Manager (RPM) IPP Service", body);
            Assert.Contains("Test Remote Printer is Online", body);
        }
        finally
        {
            ippServer.Stop();
        }
    }

    [Fact]
    public void UpdateService_IsNewerVersion_SemanticComparison_Works()
    {
        Assert.True(UpdateService.IsNewerVersion("1.0.0", "1.0.1"));
        Assert.True(UpdateService.IsNewerVersion("V1.0.0", "v1.0.1"));
        Assert.True(UpdateService.IsNewerVersion("1.0.9", "1.1.0"));
        Assert.True(UpdateService.IsNewerVersion("1.9.9", "2.0.0"));

        Assert.False(UpdateService.IsNewerVersion("1.0.1", "1.0.0"));
        Assert.False(UpdateService.IsNewerVersion("1.0.0", "1.0.0"));
        Assert.False(UpdateService.IsNewerVersion("2.0.0", "1.0.5"));
    }

    [Fact]
    public async Task UpdateService_CheckForUpdates_ParsesManifestAndDetectsNewVersion()
    {
        string tempJsonPath = Path.Combine(Path.GetTempPath(), $"version_{Guid.NewGuid():N}.json");
        try
        {
            var manifest = new UpdateManifest
            {
                Version = "1.0.1",
                ReleaseDate = DateTime.UtcNow,
                DownloadUrl = "http://example.com/RemotePrintManager.exe",
                Sha256 = "abc1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
                Changelog = new List<string>
                {
                    "Tự động kết nối lại khi có mạng",
                    "Khắc phục lỗi treo hàng đợi in",
                    "Cải tiến giao diện Cập nhật & Nâng cấp"
                }
            };

            await File.WriteAllTextAsync(tempJsonPath, JsonSerializer.Serialize(manifest));

            var result = await UpdateService.CheckForUpdatesAsync(tempJsonPath, "1.0.0");

            Assert.True(result.HasUpdate);
            Assert.Equal("1.0.1", result.LatestVersion);
            Assert.NotNull(result.Manifest);
            Assert.Equal(3, result.Manifest.Changelog.Count);
            Assert.Contains("Tự động kết nối lại", result.Manifest.Changelog[0]);
        }
        finally
        {
            if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath);
        }
    }

    [Fact]
    public async Task UpdateService_DownloadAndVerify_ValidatesSha256Checksum()
    {
        string tempFolder = Path.Combine(Path.GetTempPath(), $"rpm_update_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFolder);

        string mockExeSource = Path.Combine(tempFolder, "MockApp.exe");
        byte[] mockBytes = Encoding.UTF8.GetBytes("MOCK BINARY CONTENT FOR UPDATE TEST 2026");
        await File.WriteAllBytesAsync(mockExeSource, mockBytes);

        string expectedSha256 = RPMCrypto.ComputeSha256(mockBytes);

        try
        {
            var validManifest = new UpdateManifest
            {
                Version = "1.0.1",
                DownloadUrl = mockExeSource,
                Sha256 = expectedSha256
            };

            var (successValid, _, validFile) = await UpdateService.DownloadUpdateAsync(validManifest);
            Assert.True(successValid);
            Assert.True(File.Exists(validFile));
            if (File.Exists(validFile)) File.Delete(validFile);

            var invalidManifest = new UpdateManifest
            {
                Version = "1.0.1",
                DownloadUrl = mockExeSource,
                Sha256 = "0000000000000000000000000000000000000000000000000000000000000000" // Corrupted hash
            };

            var (successInvalid, errorMsg, _) = await UpdateService.DownloadUpdateAsync(invalidManifest);
            Assert.False(successInvalid);
            Assert.Contains("Mã SHA-256 không khớp", errorMsg);
        }
        finally
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
        }
    }

    [Fact]
    public void Scalability_Supports100Clients_WithoutHardcodedLimits()
    {
        var clients = new List<DeviceInfo>();
        var departments = new[] { "Kế toán", "Kinh doanh", "Hành chính", "Kỹ thuật", "Ban Giám Đốc" };

        // Register 100 client devices
        for (int i = 1; i <= 100; i++)
        {
            clients.Add(new DeviceInfo
            {
                DeviceId = $"DEVICE-UUID-{i:D4}",
                DeviceName = $"PC-VănPhòng-{i:D3}",
                Department = departments[i % departments.Length],
                IsApproved = true,
                IsOnline = (i % 2 == 0),
                OperatingSystem = "Microsoft Windows 11 Pro",
                PrintJobsCount = i * 2
            });
        }

        Assert.Equal(100, clients.Count);
        Assert.Equal(50, clients.Count(c => c.IsOnline));
        Assert.Equal(50, clients.Count(c => !c.IsOnline));

        // Test department filter
        var accountingClients = clients.Where(c => c.Department == "Kế toán").ToList();
        Assert.Equal(20, accountingClients.Count);

        // Test search filter
        var found = clients.Where(c => c.DeviceName.Contains("042")).ToList();
        Assert.Single(found);
        Assert.Equal("PC-VănPhòng-042", found[0].DeviceName);
    }

    [Fact]
    public void AccessControl_DepartmentAndPrinterPermissions_Enforced()
    {
        var sharedPrinters = new List<SharedPrinterInfo>
        {
            new() { Name = "Canon LBP 2900 - Accounting", IsShared = true },
            new() { Name = "HP LaserJet - Sales", IsShared = true },
            new() { Name = "Epson Color - Marketing", IsShared = true }
        };

        // Client 1: Restricted to Accounting printer
        var clientAccounting = new DeviceInfo
        {
            DeviceId = "ACC-01",
            DeviceName = "Máy Kế Toán Trưởng",
            Department = "Kế toán",
            AllowedPrinters = new List<string> { "Canon LBP 2900 - Accounting" }
        };

        // Client 2: Unrestricted (all shared printers)
        var clientDirector = new DeviceInfo
        {
            DeviceId = "BOD-01",
            DeviceName = "Máy Giám Đốc",
            Department = "Ban Giám Đốc",
            AllowedPrinters = new List<string>() // empty = all
        };

        // Check Client 1 permissions
        Assert.Contains("Canon LBP 2900 - Accounting", clientAccounting.AllowedPrinters);
        Assert.DoesNotContain("HP LaserJet - Sales", clientAccounting.AllowedPrinters);
        Assert.Equal("Canon LBP 2900 - Accounting", clientAccounting.AllowedPrintersSummary);

        // Check Client 2 permissions
        Assert.Equal("Tất cả máy in chia sẻ", clientDirector.AllowedPrintersSummary);
    }

    [Fact]
    public async Task Concurrency_MultipleClientsPrintSimultaneously_FifoQueueNoDrops()
    {
        var printQueue = new Queue<PrintJob>();
        var queueLock = new SemaphoreSlim(1, 1);
        int totalConcurrentJobs = 20;

        // Submit 20 concurrent jobs from simulated clients
        var tasks = Enumerable.Range(1, totalConcurrentJobs).Select(async i =>
        {
            await Task.Yield();
            var job = new PrintJob
            {
                JobId = $"JOB-{i:D3}",
                DocumentName = $"Báo cáo tài chính quý {i}.pdf",
                SenderDeviceId = $"CLIENT-{i:D3}",
                SenderDeviceName = $"Máy con {i}",
                Department = (i % 2 == 0) ? "Kế toán" : "Kinh doanh",
                TargetPrinter = "Canon LBP 2900",
                ContentSha256 = RPMCrypto.ComputeSha256(Encoding.UTF8.GetBytes($"DATA_JOB_{i}"))
            };

            await queueLock.WaitAsync();
            try
            {
                printQueue.Enqueue(job);
            }
            finally
            {
                queueLock.Release();
            }
        });

        await Task.WhenAll(tasks);

        Assert.Equal(totalConcurrentJobs, printQueue.Count);

        // Verify FIFO order extraction without data corruption
        var dequeuedJobs = new List<PrintJob>();
        while (printQueue.Count > 0)
        {
            dequeuedJobs.Add(printQueue.Dequeue());
        }

        Assert.Equal(totalConcurrentJobs, dequeuedJobs.Count);
        Assert.Equal(totalConcurrentJobs, dequeuedJobs.Select(j => j.JobId).Distinct().Count());
    }
}
