using System.Threading.Channels;
using Npgsql;

internal sealed class PostgresEventNotifier
{
    private const string ChannelName = "rpm_realtime_events";
    private readonly string _connectionString;
    private readonly Channel<bool> _notifications = Channel.CreateUnbounded<bool>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public PostgresEventNotifier(string connectionString) => _connectionString = connectionString;

    public ChannelReader<bool> Notifications => _notifications.Reader;

    public async Task ListenAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        connection.Notification += (_, _) => _notifications.Writer.TryWrite(true);

        await using (var command = new NpgsqlCommand($"LISTEN {ChannelName};", connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await connection.WaitAsync(cancellationToken);
        }
    }
}
