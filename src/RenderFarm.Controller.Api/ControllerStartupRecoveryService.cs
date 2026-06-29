using RenderFarm.Persistence;

namespace RenderFarm.Controller.Api;

/// <summary>
/// Performs one controller-owned reconciliation pass after startup so stale leases and active jobs are not left stranded after a crash or restart.
/// </summary>
public sealed class ControllerStartupRecoveryService(
    IRenderFarmDatabase database,
    IJobScheduler scheduler,
    ILogger<ControllerStartupRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken);
        var recovered = await scheduler.RecoverStartupAsync(cancellationToken);
        if (recovered > 0)
        {
            logger.LogWarning("Controller startup recovery reconciled {RecoveredCount} job/lease record(s)", recovered);
        }
        else
        {
            logger.LogInformation("Controller startup recovery found no stale active jobs or leases");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
