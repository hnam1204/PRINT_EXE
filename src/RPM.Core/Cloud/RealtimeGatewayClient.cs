using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPM.Core.Cloud;

/// <summary>
/// Authenticated control-plane WebSocket. It carries metadata only; documents
/// remain on the existing Cloud/P2P/Relay data paths.
/// </summary>
public sealed class RealtimeGatewayClient : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<CloudRealtimeSession?>> _sessionFactory;
    private readonly string _deviceId;
    private readonly CancellationTokenSource _stop = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private Task? _loop;
    private long _cursor;

    public RealtimeGatewayClient(string deviceId, Func<CancellationToken, Task<CloudRealtimeSession?>> sessionFactory)
    {
        _deviceId = deviceId;
        _sessionFactory = sessionFactory;
    }

    public bool IsRunning => _loop is not null;
    public event EventHandler<CloudRealtimeEvent>? OnEvent;
    public event EventHandler<string>? OnLog;

    public Task StartAsync()
    {
        if (_loop is null) _loop = Task.Run(RunAsync);
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        TimeSpan backoff = TimeSpan.FromSeconds(1);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var session = await _sessionFactory(_stop.Token);
                if (session is null || string.IsNullOrWhiteSpace(session.GatewayUrl) || string.IsNullOrWhiteSpace(session.SessionToken))
                    throw new InvalidOperationException("Realtime session was not issued.");

                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ValidateGatewayUri(session.GatewayUrl);
                await socket.ConnectAsync(new Uri(session.GatewayUrl), _stop.Token);
                await SendAsync(socket, new AuthMessage("AUTH", session.SessionToken, _cursor), _stop.Token);
                OnLog?.Invoke(this, $"[Realtime] Đã kết nối Gateway cho thiết bị {_deviceId}.");
                backoff = TimeSpan.FromSeconds(1);
                await ReceiveLoopAsync(socket, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                OnLog?.Invoke(this, $"[Realtime] Mất kết nối Gateway: {ex.Message}");
            }

            if (_stop.IsCancellationRequested) break;
            var jitter = Random.Shared.Next(0, 500);
            try { await Task.Delay(backoff + TimeSpan.FromMilliseconds(jitter), _stop.Token); } catch (OperationCanceledException) { break; }
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
                if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 256 * 1024)
                    throw new InvalidDataException("Realtime message is invalid or too large.");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            GatewayMessage? gatewayMessage;
            try { gatewayMessage = JsonSerializer.Deserialize<GatewayMessage>(message.ToArray(), _jsonOptions); }
            catch (JsonException) { continue; }
            if (gatewayMessage?.Event is null || !string.Equals(gatewayMessage.Type, "EVENT", StringComparison.OrdinalIgnoreCase)) continue;
            var eventData = gatewayMessage.Event;
            if (eventData.Cursor <= Interlocked.Read(ref _cursor)) continue;
            Interlocked.Exchange(ref _cursor, eventData.Cursor);
            OnEvent?.Invoke(this, eventData);
        }
    }

    private static Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, cancellationToken);

    private static void ValidateGatewayUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidOperationException("Realtime Gateway URL is invalid.");
        if (uri.Scheme != "wss" && !(uri.Scheme == "ws" && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1")))
            throw new InvalidOperationException("Realtime Gateway must use WSS outside localhost.");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }

    private sealed record AuthMessage(string Type, string Token, long Cursor);
    private sealed record GatewayMessage(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("cursor")] long Cursor,
        [property: JsonPropertyName("event")] CloudRealtimeEvent? Event);
}
