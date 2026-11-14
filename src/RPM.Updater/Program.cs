using System.Diagnostics;

var options = ParseArgs(args);
if (!options.TryGetValue("--pid", out var pidText) || !int.TryParse(pidText, out var pid) ||
    !options.TryGetValue("--current", out var current) || string.IsNullOrWhiteSpace(current) ||
    !options.TryGetValue("--restart-args", out var restartArgs)) return 2;

try
{
    try
    {
        using var process = Process.GetProcessById(pid);
        if (!process.HasExited && !process.WaitForExit(30000)) return 3;
    }
    catch (ArgumentException) { }

    string backup = current + ".bak";
    if (options.TryGetValue("--update", out var update))
    {
        if (!File.Exists(update) || !IsPeExecutable(update)) return 4;
        Replace(current, update, backup);
    }
    else if (options.TryGetValue("--rollback", out var rollback))
    {
        if (!File.Exists(rollback) || !IsPeExecutable(rollback)) return 5;
        string failed = current + ".failed";
        if (File.Exists(failed)) File.Delete(failed);
        if (File.Exists(current)) File.Move(current, failed);
        File.Move(rollback, current);
    }
    else return 6;

    var start = new ProcessStartInfo(current) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(current) ?? AppContext.BaseDirectory };
    if (!string.IsNullOrWhiteSpace(restartArgs)) start.ArgumentList.Add(restartArgs);
    Process.Start(start);
    return 0;
}
catch
{
    return 10;
}

static void Replace(string current, string update, string backup)
{
    if (File.Exists(backup)) File.Delete(backup);
    if (File.Exists(current)) File.Move(current, backup);
    File.Move(update, current);
}

static bool IsPeExecutable(string path)
{
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);
    if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return false;
    stream.Position = 0x3C;
    int peOffset = reader.ReadInt32();
    if (peOffset < 0 || peOffset + 4 > stream.Length) return false;
    stream.Position = peOffset;
    return reader.ReadUInt32() == 0x00004550;
}

static Dictionary<string, string> ParseArgs(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i + 1 < values.Length; i += 2) result[values[i]] = values[i + 1];
    return result;
}
