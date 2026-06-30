using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public interface IActivityLog
{
    void Add(string severity, string type, string title, string message, string? workerId = null, string? jobId = null, string? projectId = null, string? renderProfileId = null, string? actionRoute = null);

    IReadOnlyList<ActivityItemDto> ListRecent(int limit = 100);
}

public sealed class InMemoryActivityLog : IActivityLog
{
    private const int Capacity = 100;
    private readonly object syncRoot = new();
    private readonly Queue<ActivityItemDto> items = new();

    public InMemoryActivityLog()
    {
        Add("success", "Controller", "Controller online", "C# controller started and the dashboard is available.", actionRoute: "/");
    }

    public void Add(string severity, string type, string title, string message, string? workerId = null, string? jobId = null, string? projectId = null, string? renderProfileId = null, string? actionRoute = null)
    {
        var item = new ActivityItemDto(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            NormalizeSeverity(severity),
            string.IsNullOrWhiteSpace(type) ? "Activity" : type.Trim(),
            string.IsNullOrWhiteSpace(title) ? "Farm activity" : title.Trim(),
            string.IsNullOrWhiteSpace(message) ? "Controller activity recorded." : message.Trim(),
            workerId,
            jobId,
            projectId,
            renderProfileId,
            actionRoute);

        lock (syncRoot)
        {
            items.Enqueue(item);
            while (items.Count > Capacity)
            {
                items.Dequeue();
            }
        }
    }

    public IReadOnlyList<ActivityItemDto> ListRecent(int limit = 100)
    {
        var take = Math.Clamp(limit, 1, Capacity);
        lock (syncRoot)
        {
            return items.Reverse().Take(take).ToArray();
        }
    }

    private static string NormalizeSeverity(string severity)
    {
        return severity.Trim().ToLowerInvariant() switch
        {
            "success" or "good" => "success",
            "warning" or "warn" => "warning",
            "error" or "bad" or "failure" => "error",
            _ => "info"
        };
    }
}

public static class ActivityEndpoints
{
    public static IEndpointRouteBuilder MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/activity").WithTags("Activity");
        group.MapGet("/recent", (int? limit, IActivityLog activity, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store, max-age=0";
            return Results.Ok(activity.ListRecent(limit ?? 100));
        });

        return app;
    }
}
