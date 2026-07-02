using RenderFarm.Persistence;

namespace RenderFarm.Controller.Api;

/// <summary>
/// Performs one controller-owned reconciliation pass after startup so stale leases and active jobs are not left stranded after a crash or restart.
/// </summary>
public sealed class ControllerStartupRecoveryService(
    IRenderFarmDatabase database,
    IJobScheduler scheduler,
    IActivityLog activity,
    ILogger<ControllerStartupRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken);
        activity.Add("info", "Recovery", "Startup reconciliation started", "Controller is checking active leases and in-progress jobs before scheduling new work.", actionRoute: "diagnostics");
        var recovered = await scheduler.RecoverStartupAsync(cancellationToken);
        if (recovered > 0)
        {
            logger.LogWarning("Controller startup recovery reconciled {RecoveredCount} job/lease record(s)", recovered);
            activity.Add("warning", "Recovery", "Startup reconciliation repaired jobs", $"Controller reconciled {recovered} stale job/lease record(s).", actionRoute: "diagnostics");
        }
        else
        {
            logger.LogInformation("Controller startup recovery found no stale active jobs or leases");
            activity.Add("success", "Recovery", "Startup reconciliation clean", "No stale active jobs or leases were found.", actionRoute: "diagnostics");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}



