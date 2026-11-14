using RPM.Networking;

namespace RPM.Relay;

internal class Program
{
    static async Task Main(string[] args)
    {
        int port = 9090;
        if (args.Length > 0 && int.TryParse(args[0], out int customPort))
        {
            port = customPort;
        }

        Console.Title = $"RPM Cloud Relay Broker - Port {port}";
        Console.WriteLine("==================================================");
        Console.WriteLine("  REMOTE PRINT MANAGER (RPM) - CLOUD RELAY BROKER ");
        Console.WriteLine("==================================================");

        var server = new RelayServer();
        try
        {
            server.Start(port);
            Console.WriteLine($"[INFO] Relay broker listening on internal HTTP/WebSocket port {port}. Put it behind a TLS reverse proxy and expose only https:// / wss:// externally.");
            Console.WriteLine("[INFO] Press Ctrl+C to terminate server.");

            var tcs = new TaskCompletionSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                server.Stop();
                tcs.SetResult();
            };

            await tcs.Task;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] Error starting relay: {ex.Message}");
        }
    }
}
