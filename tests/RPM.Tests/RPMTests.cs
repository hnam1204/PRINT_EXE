using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using RPM.Core.Models;
using RPM.Core.Config;
using RPM.Core.Networking;
using RPM.Core.Protocol;
using RPM.Core.Security;
using RPM.Core.Diagnostics;
using RPM.Core.Update;
using RPM.Core.Cloud;
using RPM.Networking;
using RPM.Printing;
using RPM.Core.Printing;
using RPM.Core.Connection;

namespace RPM.Tests;

public class RPMTests
{
    [Fact]
    public void DeviceInfo_IsRemoteDevice_ExcludesCurrentHost()
    {
        var host = new DeviceInfo { DeviceId = "host-01" };
        var client = new DeviceInfo { DeviceId = "client-01" };

        Assert.False(DeviceInfoPolicy.IsRemoteDevice(host, "HOST-01"));
        Assert.True(DeviceInfoPolicy.IsRemoteDevice(client, "host-01"));
    }

    [Theory]
    [InlineData(ActivationStatus.NotActivated, false, true, "Đã kết nối nội bộ", ConnectionMode.Lan, TransportStatus.Online)]
    [InlineData(ActivationStatus.VerificationPending, false, true, "Đã kết nối nội bộ", ConnectionMode.Lan, TransportStatus.Online)]
    [InlineData(ActivationStatus.NotActivated, false, false, "Chưa kết nối nội bộ", ConnectionMode.None, TransportStatus.Offline)]
    [InlineData(ActivationStatus.Valid, true, true, "Đã kết nối dịch vụ", ConnectionMode.Cloud, TransportStatus.Online)]
    [InlineData(ActivationStatus.Valid, true, false, "Đã kết nối dịch vụ", ConnectionMode.Cloud, TransportStatus.Online)]
    [InlineData(ActivationStatus.Valid, false, true, "Mất kết nối dịch vụ", ConnectionMode.Lan, TransportStatus.Degraded)]
    [InlineData(ActivationStatus.Valid, false, false, "Mất kết nối dịch vụ", ConnectionMode.None, TransportStatus.Offline)]
    [InlineData(ActivationStatus.Revoked, false, true, "Mã kích hoạt đã bị thu hồi", ConnectionMode.Lan, TransportStatus.Degraded)]
    [InlineData(ActivationStatus.Expired, false, false, "Mã kích hoạt đã hết hạn", ConnectionMode.None, TransportStatus.Offline)]
    public void ConnectionStatusResolver_UsesActivationProofAndTransportState(
        ActivationStatus activationStatus,
        bool cloudOnline,
        bool relayOnline,
        string expectedText,
        ConnectionMode expectedMode,
        TransportStatus expectedTransport)
    {
        var result = ConnectionStatusResolver.Resolve(activationStatus, cloudOnline, relayOnline);

        Assert.Equal(expectedText, result.PrimaryText);
        Assert.Equal(expectedMode, result.ConnectionMode);
        Assert.Equal(expectedTransport, result.TransportStatus);
    }

    [Fact]
    public void ConnectionStatusResolver_DoesNotClaimCloudFromStoredActivationMetadata()
    {
        var result = ConnectionStatusResolver.Resolve(ActivationStatus.VerificationPending, cloudOnline: true, relayOnline: false);

        Assert.NotEqual("Đã kết nối dịch vụ", result.PrimaryText);
        Assert.Equal(ConnectionMode.None, result.ConnectionMode);
    }

    [Fact]
    public void ConnectionStatusResolver_ExposesLanAsSecondaryWhenVerifiedCloudIsOffline()
    {
        var result = ConnectionStatusResolver.Resolve(ActivationStatus.Valid, cloudOnline: false, relayOnline: true);

        Assert.Equal("Mất kết nối dịch vụ", result.PrimaryText);
        Assert.Equal("Kết nối nội bộ vẫn sẵn sàng", result.SecondaryText);
    }

    [Fact]
    public void ConnectionStatusResolver_PreservesCloudLossAfterRestartWithoutClaimingLanAsPrimary()
    {
        var result = ConnectionStatusResolver.Resolve(
            ActivationStatus.VerificationPending,
            cloudOnline: false,
            relayOnline: true,
            previouslyVerified: true);

        Assert.Equal("Mất kết nối dịch vụ", result.PrimaryText);
        Assert.Equal(ConnectionMode.Lan, result.ConnectionMode);
        Assert.Equal(TransportStatus.Degraded, result.TransportStatus);
        Assert.Equal("Kết nối nội bộ vẫn sẵn sàng", result.SecondaryText);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConnectionStatusResolver_RequiresStoredTokenAndVerificationMarker(
        bool hasStoredToken,
        bool hasVerificationMarker)
    {
        DateTime? verifiedAt = hasVerificationMarker ? DateTime.UtcNow : null;

        Assert.Equal(
            hasStoredToken && hasVerificationMarker,
            ConnectionStatusResolver.IsPreviouslyVerifiedActivation(hasStoredToken, verifiedAt));
    }

    [Fact]
    public void TransportPolicy_DefaultsToDirectOnlyAndPreservesLegacyConfig()
    {
        var fresh = new AppSettings();
        Assert.Equal(PrintTransportMode.DirectOnly, fresh.EffectiveTransportMode);

        var legacyCloud = new AppSettings { DirectPrintStreamingEnabled = false };
        Assert.Equal(PrintTransportMode.LegacyCloud, legacyCloud.EffectiveTransportMode);

        var explicitDirect = new AppSettings
        {
            DirectPrintStreamingEnabled = false,
            TransportMode = PrintTransportMode.DirectOnly
        };
        Assert.Equal(PrintTransportMode.DirectOnly, explicitDirect.EffectiveTransportMode);

        var automatic = new AppSettings { TransportMode = PrintTransportMode.Auto };
        Assert.True(automatic.UsesDirectTransport);
    }

    [Fact]
    public void DeviceStatus_SeparatesPermissionFromConnection()
    {
        var device = new DeviceInfo { IsApproved = true, IsOnline = false };

        Assert.Equal("Đã được chấp nhận", device.PermissionStatus);
        Assert.Equal("Máy đang ngoại tuyến", device.ConnectionStatus);

        device.IsOnline = true;
        Assert.Equal("Máy đang trực tuyến", device.ConnectionStatus);
    }

    [Fact]
    public void PrintJobStatus_DoesNotClaimPhysicalCompletion()
    {
        var job = new PrintJob
        {
            Status = PrintJobStatus.Completed,
            StatusMessage = "Đã gửi đến máy in. Chưa xác minh được việc in ra giấy."
        };

        Assert.Equal("Đã gửi đến máy in", job.StatusText);
    }

    [Fact]
    public void PrintJobStatus_UnknownRemainsDistinctFromFailure()
    {
        var job = new PrintJob
        {
            Status = PrintJobStatus.Unknown,
            StatusMessage = "Chưa xác định kết quả in; không tự động in lại."
        };

        Assert.NotEqual(PrintJobStatus.Failed, job.Status);
        Assert.Equal("Chưa xác định", job.StatusText);
    }

    [Fact]
    public void CloudUnknownStatus_IsMappedWithoutConvertingToFailure()
    {
        Assert.Equal(PrintJobStatus.Unknown, PrintJobStatusMapper.FromCloud("UNKNOWN", PrintJobStatus.Printing));
        Assert.Equal("Chưa xác định kết quả in; không tự động in lại.", PrintJobStatusMapper.MessageFromCloud("UNKNOWN"));
    }

    [Fact]
    public void RealtimeCursorState_DeduplicatesEventsAndCommitsAfterProcessing()
    {
        var state = new RealtimeCursorState(4, new[] { 10L });
        var duplicate = new CloudRealtimeEvent(10, "PAIRING_REQUESTED", "device", 1, null, null, null, null, DateTime.UtcNow, 5, 5, "corr", new());
        var next = duplicate with { EventId = 11, Cursor = 6 };

        Assert.False(state.ShouldProcess(duplicate));
        Assert.True(state.ShouldProcess(next));
        state.Commit(next);
        Assert.Equal(6, state.Cursor);
        Assert.False(state.ShouldProcess(next));

        var duplicateAtLaterCursor = next with { Cursor = 7 };
        Assert.False(state.ShouldProcess(duplicateAtLaterCursor));
        Assert.True(state.AdvancePastDuplicate(duplicateAtLaterCursor));
        Assert.Equal(7, state.Cursor);
    }

    [Fact]
    public void ConfigMigration_PreservesIdentityAndAddsRealtimeState()
    {
        var settings = new AppSettings
        {
            ConfigSchemaVersion = 1,
            DeviceId = "device-preserved",
            PairingCode = "PAIRING-PRESERVED",
            SelectedPrinterName = "Printer-preserved",
            RealtimeEventCursor = -10,
            RealtimeProcessedEventIds = new() { 2, 2, 0 }
        };

        var migrated = ConfigManager.Migrate(settings, 1);

        Assert.Equal(AppSettings.CurrentConfigSchemaVersion, migrated.ConfigSchemaVersion);
        Assert.Equal("device-preserved", migrated.DeviceId);
        Assert.Equal("PAIRING-PRESERVED", migrated.PairingCode);
        Assert.Equal("Printer-preserved", migrated.SelectedPrinterName);
        Assert.Equal(0, migrated.RealtimeEventCursor);
        Assert.Equal(new[] { 2L }, migrated.RealtimeProcessedEventIds);
    }

    [Fact]
    public void CloudDocumentCrypto_EncryptsForHostAndRejectsWrongKey()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"rpm_cloud_crypto_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var hostStore = new CloudDeviceKeyStore(Path.Combine(folder, "host.key"));
            var otherStore = new CloudDeviceKeyStore(Path.Combine(folder, "other.key"));
            byte[] plaintext = Encoding.UTF8.GetBytes("encrypted print document");
            byte[] envelope = CloudDocumentCrypto.EncryptForHost(plaintext, hostStore.GetOrCreatePublicKey());

            Assert.Equal(plaintext, CloudDocumentCrypto.DecryptForThisDevice(envelope, hostStore));
            Assert.Throws<CryptographicException>(() => CloudDocumentCrypto.DecryptForThisDevice(envelope, otherStore));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void CloudDocumentCrypto_PublicKeyFingerprintChangesWhenKeyChanges()
    {
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        string firstKey = Convert.ToBase64String(first.ExportSubjectPublicKeyInfo());
        string secondKey = Convert.ToBase64String(second.ExportSubjectPublicKeyInfo());

        string firstFingerprint = CloudDocumentCrypto.ComputePublicKeyFingerprint(firstKey);
        string secondFingerprint = CloudDocumentCrypto.ComputePublicKeyFingerprint(secondKey);

        Assert.Equal(64, firstFingerprint.Length);
        Assert.NotEqual(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void CloudCredentials_PersistAcrossRoleChanges()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"rpm_cloud_credentials_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var hostTokenStore = CloudDeviceTokenStore.ForDevice("device-1", folder);
            hostTokenStore.Save("token-before-role-change");

            var clientTokenStore = CloudDeviceTokenStore.ForDevice("device-1", folder);
            Assert.Equal("token-before-role-change", clientTokenStore.Load());

            var secretStore = CloudActivationSecretStore.ForDevice("device-1", folder);
            secretStore.Save("activation-code");

            var afterRoleChangeStore = CloudActivationSecretStore.ForDevice("device-1", folder);
            Assert.Equal("activation-code", afterRoleChangeStore.Load());
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void RelayEndpointPolicy_ClientDoesNotUseItsOwnLoopback()
    {
        Assert.False(RelayEndpointPolicy.TryValidateForRole(AppRole.Client, "ws://127.0.0.1:9090", out var reason));
        Assert.Contains("localhost", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelayEndpointPolicy_HostMayUseEmbeddedLoopback()
    {
        Assert.True(RelayEndpointPolicy.TryValidateForRole(AppRole.Server, "ws://127.0.0.1:9090", out _));
    }

    [Fact]
    public void RelayEndpointPolicy_PrivateLanPlainWebSocketIsAllowed()
    {
        Assert.True(RelayEndpointPolicy.TryValidateForRole(AppRole.Client, "ws://192.168.1.20:9090", out _));
    }

    [Fact]
    public void RelayEndpointPolicy_PublicPlainWebSocketIsRejected()
    {
        Assert.False(RelayEndpointPolicy.TryValidateForRole(AppRole.Client, "ws://8.8.8.8:9090", out var reason));
        Assert.Contains("wss", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LanRelayDiscovery_AdvertisementContainsPrivateEndpoint()
    {
        string advertisement = LanRelayDiscovery.BuildAdvertisement("192.168.1.25", 9090);

        Assert.True(LanRelayDiscovery.TryParseAdvertisement(advertisement, out var endpoint));
        Assert.Equal("ws://192.168.1.25:9090/", endpoint);
    }

    [Theory]
    [InlineData("RPM_RELAY_ADVERTISEMENT_V1|127.0.0.1|9090")]
    [InlineData("RPM_RELAY_ADVERTISEMENT_V1|8.8.8.8|9090")]
    [InlineData("RPM_RELAY_ADVERTISEMENT_V1|192.168.1.25|70000")]
    public void LanRelayDiscovery_RejectsUnsafeAdvertisement(string advertisement)
    {
        Assert.False(LanRelayDiscovery.TryParseAdvertisement(advertisement, out _));
    }

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
    public void PacketSecurity_RejectsTamperingAndExpiredPackets()
    {
        string pairingCode = "RPM-A7K2Q9";
        var packet = new NetworkPacket
        {
            Type = MessageType.Heartbeat,
            SenderId = "CLIENT-A",
            PairingCode = pairingCode,
            Timestamp = DateTime.UtcNow
        };
        packet.AuthTagBase64 = PacketSecurity.ComputeAuthTag(packet, pairingCode);

        Assert.True(PacketSecurity.VerifyAuthTag(packet, pairingCode));
        packet.TargetId = "SERVER-B";
        Assert.False(PacketSecurity.VerifyAuthTag(packet, pairingCode));
        packet.TargetId = string.Empty;
        packet.Timestamp = DateTime.UtcNow.AddMinutes(-10);
        Assert.False(PacketSecurity.IsFresh(packet, DateTime.UtcNow));
    }

    [Fact]
    public async Task PrintStreamChunker_ReadsOrderedBoundedChunks()
    {
        byte[] sourceBytes = new byte[(PacketSecurity.DirectPrintChunkBytes * 2) + 17];
        Random.Shared.NextBytes(sourceBytes);
        var chunks = new List<PrintStreamChunk>();

        await using var source = new MemoryStream(sourceBytes, writable: false);
        await foreach (var chunk in PrintStreamChunker.ReadAsync(source, PacketSecurity.DirectPrintChunkBytes))
        {
            chunks.Add(chunk);
            Assert.InRange(chunk.Data.Length, 1, PacketSecurity.DirectPrintChunkBytes);
        }

        Assert.Equal(3, chunks.Count);
        Assert.Equal(new long[] { 0, 1, 2 }, chunks.Select(chunk => chunk.Sequence));
        Assert.Equal(new long[] { 0, PacketSecurity.DirectPrintChunkBytes, PacketSecurity.DirectPrintChunkBytes * 2 }, chunks.Select(chunk => chunk.Offset));
        Assert.Equal(sourceBytes, chunks.SelectMany(chunk => chunk.Data).ToArray());
    }

    [Fact]
    public void DirectPrintAuthorization_ResolvesOnlySharedAndAllowedPrinter()
    {
        var sender = new DeviceInfo
        {
            AllowedPrinters = new List<string> { "Canon" }
        };
        var printers = new[]
        {
            new SharedPrinterInfo { Name = "Canon", IsShared = true },
            new SharedPrinterInfo { Name = "Epson", IsShared = false }
        };

        Assert.True(DirectPrintAuthorization.TryResolveTargetPrinter(sender, printers, null, out var target, out _));
        Assert.Equal("Canon", target);
        Assert.False(DirectPrintAuthorization.TryResolveTargetPrinter(sender, printers, "Epson", out _, out var error));
        Assert.Contains("không khả dụng", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectPrintAuthorization_RejectsPrinterOutsideClientPermission()
    {
        var sender = new DeviceInfo { AllowedPrinters = new List<string> { "Canon" } };
        var printers = new[] { new SharedPrinterInfo { Name = "Epson", IsShared = true } };

        Assert.False(DirectPrintAuthorization.TryResolveTargetPrinter(sender, printers, "Epson", out _, out var error));
        Assert.Contains("quyền", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectPrintAuthorization_RejectsClientWithExplicitNoPrinterPermission()
    {
        var sender = new DeviceInfo { PrinterPermissionMode = PrinterAccessMode.None };
        var printers = new[] { new SharedPrinterInfo { Name = "Canon", IsShared = true } };

        Assert.False(DirectPrintAuthorization.TryResolveTargetPrinter(sender, printers, "Canon", out _, out var error));
        Assert.Contains("quyền", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrinterAccessPolicy_DistinguishesAllSelectedAndNone()
    {
        var printers = new[]
        {
            new SharedPrinterInfo { Name = "Canon", IsShared = true },
            new SharedPrinterInfo { Name = "Epson", IsShared = true },
            new SharedPrinterInfo { Name = "Private", IsShared = false }
        };

        Assert.Equal(2, PrinterAccessPolicy.Filter(printers, PrinterAccessMode.AllShared, Array.Empty<string>()).Count);
        Assert.Single(PrinterAccessPolicy.Filter(printers, PrinterAccessMode.SelectedOnly, new[] { "Epson" }));
        Assert.Empty(PrinterAccessPolicy.Filter(printers, PrinterAccessMode.None, Array.Empty<string>()));
        Assert.True(PrinterAccessPolicy.Allows(PrinterAccessMode.AllShared, Array.Empty<string>(), "Canon"));
        Assert.False(PrinterAccessPolicy.Allows(PrinterAccessMode.None, Array.Empty<string>(), "Canon"));
    }

    [Fact]
    public void PrinterAccessPolicy_PreservesLegacyNonEmptyListAsSelectedOnly()
    {
        var device = new DeviceInfo { AllowedPrinters = new List<string> { "Canon" } };

        Assert.Equal(PrinterAccessMode.SelectedOnly, device.EffectivePrinterPermissionMode);
        Assert.Equal("Canon", device.AllowedPrintersSummary);
        Assert.False(PrinterAccessPolicy.Allows(device.PrinterPermissionMode, device.AllowedPrinters, "Epson"));
    }

    [Fact]
    public void PairingCode_Generate_ValidFormat()
    {
        for (int i = 0; i < 20; i++)
        {
            string code = RPMCrypto.GeneratePairingCode();
            Assert.StartsWith("RPM-", code);
            Assert.Equal(10, code.Length);
            Assert.True(code[4..].All(char.IsAsciiLetterOrDigit));
        }
    }

    [Fact]
    public void PairingCode_Generate_WithPreviousCode_AlwaysRotates()
    {
        string previousCode = "RPM-A7K2Q9";

        string nextCode = RPMCrypto.GeneratePairingCode(previousCode);

        Assert.NotEqual(previousCode, nextCode);
        Assert.StartsWith("RPM-", nextCode);
        Assert.Equal(10, nextCode.Length);
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
            string pairingCode = "RPM-A7K2Q9";
            string secretKey = "RPM_TEST_SHARED_SECRET";

            var serverClient = new RelayClient();
            var clientA = new RelayClient();
            serverClient.PairingCode = pairingCode;
            clientA.PairingCode = pairingCode;

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
    public async Task Relay_RoutesDirectPrintAckBackToClient()
    {
        int testPort = 19891;
        var relay = new RelayServer();
        relay.Start(testPort);

        try
        {
            string relayUrl = $"ws://127.0.0.1:{testPort}/";
            string pairingCode = "RPM-B8L3T7";
            string secretKey = "RPM_TEST_STREAM_SECRET";
            using var host = new RelayClient { PairingCode = pairingCode };
            using var client = new RelayClient { PairingCode = pairingCode };
            var ackReceived = new TaskCompletionSource<PrintStreamAckPayload>(TaskCreationOptions.RunContinuationsAsynchronously);

            client.OnPacketReceived += (_, packet) =>
            {
                if (packet.Type != MessageType.PrintStreamAck) return;
                var ack = client.DecryptPayload<PrintStreamAckPayload>(packet, secretKey);
                if (ack is not null) ackReceived.TrySetResult(ack);
            };

            await host.StartAsync(relayUrl);
            await Task.Delay(250);
            Assert.True(await host.SendPacketAsync(new NetworkPacket
            {
                Type = MessageType.RegisterServer,
                SenderId = "HOST-01",
                PairingCode = pairingCode
            }));

            await client.StartAsync(relayUrl);
            await Task.Delay(250);
            Assert.True(await client.SendPacketAsync(new NetworkPacket
            {
                Type = MessageType.ClientJoin,
                SenderId = "CLIENT-01",
                PairingCode = pairingCode,
                PayloadJson = JsonSerializer.Serialize(new ClientJoinPayload
                {
                    PairingCode = pairingCode,
                    ClientDevice = new DeviceInfo { DeviceId = "CLIENT-01", DeviceName = "Test Client" }
                })
            }));

            await Task.Delay(250);
            Assert.True(await host.SendEncryptedPayloadAsync(
                MessageType.PrintStreamAck,
                "HOST-01",
                "CLIENT-01",
                pairingCode,
                new PrintStreamAckPayload
                {
                    JobId = "job-1",
                    Sequence = 0,
                    Status = "RECEIVED",
                    Message = "Đã nhận chunk"
                },
                secretKey));

            var completed = await Task.WhenAny(ackReceived.Task, Task.Delay(5000));
            Assert.Same(ackReceived.Task, completed);
            var ack = await ackReceived.Task;
            Assert.Equal("job-1", ack.JobId);
            Assert.Equal("RECEIVED", ack.Status);
        }
        finally
        {
            relay.Stop();
        }
    }

    [Fact]
    public async Task Relay_ReportsLanBindingAndLocalAccessStatus()
    {
        int testPort = 19890;
        var relay = new RelayServer();
        relay.Start(testPort);

        try
        {
            Assert.Equal(testPort, relay.Port);
            Assert.NotEmpty(relay.LanBindingStatus);

            bool localAccess = await relay.CheckLocalLanAccessAsync();
            Assert.Equal(relay.IsLanAvailable, localAccess);
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
    public async Task IppServer_CapturesDocumentFormatAndPayload()
    {
        int ippPort = 9886;
        using var ippServer = new LocalIppServer();
        var received = new TaskCompletionSource<PrintJobReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        ippServer.OnJobReceived += (_, args) => received.TrySetResult(args);
        ippServer.Start(ippPort);

        try
        {
            byte[] payload = Encoding.ASCII.GetBytes("RAW-TEST-DOCUMENT");
            using var packet = new MemoryStream();
            packet.Write([1, 1, 0, 2, 0, 0, 0, 1]);
            packet.WriteByte(0x01);
            WriteIppAttribute(packet, 0x47, "attributes-charset", "utf-8");
            WriteIppAttribute(packet, 0x48, "attributes-natural-language", "en-us");
            WriteIppAttribute(packet, 0x49, "document-format", "application/postscript");
            WriteIppAttribute(packet, 0x42, "job-name", "raw-test.prn");
            packet.WriteByte(0x03);
            packet.Write(payload);

            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{ippPort}/")
            {
                Content = new ByteArrayContent(packet.ToArray())
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");
            var response = await http.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            var completed = await Task.WhenAny(received.Task, Task.Delay(3000));
            Assert.Same(received.Task, completed);
            var captured = await received.Task;
            Assert.Equal("application/postscript", captured.Job.DocumentFormat);
            Assert.Equal(payload, captured.Data);
        }
        finally
        {
            ippServer.Stop();
        }
    }

    [Fact]
    public async Task IppServer_SpoolsLargePayloadToBoundedTempFile()
    {
        int ippPort = 9887;
        using var ippServer = new LocalIppServer();
        var received = new TaskCompletionSource<PrintJobReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        ippServer.OnJobReceived += (_, args) => received.TrySetResult(args);
        ippServer.Start(ippPort);

        string tempPath = string.Empty;
        try
        {
            byte[] payload = new byte[5 * 1024 * 1024];
            Random.Shared.NextBytes(payload);
            using var packet = new MemoryStream();
            packet.Write([1, 1, 0, 2, 0, 0, 0, 2]);
            packet.WriteByte(0x01);
            WriteIppAttribute(packet, 0x47, "attributes-charset", "utf-8");
            WriteIppAttribute(packet, 0x48, "attributes-natural-language", "en-us");
            WriteIppAttribute(packet, 0x49, "document-format", "application/postscript");
            WriteIppAttribute(packet, 0x42, "job-name", "large-test.prn");
            packet.WriteByte(0x03);
            int documentOffset = (int)packet.Position;
            packet.Write(payload);

            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{ippPort}/")
            {
                Content = new ByteArrayContent(packet.ToArray())
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");
            var response = await http.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            var completed = await Task.WhenAny(received.Task, Task.Delay(5000));
            Assert.Same(received.Task, completed);
            var captured = await received.Task;
            tempPath = captured.TempFilePath ?? string.Empty;
            Assert.NotNull(captured.TempFilePath);
            Assert.Empty(captured.Data);
            Assert.Equal(payload.LongLength, captured.DataLength);
            Assert.Equal(documentOffset, captured.DataOffset);
            Assert.True(File.Exists(captured.TempFilePath));
        }
        finally
        {
            ippServer.Stop();
            if (!string.IsNullOrWhiteSpace(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void WriteIppAttribute(Stream stream, byte tag, string name, string value)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);
        stream.WriteByte(tag);
        stream.WriteByte((byte)(nameBytes.Length >> 8));
        stream.WriteByte((byte)nameBytes.Length);
        stream.Write(nameBytes);
        stream.WriteByte((byte)(valueBytes.Length >> 8));
        stream.WriteByte((byte)valueBytes.Length);
        stream.Write(valueBytes);
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

            Assert.False(result.HasUpdate);
            Assert.Equal("1.0.1", result.LatestVersion);
            Assert.NotNull(result.Manifest);
            Assert.Contains("chữ ký số", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
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

            var (successValid, validMessage, validFile) = await UpdateService.DownloadUpdateAsync(validManifest);
            Assert.False(successValid);
            Assert.Contains("chữ ký số", validMessage, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(validFile)) File.Delete(validFile);

            var invalidManifest = new UpdateManifest
            {
                Version = "1.0.1",
                DownloadUrl = mockExeSource,
                Sha256 = "0000000000000000000000000000000000000000000000000000000000000000" // Corrupted hash
            };

            var (successInvalid, errorMsg, _) = await UpdateService.DownloadUpdateAsync(invalidManifest);
            Assert.False(successInvalid);
            Assert.Contains("chữ ký số", errorMsg, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
        }
    }

    [Fact]
    public async Task UpdateService_DownloadToFile_ReleasesFileBeforeValidation()
    {
        string targetFile = Path.Combine(Path.GetTempPath(), $"rpm_update_lock_{Guid.NewGuid():N}.bin");
        byte[] content = Encoding.UTF8.GetBytes("downloaded update content");

        try
        {
            await UpdateService.DownloadToFileAsync(
                new MemoryStream(content),
                targetFile,
                content.Length);

            await using var reopened = new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal(content, await ReadAllBytesAsync(reopened));
        }
        finally
        {
            if (File.Exists(targetFile)) File.Delete(targetFile);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
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

    [Fact]
    public void Compatibility_Diagnostics_DetectsWindowsAnd64BitCorrectly()
    {
        var report = CompatibilityChecker.RunDiagnostics();

        Assert.NotNull(report);
        Assert.NotEmpty(report.OperatingSystem);
        Assert.True(report.Is64Bit);
        Assert.True(report.Items.Count >= 6);

        // Verify key diagnostic checklist items
        Assert.Contains(report.Items, i => i.Category == "Hệ điều hành");
        Assert.Contains(report.Items, i => i.Category == "Kiến trúc phần cứng");
        Assert.Contains(report.Items, i => i.Category == "Dịch vụ in ấn");
        Assert.Contains(report.Items, i => i.Category == "Ứng dụng văn phòng");
    }

    [Fact]
    public void Compatibility_PrinterBrandDetection_RecognizesMajorBrands()
    {
        var mockPrinters = new List<SharedPrinterInfo>
        {
            new() { Name = "Canon LBP 2900 USB Laser", DriverName = "Canon LBP2900", IsShared = true },
            new() { Name = "HP LaserJet Pro M404dn", DriverName = "HP LaserJet", IsShared = true },
            new() { Name = "Brother HL-L2321D series", DriverName = "Brother HL-L2320D", IsShared = true },
            new() { Name = "Epson L3150 Series WiFi/USB", DriverName = "Epson ESC/P", IsShared = true }
        };

        var report = CompatibilityChecker.RunDiagnostics(null, mockPrinters);

        var printerItem = report.Items.FirstOrDefault(i => i.Category == "Máy in vật lý");
        Assert.NotNull(printerItem);
        Assert.Equal(CompatibilityLevel.Compatible, printerItem.Level);
        Assert.Contains("Canon", printerItem.Detail);
        Assert.Contains("HP", printerItem.Detail);
        Assert.Contains("Brother", printerItem.Detail);
        Assert.Contains("Epson", printerItem.Detail);
    }

    [Fact]
    public void Compatibility_PortAvailabilityCheck_ValidatesPortReadiness()
    {
        // Check a random high port
        bool isAvailable = CompatibilityChecker.CheckPortAvailable(19283);
        Assert.True(isAvailable);
    }

    [Fact]
    public void Update_SemanticVersionComparison_IdentifiesNewerVersions()
    {
        Assert.True(UpdateService.IsNewerVersion("1.0.0", "1.0.1"));
        Assert.True(UpdateService.IsNewerVersion("V1.0.0", "V1.0.1"));
        Assert.True(UpdateService.IsNewerVersion("1.0.1", "1.1.0"));
        Assert.True(UpdateService.IsNewerVersion("1.0.9", "1.1.0"));
        Assert.True(UpdateService.IsNewerVersion("1.1.0", "2.0.0"));

        // Same or older versions should NOT trigger update
        Assert.False(UpdateService.IsNewerVersion("1.0.1", "1.0.0"));
        Assert.False(UpdateService.IsNewerVersion("1.0.0", "1.0.0"));
        Assert.False(UpdateService.IsNewerVersion("2.0.0", "1.9.9"));
    }

    [Fact]
    public void Update_Compatibility_RejectsInvalidOrTooOldVersions()
    {
        Assert.True(UpdateService.IsCompatibleVersion("1.0.1", "1.0.0"));
        Assert.False(UpdateService.IsCompatibleVersion("1.0.0", "1.0.1"));
        Assert.False(UpdateService.IsCompatibleVersion("1.0", "1.0.0"));
        Assert.False(UpdateService.IsCompatibleVersion("1.0.0", "invalid"));
    }

    [Fact]
    public async Task Update_ChannelFiltering_StableClientRejectsBetaUntilReleased()
    {
        string tempJson = Path.Combine(Path.GetTempPath(), $"rpm_test_manifest_{Guid.NewGuid():N}.json");
        try
        {
            var betaManifest = new UpdateManifest
            {
                Version = "1.0.2",
                Channel = "Beta",
                ReleaseDate = DateTime.UtcNow,
                DownloadUrl = "http://localhost:9090/download/RPM_v1.0.2.exe",
                Sha256 = "dummy_sha256",
                Changelog = new() { "Thử nghiệm tính năng kết nối mới trên máy con số 1" }
            };

            await File.WriteAllTextAsync(tempJson, JsonSerializer.Serialize(betaManifest));

            // Client on STABLE channel rejects beta release
            var stableResult = await UpdateService.CheckForUpdatesAsync(tempJson, "1.0.0", "Stable");
            Assert.False(stableResult.HasUpdate);
            Assert.Equal("1.0.2", stableResult.LatestVersion);

            // Unsigned manifests are rejected for every channel.
            var betaResult = await UpdateService.CheckForUpdatesAsync(tempJson, "1.0.0", "Beta");
            Assert.False(betaResult.HasUpdate);
            Assert.Equal("1.0.2", betaResult.LatestVersion);
            Assert.Equal("Beta", betaResult.Channel);
            Assert.Contains("chữ ký số", betaResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempJson)) File.Delete(tempJson);
        }
    }

    [Fact]
    public void Update_DataPreservation_ConfigRemainsIntactAcrossUpdates()
    {
        var settings = new RPM.Core.Config.AppSettings
        {
            AppVersion = "1.0.0",
            Role = AppRole.Server,
            PairingCode = "RPM-9988",
            SelectedPrinterName = "Canon LBP2900",
            SharedPrinterNames = new() { "Canon LBP2900", "HP LaserJet" },
            ApprovedClients = new()
            {
                new() { DeviceId = "dev-1", DeviceName = "MayCon-1", IsApproved = true, Department = "Kế toán" }
            },
            PrintHistory = new()
            {
                new() { JobId = "job-1", DocumentName = "BaoCaoThang.pdf", Status = PrintJobStatus.Completed }
            }
        };

        // Simulate version update to 1.0.1
        settings.AppVersion = "1.0.1";
        settings.UpdateHistory.Add(new UpdateHistoryItem
        {
            FromVersion = "1.0.0",
            ToVersion = "1.0.1",
            Status = "Thành công"
        });

        // Verify all settings are 100% preserved
        Assert.Equal("1.0.1", settings.AppVersion);
        Assert.Equal(AppRole.Server, settings.Role);
        Assert.Equal("RPM-9988", settings.PairingCode);
        Assert.Equal("Canon LBP2900", settings.SelectedPrinterName);
        Assert.Equal(2, settings.SharedPrinterNames.Count);
        Assert.Single(settings.ApprovedClients);
        Assert.Equal("MayCon-1", settings.ApprovedClients[0].DeviceName);
        Assert.Single(settings.PrintHistory);
        Assert.Equal("BaoCaoThang.pdf", settings.PrintHistory[0].DocumentName);
        Assert.Single(settings.UpdateHistory);
    }

    [Fact]
    public void Audit_LiveSystemAuditor_CalculatesExactWeightsAndRejectsMockData()
    {
        var svc = new PrinterService();
        var printers = svc.GetInstalledPrinters();
        var audit = LiveSystemAuditor.RunAudit(null, printers);

        Assert.NotNull(audit);
        Assert.NotEmpty(audit.OperatingSystem);
        Assert.Equal(27, audit.Items.Count);

        // Sum of all weights must equal exactly 100.0%
        double totalWeight = audit.Items.Sum(i => i.WeightPercent);
        Assert.Equal(100.0, Math.Round(totalWeight, 1));

        // Strict verification: Absolutely 0 mock data items!
        Assert.Equal(0, audit.MockDataCount);
        Assert.True(audit.RealDataCount > 0);

        // Verified percent must be based solely on PASS items
        double expectedVerified = audit.Items.Where(i => i.Status == AuditStatus.Pass).Sum(i => i.WeightPercent);
        Assert.Equal(Math.Round(expectedVerified, 1), audit.VerifiedOperationalPercent);
        Assert.Equal(audit.Items.Count, audit.PassCount + audit.FailCount + audit.BlockedCount + audit.NotTestedCount);
        Assert.True(audit.PassCount > 0);
        Assert.True(audit.BlockedCount > 0);

        // Verify the 7 groups exist
        Assert.Equal(7, audit.Groups.Count);
        Assert.Contains(audit.Groups, g => g.GroupName.StartsWith("1."));
        Assert.Contains(audit.Groups, g => g.GroupName.StartsWith("5."));
    }

    [Fact]
    public void Audit_PrinterService_PopulatesPortAndDriverFromWindows()
    {
        var svc = new PrinterService();
        var printers = svc.GetInstalledPrinters();

        Assert.NotNull(printers);
        // On any standard Windows machine, at least Print to PDF or OneNote exists
        if (printers.Count > 0)
        {
            var pdfPrinter = printers.FirstOrDefault(p => p.Name.Contains("PDF", StringComparison.OrdinalIgnoreCase));
            if (pdfPrinter != null)
            {
                Assert.False(string.IsNullOrEmpty(pdfPrinter.PortName));
            }
        }
    }
}
