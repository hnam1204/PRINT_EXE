using System.Diagnostics;

var options = ParseArgs(args);
if (!options.TryGetValue("--pid", out var pidText) || !int.TryParse(pidText, out var pid) ||
    !options.TryGetValue("--current", out var current) || string.IsNullOrWhiteSpace(current) ||
    !options.TryGetValue("--restart-args", out var restartArgs)) return 2;

bool oldProcessExited = false;
bool updateApplied = false;
try
{
    try
    {
        using var process = Process.GetProcessById(pid);
        if (!process.HasExited && !process.WaitForExit(30000)) return 3;
        oldProcessExited = true;
    }
    catch (ArgumentException) { oldProcessExited = true; }

    if (options.TryGetValue("--update", out var update))
    {
        if (!File.Exists(update) || !IsPeExecutable(update)) return 4;
        Replace(current, update, current + ".bak");
        updateApplied = true;
    }
    else if (options.TryGetValue("--rollback", out var rollback))
    {
        if (!File.Exists(rollback) || !IsPeExecutable(rollback)) return 5;
        Rollback(current, rollback);
    }
    else return 6;

    StartAndValidate(current, restartArgs);
    return 0;
}
catch
{
    if (oldProcessExited)
    {
        if (updateApplied) TryRestoreUpdate(current, current + ".bak");
        TryStart(current, restartArgs);
    }
    return 10;
}

static void TryRestoreUpdate(string current, string backup)
{
    try
    {
        if (!File.Exists(backup) || !IsPeExecutable(backup)) return;
        if (File.Exists(current)) File.Delete(current);
        File.Move(backup, current, true);
    }
    catch { }
}

static void Replace(string current, string update, string backup)
{
    string stagedBackup = backup + $".{Guid.NewGuid():N}.next";
    bool hadCurrent = File.Exists(current);
    if (hadCurrent) File.Copy(current, stagedBackup, false);

    try
    {
        File.Move(update, current, true);
        if (!IsPeExecutable(current)) throw new InvalidDataException("Bản cập nhật sau thay thế không phải PE hợp lệ.");
        if (hadCurrent) File.Move(stagedBackup, backup, true);
    }
    catch
    {
        try
        {
            if (File.Exists(current)) File.Delete(current);
            if (hadCurrent && File.Exists(stagedBackup)) File.Move(stagedBackup, current, true);
        }
        catch { }
        throw;
    }
    finally
    {
        try { if (File.Exists(stagedBackup)) File.Delete(stagedBackup); } catch { }
    }
}

static void Rollback(string current, string rollback)
{
    string failedCurrent = current + $".{Guid.NewGuid():N}.failed";
    File.Move(current, failedCurrent, true);
    try
    {
        File.Move(rollback, current, true);
        if (!IsPeExecutable(current)) throw new InvalidDataException("Bản khôi phục không phải PE hợp lệ.");
    }
    catch
    {
        try
        {
            if (File.Exists(current)) File.Move(current, rollback, true);
            if (File.Exists(failedCurrent)) File.Move(failedCurrent, current, true);
        }
        catch { }
        throw;
    }
}

static void StartAndValidate(string current, string restartArgs)
{
    var process = Process.Start(CreateStartInfo(current, restartArgs))
        ?? throw new InvalidOperationException("Không thể khởi động ứng dụng sau cập nhật.");
    if (process.WaitForExit(3000))
        throw new InvalidOperationException("Ứng dụng kết thúc ngay sau khi cập nhật.");
}

static void TryStart(string current, string restartArgs)
{
    try { Process.Start(CreateStartInfo(current, restartArgs)); } catch { }
}

static ProcessStartInfo CreateStartInfo(string current, string restartArgs)
{
    var start = new ProcessStartInfo(current)
    {
        UseShellExecute = true,
        WorkingDirectory = Path.GetDirectoryName(current) ?? AppContext.BaseDirectory
    };
    if (!string.IsNullOrWhiteSpace(restartArgs)) start.ArgumentList.Add(restartArgs);
    return start;
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
