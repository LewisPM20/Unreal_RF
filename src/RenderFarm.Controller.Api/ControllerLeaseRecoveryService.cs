using Microsoft.Extensions.Options;

namespace RenderFarm.Controller.Api;

/// <summary>
/// Periodically asks the scheduler to expire leases and promote due retries so abandoned jobs recover without manual intervention.
/// </summary>
public sealed class ControllerLeaseRecoveryService(
    IJobScheduler scheduler,
    IOptions<JobSchedulerOptions> options,
    ILogger<ControllerLeaseRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.LeaseRecoverySeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var expired = await scheduler.ExpireLeasesAsync(stoppingToken);
                if (expired > 0)
                {
                    logger.LogWarning("Controller lease recovery expired {ExpiredCount} stale lease(s)", expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Controller lease recovery pass failed");
            }
        }
    }
}
