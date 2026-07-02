using System.Security.Cryptography;
using System.Text;

namespace RenderFarm.Controller.Api;

/// <summary>
/// Windows-friendly named mutex guard that prevents multiple controller schedulers from the same installation fighting over one farm.
/// </summary>
public sealed class ControllerSingleInstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    private bool disposed;

    private ControllerSingleInstanceGuard(Mutex mutex, string name, string installationRoot)
    {
        this.mutex = mutex;
        Name = name;
        InstallationRoot = installationRoot;
        AcquiredAtUtc = DateTimeOffset.UtcNow;
    }

    public string Name { get; }

    public string InstallationRoot { get; }

    public DateTimeOffset AcquiredAtUtc { get; }

    public static ControllerSingleInstanceGuard? Current { get; private set; }

    public static ControllerSingleInstanceGuard? TryAcquire(out string error)
    {
        var installationRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = "Global\\RenderFarm.Controller.Api." + StableHash(installationRoot);
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            if (!createdNew)
            {
                error = $"Another RenderFarm controller instance is already running for this installation: {installationRoot}. Close it or reuse the existing dashboard before starting a new controller.";
                mutex.Dispose();
                return null;
            }

            error = string.Empty;
            Current = new ControllerSingleInstanceGuard(mutex, name, installationRoot);
            return Current;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            mutex?.Dispose();
            error = $"Could not create the RenderFarm controller single-instance guard: {ex.Message}";
            return null;
        }
    }

    public static object? BuildDiagnostics() => Current is null
        ? null
        : new
        {
            current = true,
            mutexName = Current.Name,
            installationRoot = Current.InstallationRoot,
            acquiredAtUtc = Current.AcquiredAtUtc
        };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        mutex.Dispose();
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }

    private static string StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
