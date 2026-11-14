using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RPM.Core.Protocol;

namespace RPM.Relay;

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
            // If port already in use or prefix conflict
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

                    if (path == "/version.json")
                    {
                        string versionFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.json");
                        if (!File.Exists(versionFile))
                        {
                            versionFile = Path.Combine(Directory.GetCurrentDirectory(), "version.json");
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
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                byte[] packetBytes = ms.ToArray();
                string json = Encoding.UTF8.GetString(packetBytes);
                var packet = JsonSerializer.Deserialize<NetworkPacket>(json);

                if (packet == null) continue;

                // Handle Room Registration & Routing
                switch (packet.Type)
                {
                    case MessageType.RegisterServer:
                    {
                        isServer = true;
                        currentRoomCode = packet.PairingCode;
                        currentDeviceId = packet.SenderId;

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
                            // Forward packet to Server
                            await SendToSocketAsync(room.ServerSocket, packet);
                        }
                        else
                        {
                            // Server not found or offline
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
                        // Route packet based on TargetId
                        if (currentRoomCode != null && _rooms.TryGetValue(currentRoomCode, out var room))
                        {
                            if (!string.IsNullOrEmpty(packet.TargetId))
                            {
                                if (packet.TargetId == room.ServerDeviceId && room.ServerSocket != null)
                                {
                                    // Forward to Server
                                    await SendToSocketAsync(room.ServerSocket, packet);
                                }
                                else if (room.Clients.TryGetValue(packet.TargetId, out var clientSession))
                                {
                                    // Forward to specific Client
                                    await SendToSocketAsync(clientSession.Socket, packet);
                                }
                            }
                            else if (isServer)
                            {
                                // Broadcast from Server to all room clients
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
            // Clean up session
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
        string json = JsonSerializer.Serialize(packet);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
