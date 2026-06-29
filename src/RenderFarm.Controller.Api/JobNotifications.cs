using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

/// <summary>
/// Optional generic webhook notification settings for terminal job states.
/// </summary>
public sealed class NotificationOptions
{
    public bool Enabled { get; set; }
    public string? WebhookUrl { get; set; }
    public bool NotifyOnSucceeded { get; set; } = true;
    public bool NotifyOnFailed { get; set; } = true;
    public bool NotifyOnCancelled { get; set; } = true;
}

public interface IJobNotificationSink
{
    Task NotifyTerminalAsync(JobNotificationPayloadDto payload, CancellationToken cancellationToken);
}

public sealed class NullJobNotificationSink : IJobNotificationSink
{
    public static NullJobNotificationSink Instance { get; } = new();

    private NullJobNotificationSink()
    {
    }

    public Task NotifyTerminalAsync(JobNotificationPayloadDto payload, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class WebhookJobNotificationSink(
    IHttpClientFactory httpClientFactory,
    IOptions<NotificationOptions> options,
    ILogger<WebhookJobNotificationSink> logger) : IJobNotificationSink
{
    public async Task NotifyTerminalAsync(JobNotificationPayloadDto payload, CancellationToken cancellationToken)
    {
        var config = options.Value;
        if (!ShouldNotify(config, payload) || string.IsNullOrWhiteSpace(config.WebhookUrl))
        {
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.PostAsJsonAsync(config.WebhookUrl, payload, RenderFarmJson.SerializerOptions, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send terminal job notification for job {JobId}", payload.JobId);
        }
    }

    private static bool ShouldNotify(NotificationOptions config, JobNotificationPayloadDto payload) =>
        config.Enabled && (payload.FinalState switch
        {
            RenderFarm.Domain.JobState.Succeeded => config.NotifyOnSucceeded,
            RenderFarm.Domain.JobState.Failed => config.NotifyOnFailed,
            RenderFarm.Domain.JobState.Cancelled => config.NotifyOnCancelled,
            _ => false
        });
}
