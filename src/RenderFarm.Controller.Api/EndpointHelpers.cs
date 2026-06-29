using System.Text.Json;
using RenderFarm.Domain;
using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

internal static class EndpointValidation
{
    public static IResult? ValidateProject(ProjectProfileDto dto)
    {
        var errors = new Dictionary<string, string[]>();
        AddRequired(errors, nameof(dto.Id), dto.Id);
        AddRequired(errors, nameof(dto.DisplayName), dto.DisplayName);
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    public static IResult? ValidateRenderProfile(RenderProfileDto dto)
    {
        var errors = new Dictionary<string, string[]>();
        AddRequired(errors, nameof(dto.Id), dto.Id);
        AddRequired(errors, nameof(dto.ProjectId), dto.ProjectId);
        AddRequired(errors, nameof(dto.DisplayName), dto.DisplayName);
        AddRequired(errors, nameof(dto.DefaultOutputType), dto.DefaultOutputType);
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    public static IResult? ValidateCreateJob(CreateRenderJobRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        AddRequired(errors, nameof(request.ProjectId), request.ProjectId);
        AddRequired(errors, nameof(request.RenderProfileId), request.RenderProfileId);
        AddRequired(errors, nameof(request.Name), request.Name);
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static void AddRequired(IDictionary<string, string[]> errors, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = [$"{key} is required."];
        }
    }
}

internal static class WorkerStatusProjection
{
    public static string GetEffectiveWorkerStatus(Worker worker, DateTimeOffset now)
    {
        if (worker.Status is WorkerStatus.Pending or WorkerStatus.Rejected)
        {
            return worker.Status.ToString();
        }

        if (worker.Status is WorkerStatus.Offline or WorkerStatus.Disabled or WorkerStatus.Error)
        {
            return worker.Status.ToString();
        }

        if ((now - worker.LastHeartbeatUtc) > TimeSpan.FromSeconds(30))
        {
            return WorkerStatus.Stale.ToString();
        }

        if (!string.IsNullOrWhiteSpace(worker.CurrentJobId) || worker.Status == WorkerStatus.Busy)
        {
            return WorkerStatus.Busy.ToString();
        }

        return worker.Status == WorkerStatus.Unknown ? WorkerStatus.Online.ToString() : worker.Status.ToString();
    }
}

internal static class WorkerApproval
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    private const string Prefix = "worker.approval.";

    public static async Task<string> GetAsync(ISettingsRepository settings, string workerId, CancellationToken cancellationToken)
    {
        var setting = await settings.GetAsync(Prefix + workerId, cancellationToken);
        return Parse(setting?.ValueJson) ?? Pending;
    }

    public static async Task<IReadOnlyDictionary<string, string>> ListAsync(ISettingsRepository settings, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in await settings.ListAsync(cancellationToken))
        {
            if (!setting.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result[setting.Key[Prefix.Length..]] = Parse(setting.ValueJson) ?? Pending;
        }

        return result;
    }

    public static string GetEffective(IReadOnlyDictionary<string, string> approvals, Worker worker)
    {
        if (approvals.TryGetValue(worker.Id, out var approval))
        {
            return approval;
        }

        return worker.Status switch
        {
            WorkerStatus.Pending => Pending,
            WorkerStatus.Rejected => Rejected,
            _ => Accepted
        };
    }

    public static Task SetAsync(ISettingsRepository settings, string workerId, string approval, CancellationToken cancellationToken) =>
        settings.UpsertAsync(new FarmSetting(Prefix + workerId, JsonSerializer.Serialize(approval), DateTimeOffset.UtcNow), cancellationToken);

    private static string? Parse(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(valueJson)?.Trim().ToLowerInvariant();
        }
        catch (JsonException)
        {
            return valueJson.Trim().Trim('"').ToLowerInvariant();
        }
    }
}

internal static class WorkerScheduling
{
    private const string Prefix = "worker.scheduling.";

    public static async Task<WorkerSchedulingMode> GetAsync(ISettingsRepository settings, string workerId, CancellationToken cancellationToken)
    {
        var setting = await settings.GetAsync(Prefix + workerId, cancellationToken);
        return Parse(setting?.ValueJson);
    }

    public static async Task<IReadOnlyDictionary<string, WorkerSchedulingMode>> ListAsync(ISettingsRepository settings, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, WorkerSchedulingMode>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in await settings.ListAsync(cancellationToken))
        {
            if (!setting.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result[setting.Key[Prefix.Length..]] = Parse(setting.ValueJson);
        }

        return result;
    }

    public static WorkerSchedulingMode GetEffective(IReadOnlyDictionary<string, WorkerSchedulingMode> modes, Worker worker) =>
        modes.TryGetValue(worker.Id, out var mode) ? mode : WorkerSchedulingMode.Active;

    public static Task SetAsync(ISettingsRepository settings, string workerId, WorkerSchedulingMode mode, CancellationToken cancellationToken) =>
        settings.UpsertAsync(new FarmSetting(Prefix + workerId, JsonSerializer.Serialize(mode, RenderFarmJson.SerializerOptions), DateTimeOffset.UtcNow), cancellationToken);

    private static WorkerSchedulingMode Parse(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return WorkerSchedulingMode.Active;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkerSchedulingMode>(valueJson, RenderFarmJson.SerializerOptions);
        }
        catch (JsonException)
        {
            return Enum.TryParse<WorkerSchedulingMode>(valueJson.Trim().Trim('"'), true, out var parsed) ? parsed : WorkerSchedulingMode.Active;
        }
    }
}

public sealed record QueueSettingsRequest(bool Enabled);
