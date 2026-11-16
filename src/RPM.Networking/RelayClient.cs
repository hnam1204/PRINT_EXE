using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RPM.Core.Networking;
using RPM.Core.Models;
using RPM.Core.Protocol;
using RPM.Core.Security;

namespace RPM.Networking;

public class PacketReceivedEventArgs : EventArgs
{
    public NetworkPacket Packet { get; set; } = new();
}

public class RelayClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _isDisposed;
    private string _relayUrl = string.Empty;
    private bool _shouldAutoReconnect = true;
    private int _startRequested;
    private int _consecutiveFails = 0;
    private bool _wasConnectedBefore = false;

    public string PairingCode { get; set; } = string.Empty;

    public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;

    public event EventHandler? OnConnected;
    public event EventHandler<string>? OnDisconnected;
    public event EventHandler<NetworkPacket>? OnPacketReceived;
    public event EventHandler<string>? OnLog;

    public Task StartAsync(string relayUrl)
    {
        if (!Uri.TryCreate(relayUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme is not ("ws" or "wss")) ||
            (uri.Scheme == "ws" && !RelayEndpointPolicy.IsPrivateNetworkHost(uri.Host)))
        {
            throw new ArgumentException("ws:// chỉ được dùng cho IP mạng LAN riêng; Relay công khai phải dùng wss://.", nameof(relayUrl));
        }

        if (Interlocked.CompareExchange(ref _startRequested, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        _relayUrl = relayUrl;
        _shouldAutoReconnect = true;
        _ = Task.Run(ConnectInternalAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _startRequested, 0);
        _shouldAutoReconnect = false;
        _cts?.Cancel();

        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client shutting down", CancellationToken.None);
                }
            }
            catch { }
            finally
            {
                _ws.Dispose();
                _ws = null;
            }
        }
    }

    private async Task ConnectInternalAsync()
    {
        while (_shouldAutoReconnect && !_isDisposed)
        {
            try
            {
                _cts = new CancellationTokenSource();
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                if (_consecutiveFails == 0)
                {
                    OnLog?.Invoke(this, $"Đang kết nối tới Relay: {_relayUrl}...");
                }
                else if (_consecutiveFails % 12 == 0)
                {
                    OnLog?.Invoke(this, $"Đang thử kết nối lại tới Relay (lần {_consecutiveFails})...");
                }

                await _ws.ConnectAsync(new Uri(_relayUrl), _cts.Token);

                if (_ws.State == WebSocketState.Open)
                {
                    _consecutiveFails = 0;
                    _wasConnectedBefore = true;
                    OnLog?.Invoke(this, "Kết nối Relay thành công!");
                    OnConnected?.Invoke(this, EventArgs.Empty);

                    await ReceiveLoopAsync(_cts.Token);
                }
            }
            catch (Exception ex)
            {
                _consecutiveFails++;
                if (_consecutiveFails == 1)
                {
                    OnLog?.Invoke(this, $"Lỗi kết nối Relay: {ex.Message} (Tự động kết nối lại ngầm)");
                }
            }

            if (_wasConnectedBefore)
            {
                _wasConnectedBefore = false;
                OnDisconnected?.Invoke(this, "Mất kết nối tới Relay.");
            }

            if (!_shouldAutoReconnect || _isDisposed) break;

            // Wait 5 seconds before retrying
            try
            {
                await Task.Delay(5000, _cts?.Token ?? CancellationToken.None);
            }
            catch
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024]; // 64KB chunk buffer

        while (!ct.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", ct);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            byte[] fullBytes = ms.ToArray();
            string json = Encoding.UTF8.GetString(fullBytes);

            try
            {
                var packet = JsonSerializer.Deserialize<NetworkPacket>(json);
                if (packet != null &&
                    PacketSecurity.IsValidPairingCode(PairingCode) &&
                    string.Equals(packet.PairingCode, PairingCode, StringComparison.OrdinalIgnoreCase) &&
                    PacketSecurity.VerifyAuthTag(packet, PairingCode) &&
                    PacketSecurity.IsFresh(packet, DateTime.UtcNow))
                {
                    OnPacketReceived?.Invoke(this, packet);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(this, $"Lỗi giải mã gói tin: {ex.Message}");
            }
        }
    }

    public async Task<bool> SendPacketAsync(NetworkPacket packet)
    {
        if (!IsConnected || _ws == null) return false;

        if (!PacketSecurity.IsValidPairingCode(PairingCode) ||
            !string.Equals(packet.PairingCode, PairingCode, StringComparison.OrdinalIgnoreCase))
        {
            OnLog?.Invoke(this, "Từ chối gửi packet: mã ghép đôi không khớp phiên.");
            return false;
        }

        packet.Timestamp = DateTime.UtcNow;
        packet.AuthTagBase64 = PacketSecurity.ComputeAuthTag(packet, PairingCode);

        await _sendLock.WaitAsync();
        try
        {
            string json = JsonSerializer.Serialize(packet);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"Lỗi gửi gói tin: {ex.Message}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<bool> SendEncryptedPayloadAsync(
        MessageType type,
        string senderId,
        string targetId,
        string pairingCode,
        object payloadObject,
        string secretKey)
    {
        string payloadJson = JsonSerializer.Serialize(payloadObject);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        byte[] key = RPMCrypto.DeriveKey(secretKey);
        var (cipher, nonce, tag) = RPMCrypto.Encrypt(payloadBytes, key);

        var packet = new NetworkPacket
        {
            Type = type,
            SenderId = senderId,
            TargetId = targetId,
            PairingCode = pairingCode,
            Timestamp = DateTime.UtcNow,
            EncryptedPayloadBase64 = Convert.ToBase64String(cipher),
            NonceBase64 = Convert.ToBase64String(nonce),
            TagBase64 = Convert.ToBase64String(tag)
        };

        return await SendPacketAsync(packet);
    }

    public T? DecryptPayload<T>(NetworkPacket packet, string secretKey)
    {
        if (string.IsNullOrEmpty(packet.EncryptedPayloadBase64) ||
            string.IsNullOrEmpty(packet.NonceBase64) ||
            string.IsNullOrEmpty(packet.TagBase64))
        {
            if (!string.IsNullOrEmpty(packet.PayloadJson))
            {
                return JsonSerializer.Deserialize<T>(packet.PayloadJson);
            }
            return default;
        }

        try
        {
            byte[] key = RPMCrypto.DeriveKey(secretKey);
            byte[] cipher = Convert.FromBase64String(packet.EncryptedPayloadBase64);
            byte[] nonce = Convert.FromBase64String(packet.NonceBase64);
            byte[] tag = Convert.FromBase64String(packet.TagBase64);

            byte[] plainBytes = RPMCrypto.Decrypt(cipher, nonce, tag, key);
            string json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"Lỗi giải mã payload E2EE: {ex.Message}");
            return default;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Interlocked.Exchange(ref _startRequested, 0);
        _cts?.Cancel();
        _ws?.Dispose();
        _sendLock.Dispose();
    }

}
