using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace RenderFarm.Worker.Agent;

/// <summary>
/// Prevents accidental duplicate worker agents using the same worker identity on one Windows machine.
/// </summary>
public sealed class WorkerSingleInstanceService(
    IWorkerIdentityProvider identityProvider,
    IOptions<WorkerAgentOptions> options,
    ILogger<WorkerSingleInstanceService> logger) : IHostedService, IDisposable
{
    private Mutex? mutex;

    public static string BuildMutexName(string workerId)
    {
        var normalized = string.IsNullOrWhiteSpace(workerId) ? "unknown" : workerId.Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $"Global\\RenderFarm.Worker.Agent.{hash}";
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.AllowDuplicateWorkerInstance)
        {
            logger.LogWarning("Duplicate worker instance protection is disabled by configuration for testing.");
            return Task.CompletedTask;
        }

        var workerId = identityProvider.GetWorkerId();
        var name = BuildMutexName(workerId);
        mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            mutex = null;
            throw new InvalidOperationException($"A RenderFarm worker with identity '{workerId}' is already running on this machine. Stop the existing worker or set RenderFarm:AllowDuplicateWorkerInstance=true only for controlled testing.");
        }

        logger.LogInformation("Acquired RenderFarm worker single-instance guard for {WorkerId}", workerId);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        finally
        {
            mutex.Dispose();
            mutex = null;
        }
    }
}
