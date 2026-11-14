using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RPM.Core.Protocol;
using RPM.Core.Security;

namespace RPM.Networking;

public class ClientSession
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public WebSocket Socket { get; set; } = null!;
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
}

public class Room
{
    public string PairingCode { get; set; } = string.Empty;
    public string ServerDeviceId { get; set; } = string.Empty;
    public string ServerDeviceName { get; set; } = string.Empty;
    public WebSocket? ServerSocket { get; set; }
    public ConcurrentDictionary<string, ClientSession> Clients { get; } = new();
    public ConcurrentDictionary<string, DateTime> SeenPacketIds { get; } = new();
}

public class RelayServer
{
    private HttpListener? _listener;
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private bool _isRunning;

    public void Start(int port = 9090)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            _listener.Close();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
        }

        _isRunning = true;
        Console.WriteLine($"[RPM Relay] Server listening on port {port}...");
        Task.Run(AcceptLoopAsync);
    }

    public void Stop()
    {
        _isRunning = false;
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
    }

    private async Task AcceptLoopAsync()
    {
        while (_isRunning && _listener != null && _listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();

                if (ctx.Request.IsWebSocketRequest)
                {
                    _ = ProcessWebSocketContextAsync(ctx);
                }
                else
                {
                    string path = ctx.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "/";

                    if (path.EndsWith(".json"))
                    {
                        string requestedJson = Path.GetFileName(ctx.Request.Url!.LocalPath);
                        string versionFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, requestedJson);
                        if (!File.Exists(versionFile))
                        {
                            versionFile = Path.Combine(Directory.GetCurrentDirectory(), requestedJson);
                        }

                        if (File.Exists(versionFile))
                        {
                            byte[] vBytes = await File.ReadAllBytesAsync(versionFile);
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            ctx.Response.ContentLength64 = vBytes.Length;
                            ctx.Response.OutputStream.Write(vBytes, 0, vBytes.Length);
                            ctx.Response.Close();
                            continue;
                        }
                    }
                    else if (path.StartsWith("/download/"))
                    {
                        string fileName = Path.GetFileName(ctx.Request.Url!.LocalPath);
                        string downloadFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates", fileName);
                        if (!File.Exists(downloadFile))
                        {
                            downloadFile = Path.Combine(Directory.GetCurrentDirectory(), "updates", fileName);
                        }

                        if (File.Exists(downloadFile))
                        {
                            byte[] fBytes = await File.ReadAllBytesAsync(downloadFile);
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/octet-stream";
                            ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");
                            ctx.Response.ContentLength64 = fBytes.Length;
                            ctx.Response.OutputStream.Write(fBytes, 0, fBytes.Length);
                            ctx.Response.Close();
                            continue;
                        }
                    }

                    // HTTP Health Check
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    var status = new
                    {
                        Service = "Remote Print Manager Relay Broker",
                        Version = "1.0.0",
                        ActiveRooms = _rooms.Count,
                        Status = "Running",
                        Timestamp = DateTime.UtcNow
                    };
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(status));
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
            }
            catch (Exception ex)
            {
                if (!_isRunning) break;
                Console.WriteLine($"[Relay Error] Accept loop: {ex.Message}");
            }
        }
    }

    private async Task ProcessWebSocketContextAsync(HttpListenerContext ctx)
    {
        WebSocketContext? wsContext = null;
        try
        {
            wsContext = await ctx.AcceptWebSocketAsync(subProtocol: null);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            Console.WriteLine($"[Relay] WS Handshake failed: {ex.Message}");
            return;
        }

        var socket = wsContext.WebSocket;
        string? currentRoomCode = null;
        string? currentDeviceId = null;
        bool isServer = false;

        var buffer = new byte[64 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                        return;
                    }
                    if (ms.Length + result.Count > PacketSecurity.MaxNetworkPacketBytes)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Packet too large", CancellationToken.None);
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                byte[] packetBytes = ms.ToArray();
                string json = Encoding.UTF8.GetString(packetBytes);
                var packet = JsonSerializer.Deserialize<NetworkPacket>(json);

                if (packet == null) continue;

                if (!PacketSecurity.IsValidPairingCode(packet.PairingCode) ||
                    !PacketSecurity.VerifyAuthTag(packet, packet.PairingCode) ||
                    !PacketSecurity.IsFresh(packet, DateTime.UtcNow) ||
                    !Guid.TryParseExact(packet.PacketId, "N", out _))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid packet authentication", CancellationToken.None);
                    return;
                }

                if (!_rooms.TryGetValue(packet.PairingCode, out var packetRoom))
                {
                    if (packet.Type != MessageType.RegisterServer) continue;
                }
                else if (!packetRoom.SeenPacketIds.TryAdd(packet.PacketId, DateTime.UtcNow))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Replay detected", CancellationToken.None);
                    return;
                }
                else if (packetRoom.SeenPacketIds.Count > 10_000)
                {
                    foreach (var oldPacket in packetRoom.SeenPacketIds
                        .Where(x => x.Value < DateTime.UtcNow.AddMinutes(-10))
                        .Take(2_000))
                    {
                        packetRoom.SeenPacketIds.TryRemove(oldPacket.Key, out _);
                    }
                }

                if (currentDeviceId != null && !string.Equals(currentDeviceId, packet.SenderId, StringComparison.Ordinal))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Sender identity changed", CancellationToken.None);
                    return;
                }

                // Handle Room Registration & Routing
                switch (packet.Type)
                {
                    case MessageType.RegisterServer:
                    {
                        isServer = true;
                        currentRoomCode = packet.PairingCode;
                        currentDeviceId = packet.SenderId;

                        if (_rooms.TryGetValue(packet.PairingCode, out var existingRoom) &&
                            existingRoom.ServerSocket != null &&
                            existingRoom.ServerSocket.State == WebSocketState.Open &&
                            !string.Equals(existingRoom.ServerDeviceId, packet.SenderId, StringComparison.Ordinal))
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Room already has a server", CancellationToken.None);
                            return;
                        }

                        var room = _rooms.GetOrAdd(packet.PairingCode, code => new Room
                        {
                            PairingCode = code,
                            ServerDeviceId = packet.SenderId,
                            ServerSocket = socket
                        });

                        room.ServerSocket = socket;
                        room.ServerDeviceId = packet.SenderId;

                        Console.WriteLine($"[Relay] Máy chủ đã đăng ký Room: {packet.PairingCode} (Device: {packet.SenderId})");

                        // Reply Ack
                        var ack = new NetworkPacket
                        {
                            Type = MessageType.RegisterServerAck,
                            PairingCode = packet.PairingCode,
                            TargetId = packet.SenderId,
                            PayloadJson = "{\"status\":\"registered\"}"
                        };
                        await SendToSocketAsync(socket, ack);
                        break;
                    }

                    case MessageType.ClientJoin:
                    {
                        currentRoomCode = packet.PairingCode;
                        currentDeviceId = packet.SenderId;

                        if (_rooms.TryGetValue(packet.PairingCode, out var room) && room.ServerSocket != null && room.ServerSocket.State == WebSocketState.Open)
                        {
                            var session = new ClientSession
                            {
                                DeviceId = packet.SenderId,
                                Socket = socket
                            };
                            room.Clients[packet.SenderId] = session;

                            Console.WriteLine($"[Relay] Máy con ({packet.SenderId}) gửi yêu cầu ghép đôi tới Room {packet.PairingCode}");
                            await SendToSocketAsync(room.ServerSocket, packet);
                        }
                        else
                        {
                            var errorPkt = new NetworkPacket
                            {
                                Type = MessageType.Error,
                                TargetId = packet.SenderId,
                                PayloadJson = "{\"error\":\"Máy chủ không trực tuyến hoặc mã ghép đôi không đúng.\"}"
                            };
                            await SendToSocketAsync(socket, errorPkt);
                        }
                        break;
                    }

                    default:
                    {
                        if (currentRoomCode != null && _rooms.TryGetValue(currentRoomCode, out var room))
                        {
                            if (!string.IsNullOrEmpty(packet.TargetId))
                            {
                                if (packet.TargetId == room.ServerDeviceId && room.ServerSocket != null)
                                {
                                    await SendToSocketAsync(room.ServerSocket, packet);
                                }
                                else if (room.Clients.TryGetValue(packet.TargetId, out var clientSession))
                                {
                                    await SendToSocketAsync(clientSession.Socket, packet);
                                }
                            }
                            else if (isServer)
                            {
                                foreach (var client in room.Clients.Values)
                                {
                                    await SendToSocketAsync(client.Socket, packet);
                                }
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] Socket session ended: {ex.Message}");
        }
        finally
        {
            if (currentRoomCode != null && _rooms.TryGetValue(currentRoomCode, out var room))
            {
                if (isServer)
                {
                    Console.WriteLine($"[Relay] Server {currentDeviceId} disconnected from Room {currentRoomCode}");
                    room.ServerSocket = null;
                }
                else if (currentDeviceId != null)
                {
                    Console.WriteLine($"[Relay] Client {currentDeviceId} disconnected from Room {currentRoomCode}");
                    room.Clients.TryRemove(currentDeviceId, out _);
                }
            }

            try
            {
                socket.Dispose();
            }
            catch { }
        }
    }

    private static async Task SendToSocketAsync(WebSocket socket, NetworkPacket packet)
    {
        if (socket.State != WebSocketState.Open) return;
        if (!PacketSecurity.IsValidPairingCode(packet.PairingCode)) return;
        packet.Timestamp = DateTime.UtcNow;
        packet.AuthTagBase64 = PacketSecurity.ComputeAuthTag(packet, packet.PairingCode);
        string json = JsonSerializer.Serialize(packet);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}

