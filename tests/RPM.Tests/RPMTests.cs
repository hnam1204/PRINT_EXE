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
}
