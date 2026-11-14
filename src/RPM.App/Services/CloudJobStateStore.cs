using System.Text.Json;
using RPM.Core.Config;

namespace RPM.App.Services;

/// <summary>
/// Durable local journal used to avoid replaying a Cloud Job after a Host restart.
/// A journal entry is conservative: once dispatch began, recovery reports UNKNOWN.
/// </summary>
internal sealed class CloudJobStateStore
{
    private readonly string _path = ConfigManager.GetCloudJobStatePath();
    private readonly object _gate = new();
    private Dictionary<string, string> _states = new(StringComparer.Ordinal);

    public CloudJobStateStore() => Load();

    public bool TryBegin(string jobId)
    {
        lock (_gate)
        {
            if (_states.ContainsKey(jobId)) return false;
            _states[jobId] = "DISPATCHING";
            Save();
            return true;
        }
    }

    public void MarkUnknown(string jobId)
    {
        lock (_gate)
        {
            _states[jobId] = "UNKNOWN";
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _states = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch { _states = new Dictionary<string, string>(StringComparer.Ordinal); }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_states));
        File.Move(tempPath, _path, true);
    }
}
