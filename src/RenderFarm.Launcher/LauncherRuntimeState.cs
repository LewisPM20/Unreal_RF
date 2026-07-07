using System.IO;
using System.Diagnostics;
using System.Text.Json;

namespace RenderFarm.Launcher;

internal sealed record RuntimeProcessRecord(
    string Role,
    int ProcessId,
    string ExecutablePath,
    DateTimeOffset StartedAtUtc,
    string? DashboardUrl = null,
    string? HealthUrl = null,
    string? WorkerId = null,
    string? ControllerUrl = null,
    string InstallationRoot = "");

internal sealed record RuntimeProcessStatus(RuntimeProcessRecord Record, bool IsRunning, bool IsStale, string Message);

internal static class LauncherRuntimeState
{
    public static string GetDefaultRuntimeFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "RenderFarm", "runtime", "processes.json");
    }

    public static IReadOnlyList<RuntimeProcessRecord> Load(string? path = null)
    {
        var file = path ?? GetDefaultRuntimeFilePath();
        if (!File.Exists(file)) return Array.Empty<RuntimeProcessRecord>();
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<RuntimeProcessRecord>>(File.ReadAllText(file), LauncherJson.Options) ?? Array.Empty<RuntimeProcessRecord>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<RuntimeProcessRecord>();
        }
    }

    public static void Save(IEnumerable<RuntimeProcessRecord> records, string? path = null)
    {
        var file = path ?? GetDefaultRuntimeFilePath();
        var directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(file, JsonSerializer.Serialize(records.OrderBy(x => x.Role).ThenBy(x => x.ProcessId), LauncherJson.Options));
    }

    public static RuntimeProcessRecord Register(string role, Process process, string executablePath, LauncherSettings settings, string? dashboardUrl, string? healthUrl, string? path = null)
    {
        var records = Load(path).Where(record => IsProcessRunning(record.ProcessId)).ToList();
        var record = new RuntimeProcessRecord(
            role.Trim().ToLowerInvariant(),
            process.Id,
            executablePath,
            DateTimeOffset.UtcNow,
            dashboardUrl,
            healthUrl,
            settings.WorkerId,
            settings.ControllerUrl,
            AppContext.BaseDirectory);
        records.RemoveAll(existing => string.Equals(existing.Role, record.Role, StringComparison.OrdinalIgnoreCase) && existing.ProcessId == record.ProcessId);
        records.Add(record);
        Save(records, path);
        return record;
    }

    public static void Remove(int processId, string? path = null)
    {
        Save(Load(path).Where(record => record.ProcessId != processId), path);
    }

    public static IReadOnlyList<RuntimeProcessStatus> Inspect(string? path = null) =>
        Load(path).Select(record =>
        {
            var running = IsProcessRunning(record.ProcessId);
            return new RuntimeProcessStatus(record, running, !running, running ? $"Tracked {record.Role} process is running." : $"Tracked {record.Role} process is no longer running; runtime metadata is stale.");
        }).ToArray();

    public static int ClearStale(string? path = null)
    {
        var records = Load(path).ToArray();
        var alive = records.Where(record => IsProcessRunning(record.ProcessId)).ToArray();
        Save(alive, path);
        return records.Length - alive.Length;
    }

    public static int StopTrackedProcesses(TimeSpan gracefulTimeout, string? path = null)
    {
        var stopped = 0;
        foreach (var record in Load(path).ToArray())
        {
            try
            {
                using var process = Process.GetProcessById(record.ProcessId);
                if (!IsTrackedProcess(record, process))
                {
                    continue;
                }

                if (!process.HasExited)
                {
                    try { process.CloseMainWindow(); }
                    catch (InvalidOperationException) { }
                    if (!process.WaitForExit(gracefulTimeout))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit((int)TimeSpan.FromSeconds(2).TotalMilliseconds);
                    }
                }

                stopped++;
                Remove(record.ProcessId, path);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        ClearStale(path);
        return stopped;
    }
    public static bool IsTrackedProcess(RuntimeProcessRecord record, Process process)
    {
        if (record.ProcessId != process.Id) return false;
        try
        {
            var processPath = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(processPath) || string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(record.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;
        }
    }

    public static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class LauncherSingleInstanceGuard : IDisposable
{
    private readonly Mutex mutex;

    private LauncherSingleInstanceGuard(Mutex mutex) => this.mutex = mutex;

    public static LauncherSingleInstanceGuard? TryAcquire(out string error)
    {
        error = string.Empty;
        var name = "Global\\RenderFarm.Launcher." + PathSafeHash(AppContext.BaseDirectory);
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew) return new LauncherSingleInstanceGuard(mutex);
        mutex.Dispose();
        error = "RenderFarm Launcher is already running for this installation. Use the existing launcher window, or close it before starting another.";
        return null;
    }

    public void Dispose()
    {
        try { mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        mutex.Dispose();
    }

    private static string PathSafeHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(value).ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }
}



