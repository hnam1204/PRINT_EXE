using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

const int MaxRelayMessageBytes = 16 * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
int port = ReadPositiveInt("PORT", 9191);
string apiBaseUrl = (Environment.GetEnvironmentVariable("REALTIME_API_BASE_URL") ?? "http://127.0.0.1:8000").TrimEnd('/');
string sharedSecret = Environment.GetEnvironmentVariable("REALTIME_GATEWAY_SHARED_SECRET")?.Trim() ?? string.Empty;
string databaseConnectionString = Environment.GetEnvironmentVariable("REALTIME_DATABASE_CONNECTION_STRING")?.Trim() ?? string.Empty;
if (string.IsNullOrWhiteSpace(sharedSecret))
    throw new InvalidOperationException("REALTIME_GATEWAY_SHARED_SECRET is required.");

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddSingleton(new GatewayOptions(apiBaseUrl, sharedSecret, databaseConnectionString));
builder.Services.AddSingleton<RealtimeConnectionRegistry>();
builder.Services.AddSingleton<RelayConnectionRegistry>();
builder.Services.AddSingleton<PostgresEventNotifier>(sp => new PostgresEventNotifier(sp.GetRequiredService<GatewayOptions>().DatabaseConnectionString));
builder.Services.AddHttpClient<RealtimeEventPump>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RealtimeEventPump>());

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/health", (RealtimeConnectionRegistry registry) => Results.Ok(new
{
    status = "ok",
    connections = registry.Count,
    transport = "websocket",
    tlsRequiredAtEdge = true
}));

app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket required");
        return;
    }

    var registry = context.RequestServices.GetRequiredService<RealtimeConnectionRegistry>();
    var pump = context.RequestServices.GetRequiredService<RealtimeEventPump>();
    if (registry.Count >= 1000)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsync("Gateway capacity reached");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var auth = await ReceiveAuthAsync(socket, context.RequestAborted);
    if (auth is null)
    {
        await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Authentication required");
        return;
    }

    if (!RealtimeSessionToken.TryValidate(auth.Token, registry.Options.SharedSecret, out var claims))
    {
        await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Invalid or expired session");
        return;
    }

    var connection = new RealtimeConnection(claims.DeviceId, claims.Role, socket, Math.Max(0, auth.Cursor));
    if (string.Equals(auth.Channel, CloudRelayChannel.Name, StringComparison.OrdinalIgnoreCase))
    {
        await HandleRelayConnectionAsync(
            context,
            connection,
            context.RequestServices.GetRequiredService<RelayConnectionRegistry>(),
            context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(),
            registry.Options,
            context.RequestAborted);
        return;
    }

    using var registration = await registry.RegisterAsync(connection, context.RequestAborted);
    await connection.SendJsonAsync(new GatewayMessage("WELCOME", connection.Cursor, null), context.RequestAborted);
    using var authorizationCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var authorizationMonitor = MonitorAuthorizationAsync(
        connection,
        registry.Options,
        context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(),
        authorizationCts.Token);
    try
    {
        await pump.ReplayAsync(connection, context.RequestAborted);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Realtime] catch-up unavailable: {ex.Message}");
        await CloseAsync(socket, WebSocketCloseStatus.InternalServerError, "Event source unavailable");
        return;
    }
    try
    {
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            var message = await ReceiveTextAsync(socket, context.RequestAborted);
            if (message is not { } document) break;
            if (document.TryGetProperty("type", out var type) && type.GetString() == "PING")
                await connection.SendJsonAsync(new GatewayMessage("PONG", connection.Cursor, null), context.RequestAborted);
            // The client has no command channel. Unknown messages are ignored.
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (WebSocketException) { }
    finally
    {
        authorizationCts.Cancel();
        try { await authorizationMonitor; } catch (OperationCanceledException) { }
        await registry.RemoveAsync(connection);
    }
});

await app.RunAsync();

static int ReadPositiveInt(string name, int fallback)
    => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

static async Task<AuthRequest?> ReceiveAuthAsync(WebSocket socket, CancellationToken cancellationToken)
{
    var json = await ReceiveTextAsync(socket, cancellationToken);
    if (json is not { } document || !document.TryGetProperty("type", out var type) || type.GetString() != "AUTH") return null;
    if (!document.TryGetProperty("token", out var token) || string.IsNullOrWhiteSpace(token.GetString())) return null;
    long cursor = document.TryGetProperty("cursor", out var cursorNode) && cursorNode.TryGetInt64(out var parsed) ? parsed : 0;
    string channel = document.TryGetProperty("channel", out var channelNode) ? channelNode.GetString() ?? "realtime" : "realtime";
    return new AuthRequest(token.GetString()!, cursor, channel);
}

static async Task HandleRelayConnectionAsync(
    HttpContext context,
    RealtimeConnection connection,
    RelayConnectionRegistry registry,
    HttpClient http,
    GatewayOptions options,
    CancellationToken cancellationToken)
{
    RelayBinding? binding = null;
    using var authorizationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    try
    {
        var bindDocument = await ReceiveTextAsync(context.WebSockets.IsWebSocketRequest ? connection.Socket : connection.Socket, cancellationToken);
        if (bindDocument is null || !string.Equals(bindDocument.Value.GetProperty("type").GetString(), CloudRelayChannel.BindType, StringComparison.Ordinal))
        {
            await CloseAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "Relay binding required");
            return;
        }

        binding = bindDocument.Value.Deserialize<RelayBinding>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (binding is null || !string.Equals(binding.DeviceId, connection.DeviceId, StringComparison.Ordinal) ||
            !string.Equals(binding.Role, connection.Role, StringComparison.Ordinal))
        {
            await CloseAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "Invalid relay binding");
            return;
        }

        if (!await registry.BindAsync(connection, binding, http, options, cancellationToken))
        {
            await CloseAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "Relay pairing is not authorized");
            return;
        }

        await connection.SendJsonAsync(new { type = CloudRelayChannel.ReadyType, pairingId = binding.PairingId }, cancellationToken);
        var authorizationMonitor = MonitorRelayAuthorizationAsync(connection, binding, registry, http, options, authorizationCts.Token);
        try
        {
            while (connection.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveTextAsync(connection.Socket, cancellationToken);
                if (message is not { } document) break;
                await registry.RouteAsync(connection, binding, document, cancellationToken);
            }
        }
        finally
        {
            authorizationCts.Cancel();
            try { await authorizationMonitor; } catch (OperationCanceledException) { }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    catch (WebSocketException) { }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Relay] session failed: {ex.Message}");
    }
    finally
    {
        if (binding is not null) registry.Remove(connection, binding);
        await CloseAsync(connection.Socket, WebSocketCloseStatus.NormalClosure, "Relay disconnected");
    }
}

static async Task MonitorRelayAuthorizationAsync(
    RealtimeConnection connection,
    RelayBinding binding,
    RelayConnectionRegistry registry,
    HttpClient http,
    GatewayOptions options,
    CancellationToken cancellationToken)
{
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            if (!await registry.IsAuthorizedAsync(connection, binding, http, options, cancellationToken))
            {
                await CloseAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "Relay pairing authorization revoked");
                return;
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
}

static async Task<JsonElement?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
{
    byte[] buffer = new byte[16 * 1024];
    using var stream = new MemoryStream();
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        // A 128 KiB encrypted print chunk expands to roughly 175 KiB as JSON
        // and Base64. Keep frame reads small, but bound the complete message
        // at the same 16 MiB ceiling used by the desktop packet receiver.
        if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > MaxRelayMessageBytes) return null;
        stream.Write(buffer, 0, result.Count);
        if (result.EndOfMessage) break;
    }
    try { return JsonSerializer.Deserialize<JsonElement>(stream.ToArray()); }
    catch (JsonException) { return null; }
}

static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
{
    if (socket.State == WebSocketState.Open)
    {
        try { await socket.CloseAsync(status, description, CancellationToken.None); } catch { }
    }
}

static async Task MonitorAuthorizationAsync(
    RealtimeConnection connection,
    GatewayOptions options,
    HttpClient http,
    CancellationToken cancellationToken)
{
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{options.ApiBaseUrl}/api/v1/internal/realtime/authorize/{Uri.EscapeDataString(connection.DeviceId)}");
            request.Headers.Add("X-Realtime-Gateway-Key", options.SharedSecret);
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("authorized", out var authorized) || !authorized.GetBoolean())
            {
                await CloseAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "Device authorization revoked");
                return;
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Realtime] authorization monitor failed: {ex.Message}");
        await CloseAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "Authorization check unavailable");
    }
}

internal sealed record GatewayOptions(string ApiBaseUrl, string SharedSecret, string DatabaseConnectionString);
internal sealed record AuthRequest(string Token, long Cursor, string Channel);
internal sealed record GatewayMessage(string Type, long Cursor, RealtimeEventEnvelope? Event);
internal static class CloudRelayChannel
{
    public const string Name = "relay";
    public const string BindType = "RELAY_BIND";
    public const string ReadyType = "RELAY_READY";
    public const string KeyType = "RELAY_KEY";
    public const string KeyAckType = "RELAY_KEY_ACK";
    public const string PacketType = "RELAY_PACKET";
}

internal sealed record RelayBinding(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("pairingId")] string PairingId,
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("wrappedSessionKey")] string? WrappedSessionKey,
    [property: JsonPropertyName("role")] string Role = "");

internal sealed class RelayConnectionRegistry
{
    private readonly ConcurrentDictionary<string, RelayRoom> _rooms = new(StringComparer.Ordinal);

    public int Count => _rooms.Values.Sum(room => (room.Host is null ? 0 : 1) + (room.Client is null ? 0 : 1));

    public async Task<bool> BindAsync(
        RealtimeConnection connection,
        RelayBinding binding,
        HttpClient http,
        GatewayOptions options,
        CancellationToken cancellationToken)
    {
        if (binding.Role is not ("HOST" or "CLIENT") ||
            (binding.Role == "HOST" && binding.DeviceId != binding.HostId) ||
            (binding.Role == "CLIENT" && binding.DeviceId != binding.ClientId))
            return false;

        if (!await AuthorizeAsync(connection.DeviceId, binding, http, options, cancellationToken)) return false;
        var room = _rooms.GetOrAdd(binding.PairingId, _ => new RelayRoom(binding.PairingId, binding.HostId, binding.ClientId));
        if (!room.Matches(binding)) return false;

        if (binding.Role == "HOST")
        {
            room.Host = connection;
            if (!string.IsNullOrWhiteSpace(room.WrappedSessionKey) && room.Client is not null)
                await room.Host.SendJsonAsync(new { type = CloudRelayChannel.KeyType, pairingId = room.PairingId, wrappedSessionKey = room.WrappedSessionKey }, cancellationToken);
        }
        else
        {
            room.Client = connection;
            room.WrappedSessionKey = binding.WrappedSessionKey;
            if (room.Host is not null && !string.IsNullOrWhiteSpace(binding.WrappedSessionKey))
                await room.Host.SendJsonAsync(new { type = CloudRelayChannel.KeyType, pairingId = room.PairingId, wrappedSessionKey = binding.WrappedSessionKey }, cancellationToken);
        }
        return true;
    }

    public async Task<bool> IsAuthorizedAsync(RealtimeConnection connection, RelayBinding binding, HttpClient http, GatewayOptions options, CancellationToken cancellationToken) =>
        await AuthorizeAsync(connection.DeviceId, binding, http, options, cancellationToken);

    private static async Task<bool> AuthorizeAsync(string deviceId, RelayBinding binding, HttpClient http, GatewayOptions options, CancellationToken cancellationToken)
    {
        string path = $"{options.ApiBaseUrl}/api/v1/internal/realtime/authorize-pairing?host_id={Uri.EscapeDataString(binding.HostId)}&client_id={Uri.EscapeDataString(binding.ClientId)}&pairing_id={Uri.EscapeDataString(binding.PairingId)}&device_id={Uri.EscapeDataString(deviceId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Realtime-Gateway-Key", options.SharedSecret);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return false;
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return body.RootElement.TryGetProperty("authorized", out var authorized) && authorized.GetBoolean();
    }

    public async Task RouteAsync(RealtimeConnection sender, RelayBinding binding, JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("type", out var typeNode)) return;
        string type = typeNode.GetString() ?? string.Empty;
        if (type is not (CloudRelayChannel.PacketType or CloudRelayChannel.KeyAckType)) return;
        if (!_rooms.TryGetValue(binding.PairingId, out var room) || !room.Matches(binding)) return;
        var peer = ReferenceEquals(sender, room.Host) ? room.Client : ReferenceEquals(sender, room.Client) ? room.Host : null;
        if (peer is null || peer.Socket.State != WebSocketState.Open) return;
        Console.WriteLine($"[Relay] route pairing={ShortId(binding.PairingId)} type={type} sender={ShortId(sender.DeviceId)}");
        await peer.SendJsonAsync(message, cancellationToken);
    }

    private static string ShortId(string value) => value.Length <= 8 ? value : value[..8];

    public void Remove(RealtimeConnection connection, RelayBinding binding)
    {
        if (!_rooms.TryGetValue(binding.PairingId, out var room)) return;
        if (ReferenceEquals(room.Host, connection)) room.Host = null;
        if (ReferenceEquals(room.Client, connection)) room.Client = null;
        if (room.Host is null && room.Client is null) _rooms.TryRemove(binding.PairingId, out _);
    }

    private sealed class RelayRoom
    {
        public RelayRoom(string pairingId, string hostId, string clientId) { PairingId = pairingId; HostId = hostId; ClientId = clientId; }
        public string PairingId { get; }
        public string HostId { get; }
        public string ClientId { get; }
        public string? WrappedSessionKey { get; set; }
        public RealtimeConnection? Host { get; set; }
        public RealtimeConnection? Client { get; set; }
        public bool Matches(RelayBinding binding) => PairingId == binding.PairingId && HostId == binding.HostId && ClientId == binding.ClientId;
    }
}

internal sealed record RealtimeEventEnvelope(
    [property: JsonPropertyName("eventId")] long EventId,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("recipientDeviceId")] string RecipientDeviceId,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("hostId")] string? HostId,
    [property: JsonPropertyName("clientId")] string? ClientId,
    [property: JsonPropertyName("pairingId")] string? PairingId,
    [property: JsonPropertyName("jobId")] string? JobId,
    [property: JsonPropertyName("serverTimestamp")] DateTime ServerTimestamp,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("cursor")] long Cursor,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("payload")] Dictionary<string, JsonElement> Payload);

internal sealed class RealtimeConnection
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public RealtimeConnection(string deviceId, string role, WebSocket socket, long cursor)
    {
        DeviceId = deviceId; Role = role; Socket = socket; Cursor = cursor;
    }
    public string DeviceId { get; }
    public string Role { get; }
    public WebSocket Socket { get; }
    public long Cursor { get; private set; }

    public async Task SendJsonAsync(object value, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
        }
        finally { _sendLock.Release(); }
    }

    public bool TryAdvance(long cursor)
    {
        if (cursor <= Cursor) return false;
        Cursor = cursor;
        return true;
    }
}

internal sealed class RealtimeConnectionRegistry
{
    private readonly ConcurrentDictionary<string, RealtimeConnection> _connections = new(StringComparer.Ordinal);
    public RealtimeConnectionRegistry(GatewayOptions options) => Options = options;
    public GatewayOptions Options { get; }
    public int Count => _connections.Count;
    public async Task<IDisposable> RegisterAsync(RealtimeConnection connection, CancellationToken cancellationToken)
    {
        if (_connections.TryGetValue(connection.DeviceId, out var previous))
        {
            _connections.TryRemove(connection.DeviceId, out _);
            try { await previous.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Replaced by a newer connection", cancellationToken); } catch { }
        }
        _connections[connection.DeviceId] = connection;
        return new Registration(() => _connections.TryRemove(new KeyValuePair<string, RealtimeConnection>(connection.DeviceId, connection)));
    }
    public bool TryGet(string deviceId, out RealtimeConnection? connection) => _connections.TryGetValue(deviceId, out connection);
    public async Task RemoveAsync(RealtimeConnection connection)
    {
        _connections.TryRemove(new KeyValuePair<string, RealtimeConnection>(connection.DeviceId, connection));
        try { if (connection.Socket.State == WebSocketState.Open) await connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None); } catch { }
    }
    private sealed class Registration(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}

internal sealed class RealtimeEventPump : BackgroundService
{
    private readonly HttpClient _http;
    private readonly GatewayOptions _options;
    private readonly RealtimeConnectionRegistry _registry;
    private readonly PostgresEventNotifier _notifier;
    private long _cursor;
    public RealtimeEventPump(HttpClient http, GatewayOptions options, RealtimeConnectionRegistry registry, PostgresEventNotifier notifier)
    { _http = http; _options = options; _registry = registry; _notifier = notifier; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.DatabaseConnectionString))
        {
            await ExecuteWithPostgresNotificationsAsync(stoppingToken);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = TimeSpan.FromSeconds(5);
            try { delay = await PumpOnceAsync(stoppingToken) ? TimeSpan.Zero : TimeSpan.FromSeconds(5); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[Realtime] event pump: {ex.Message}"); delay = TimeSpan.FromSeconds(2); }
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task ExecuteWithPostgresNotificationsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PumpOnceAsync(stoppingToken);
                Task listenTask = _notifier.ListenAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    Task notificationTask = _notifier.Notifications.WaitToReadAsync(stoppingToken).AsTask();
                    Task heartbeatTask = Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    Task completed = await Task.WhenAny(listenTask, notificationTask, heartbeatTask);
                    if (completed == listenTask)
                    {
                        await listenTask;
                        throw new InvalidOperationException("PostgreSQL notification listener stopped.");
                    }

                    await PumpOnceAsync(stoppingToken);
                    while (_notifier.Notifications.TryRead(out _)) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Realtime] PostgreSQL listener: {ex.Message}");
                try { await PumpOnceAsync(stoppingToken); } catch (Exception pumpError) { Console.Error.WriteLine($"[Realtime] reconciliation: {pumpError.Message}"); }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public async Task ReplayAsync(RealtimeConnection connection, CancellationToken cancellationToken)
    {
        long cursor = connection.Cursor;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await FetchAsync(cursor, 500, cancellationToken);
            if (events.Count == 0) break;
            foreach (var item in events.Where(e => e.EventId > connection.Cursor && e.RecipientDeviceId == connection.DeviceId))
            {
                if (!connection.TryAdvance(item.Cursor)) continue;
                await connection.SendJsonAsync(new GatewayMessage("EVENT", item.Cursor, item.ToEnvelope()), cancellationToken);
            }
            cursor = events[^1].Cursor;
            if (events.Count < 500) break;
        }
    }

    private async Task<bool> PumpOnceAsync(CancellationToken cancellationToken)
    {
        var events = await FetchAsync(_cursor, 500, cancellationToken);
        foreach (var item in events)
        {
            _cursor = Math.Max(_cursor, item.Cursor);
            if (!_registry.TryGet(item.RecipientDeviceId, out var connection) || connection is null) continue;
            if (!connection.TryAdvance(item.Cursor)) continue;
            try { await connection.SendJsonAsync(new GatewayMessage("EVENT", item.Cursor, item.ToEnvelope()), cancellationToken); }
            catch (Exception ex) { Console.Error.WriteLine($"[Realtime] delivery {connection.DeviceId}: {ex.Message}"); }
        }
        return events.Count > 0;
    }

    private async Task<List<RealtimeEventDto>> FetchAsync(long after, int limit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl}/api/v1/internal/realtime/events?after={after}&limit={limit}");
        request.Headers.Add("X-Realtime-Gateway-Key", _options.SharedSecret);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<RealtimeEventDto>>(cancellationToken: cancellationToken) ?? [];
    }

    private sealed record RealtimeEventDto(
        [property: JsonPropertyName("eventId")] long EventId,
        [property: JsonPropertyName("eventType")] string EventType,
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("hostId")] string? HostId,
        [property: JsonPropertyName("clientId")] string? ClientId,
        [property: JsonPropertyName("pairingId")] string? PairingId,
        [property: JsonPropertyName("jobId")] string? JobId,
        [property: JsonPropertyName("serverTimestamp")] DateTime ServerTimestamp,
        [property: JsonPropertyName("sequence")] long Sequence,
        [property: JsonPropertyName("cursor")] long Cursor,
        [property: JsonPropertyName("correlationId")] string CorrelationId,
        [property: JsonPropertyName("payload")] Dictionary<string, JsonElement> Payload)
    {
        public RealtimeEventEnvelope ToEnvelope() => new(EventId, EventType, RecipientDeviceId, SchemaVersion, HostId, ClientId, PairingId, JobId, ServerTimestamp, Sequence, Cursor, CorrelationId, Payload);

        [JsonPropertyName("recipientDeviceId")]
        public string RecipientDeviceId { get; init; } = string.Empty;
    }
}

internal sealed record RealtimeSessionClaims(string DeviceId, string Role, long Expiry);

internal static class RealtimeSessionToken
{
    public static bool TryValidate(string token, string secret, out RealtimeSessionClaims claims)
    {
        claims = new("", "", 0);
        var parts = token.Split('.', 2);
        if (parts.Length != 2) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0]))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1]))) return false;
        try
        {
            string json = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string deviceId = root.GetProperty("deviceId").GetString() ?? "";
            string role = root.GetProperty("role").GetString() ?? "";
            long expiry = root.GetProperty("exp").GetInt64();
            if (string.IsNullOrWhiteSpace(deviceId) || role is not ("HOST" or "CLIENT") || expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
            claims = new(deviceId, role, expiry);
            return true;
        }
        catch (JsonException) { return false; }
        catch (KeyNotFoundException) { return false; }
        catch (FormatException) { return false; }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
