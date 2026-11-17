using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RPM.Core.Cloud;
using RPM.Core.Networking;
using RPM.Core.Protocol;
using RPM.Core.Security;

namespace RPM.Networking;

/// <summary>
/// Authenticated WSS data-plane connection for one approved Cloud pairing.
/// The gateway only routes packet envelopes; document payloads remain inside
/// the existing packet encryption and are never logged by this class.
/// </summary>
public sealed class CloudRelayClient : IAsyncDisposable
{
    private readonly bool _isHost;
    private readonly CloudDeviceKeyStore _keyStore;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource<bool> _ready = NewReadySource();
    private ClientWebSocket? _socket;
    private Task? _runTask;
    private string _gatewayUrl = string.Empty;
    private string _sessionToken = string.Empty;
    private string _pairingId = string.Empty;
    private string _hostId = string.Empty;
    private string _clientId = string.Empty;
    private string _deviceId = string.Empty;
    private string _sessionSecret = string.Empty;
    private string? _wrappedSessionKey;
    private Func<CancellationToken, Task<CloudRealtimeSession?>>? _sessionFactory;
    private bool _shouldRun;

    public CloudRelayClient(bool isHost, CloudDeviceKeyStore? keyStore = null)
    {
        _isHost = isHost;
        _keyStore = keyStore ?? new CloudDeviceKeyStore();
    }

    public string PairingCode { get; set; } = string.Empty;
    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public bool IsReady => IsConnected && _ready.Task.IsCompletedSuccessfully;
    public string SessionSecret => _sessionSecret;

    public event EventHandler? OnConnected;
    public event EventHandler? OnReady;
    public event EventHandler<string>? OnDisconnected;
    public event EventHandler<NetworkPacket>? OnPacketReceived;
    public event EventHandler<string>? OnLog;

    public void ConfigureClientSession(string hostPublicKeyBase64)
    {
        if (_isHost) throw new InvalidOperationException("Host cannot configure the client relay session.");
        byte[] sessionKey = RandomNumberGenerator.GetBytes(32);
        _sessionSecret = Convert.ToBase64String(sessionKey);
        _wrappedSessionKey = CloudRelayProtocol.WrapSessionKey(hostPublicKeyBase64, sessionKey);
        CryptographicOperations.ZeroMemory(sessionKey);
    }

    public Task StartAsync(
        string gatewayUrl,
        string sessionToken,
        string pairingId,
        string hostId,
        string clientId,
        string deviceId)
    {
        ValidateGatewayUrl(gatewayUrl);
        if (string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(pairingId) ||
            string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Cloud relay identity is incomplete.");
        if (!_isHost && string.IsNullOrWhiteSpace(_wrappedSessionKey))
            throw new InvalidOperationException("Client relay session key has not been configured.");

        _gatewayUrl = gatewayUrl;
        _sessionToken = sessionToken;
        _pairingId = pairingId;
        _hostId = hostId;
        _clientId = clientId;
        _deviceId = deviceId;
        _shouldRun = true;
        if (_runTask is null) _runTask = Task.Run(RunAsync);
        return Task.CompletedTask;
    }

    public Task StartWithSessionFactoryAsync(
        Func<CancellationToken, Task<CloudRealtimeSession?>> sessionFactory,
        string pairingId,
        string hostId,
        string clientId,
        string deviceId)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _pairingId = pairingId;
        _hostId = hostId;
        _clientId = clientId;
        _deviceId = deviceId;
        _shouldRun = true;
        if (_runTask is null) _runTask = Task.Run(RunAsync);
        return Task.CompletedTask;
    }

    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (IsReady) return true;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await _ready.Task.WaitAsync(timeoutCts.Token);
            return IsReady;
        }
        catch (OperationCanceledException) { return false; }
    }

    private async Task RunAsync()
    {
        TimeSpan backoff = TimeSpan.FromSeconds(1);
        while (_shouldRun && !_stop.IsCancellationRequested)
        {
            try
            {
                if (_sessionFactory is not null)
                {
                    var session = await _sessionFactory(_stop.Token) ?? throw new InvalidOperationException("Cloud relay session was not issued.");
                    ValidateGatewayUrl(session.GatewayUrl);
                    _gatewayUrl = session.GatewayUrl;
                    _sessionToken = session.SessionToken;
                }
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _socket = socket;
                await socket.ConnectAsync(new Uri(_gatewayUrl), _stop.Token);
                _ready = NewReadySource();
                await SendJsonAsync(new { type = "AUTH", token = _sessionToken, channel = CloudRelayProtocol.AuthChannel }, _stop.Token);
                await SendJsonAsync(new CloudRelayBindMessage(
                    CloudRelayProtocol.BindType,
                    _pairingId,
                    _hostId,
                    _clientId,
                    _deviceId,
                    _wrappedSessionKey,
                    _isHost ? "HOST" : "CLIENT"), _stop.Token);
                backoff = TimeSpan.FromSeconds(1);
                OnLog?.Invoke(this, $"[Relay Cloud] Đã kết nối phiên {_pairingId[..Math.Min(8, _pairingId.Length)]}.");
                OnConnected?.Invoke(this, EventArgs.Empty);
                await ReceiveLoopAsync(socket, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                OnLog?.Invoke(this, $"[Relay Cloud] Kết nối thất bại: {ex.Message}");
            }
            finally
            {
                _socket = null;
                _ready.TrySetCanceled();
                if (_shouldRun) OnDisconnected?.Invoke(this, "Mất kết nối Relay Cloud.");
            }

            if (!_shouldRun || _stop.IsCancellationRequested) break;
            try { await Task.Delay(backoff, _stop.Token); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > PacketSecurity.MaxNetworkPacketBytes)
                    throw new InvalidDataException("Relay message is invalid or too large.");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            using JsonDocument document = JsonDocument.Parse(message.ToArray());
            string type = document.RootElement.TryGetProperty("type", out var typeNode) ? typeNode.GetString() ?? string.Empty : string.Empty;
            if (type == CloudRelayProtocol.KeyType && _isHost)
            {
                string wrapped = document.RootElement.GetProperty("wrappedSessionKey").GetString() ?? string.Empty;
                using var privateKey = _keyStore.LoadOrCreate();
                byte[] key = CloudRelayProtocol.UnwrapSessionKey(wrapped, privateKey);
                _sessionSecret = Convert.ToBase64String(key);
                CryptographicOperations.ZeroMemory(key);
                _ready.TrySetResult(true);
                OnReady?.Invoke(this, EventArgs.Empty);
                await SendJsonAsync(new { type = CloudRelayProtocol.KeyAckType, pairingId = _pairingId, deviceId = _deviceId }, cancellationToken);
                continue;
            }

            if (type == CloudRelayProtocol.KeyAckType && !_isHost)
            {
                _ready.TrySetResult(true);
                OnReady?.Invoke(this, EventArgs.Empty);
                continue;
            }

            if (type != CloudRelayProtocol.PacketType) continue;
            var relayMessage = document.RootElement.Deserialize<CloudRelayPacketMessage>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (relayMessage is null || !string.Equals(relayMessage.PairingId, _pairingId, StringComparison.Ordinal) ||
                !string.Equals(relayMessage.SenderId, (_isHost ? _clientId : _hostId), StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(_sessionSecret) || !CloudRelayProtocol.VerifyPacketMac(relayMessage, _sessionSecret))
            {
                OnLog?.Invoke(this, "[Relay Cloud] Đã từ chối packet không hợp lệ.");
                continue;
            }

            var packet = JsonSerializer.Deserialize<NetworkPacket>(relayMessage.PacketJson);
            if (packet is null || !PacketSecurity.IsFresh(packet, DateTime.UtcNow) || !PacketSecurity.VerifyAuthTagWithSecret(packet, _sessionSecret))
            {
                OnLog?.Invoke(this, "[Relay Cloud] Packet không vượt qua kiểm tra chữ ký hoặc thời gian.");
                continue;
            }
            OnPacketReceived?.Invoke(this, packet);
        }
    }

    public async Task<bool> SendPacketAsync(NetworkPacket packet)
    {
        if (!IsReady || _socket is null || string.IsNullOrWhiteSpace(_sessionSecret)) return false;
        packet.Timestamp = DateTime.UtcNow;
        packet.AuthTagBase64 = PacketSecurity.ComputeAuthTagWithSecret(packet, _sessionSecret);
        string packetJson = JsonSerializer.Serialize(packet);
        var message = new CloudRelayPacketMessage(
            CloudRelayProtocol.PacketType,
            _pairingId,
            _deviceId,
            string.IsNullOrWhiteSpace(packet.TargetId) ? (_isHost ? _clientId : _hostId) : packet.TargetId,
            packetJson,
            CloudRelayProtocol.ComputePacketMac(_sessionSecret, _pairingId, _deviceId, packetJson));
        await _sendLock.WaitAsync(_stop.Token);
        try
        {
            await SendJsonAsync(message, _stop.Token);
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"[Relay Cloud] Gửi packet thất bại: {ex.Message}");
            return false;
        }
        finally { _sendLock.Release(); }
    }

    public async Task<bool> SendEncryptedPayloadAsync(
        MessageType type,
        string senderId,
        string targetId,
        string pairingCode,
        object payloadObject)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(_sessionSecret)) return false;
        string payloadJson = JsonSerializer.Serialize(payloadObject);
        byte[] key = RPMCrypto.DeriveKey(_sessionSecret);
        var (cipher, nonce, tag) = RPMCrypto.Encrypt(Encoding.UTF8.GetBytes(payloadJson), key);
        CryptographicOperations.ZeroMemory(key);
        return await SendPacketAsync(new NetworkPacket
        {
            Type = type,
            SenderId = senderId,
            TargetId = targetId,
            PairingCode = pairingCode,
            EncryptedPayloadBase64 = Convert.ToBase64String(cipher),
            NonceBase64 = Convert.ToBase64String(nonce),
            TagBase64 = Convert.ToBase64String(tag)
        });
    }

    public T? DecryptPayload<T>(NetworkPacket packet)
    {
        if (string.IsNullOrWhiteSpace(_sessionSecret)) return default;
        if (string.IsNullOrEmpty(packet.EncryptedPayloadBase64) ||
            string.IsNullOrEmpty(packet.NonceBase64) || string.IsNullOrEmpty(packet.TagBase64))
        {
            return string.IsNullOrWhiteSpace(packet.PayloadJson) ? default : JsonSerializer.Deserialize<T>(packet.PayloadJson);
        }

        try
        {
            byte[] key = RPMCrypto.DeriveKey(_sessionSecret);
            byte[] plain = RPMCrypto.Decrypt(
                Convert.FromBase64String(packet.EncryptedPayloadBase64),
                Convert.FromBase64String(packet.NonceBase64),
                Convert.FromBase64String(packet.TagBase64),
                key);
            CryptographicOperations.ZeroMemory(key);
            return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"[Relay Cloud] Không giải mã được payload: {ex.Message}");
            return default;
        }
    }

    private async Task SendJsonAsync<T>(T value, CancellationToken cancellationToken)
    {
        if (_socket is null) throw new InvalidOperationException("Relay socket is not connected.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _shouldRun = false;
        _stop.Cancel();
        if (_socket is { State: WebSocketState.Open } socket)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client shutting down", CancellationToken.None); } catch { }
        }
        if (_runTask is not null)
        {
            try { await _runTask; } catch (OperationCanceledException) { }
        }
        _sendLock.Dispose();
        _lifecycleLock.Dispose();
        _stop.Dispose();
    }

    private static void ValidateGatewayUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            throw new ArgumentException("Relay Cloud phải dùng ws:// cục bộ hoặc wss:// công khai.", nameof(value));
        if (uri.Scheme == "ws" && !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1"))
            throw new ArgumentException("Relay Cloud công khai phải dùng WSS.", nameof(value));
    }

    private static TaskCompletionSource<bool> NewReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
