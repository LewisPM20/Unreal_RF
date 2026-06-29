using System.Text.Json;
using Microsoft.Extensions.Options;
using RenderFarm.Shared;

namespace RenderFarm.Worker.Agent;

/// <summary>
/// Durable marker for the worker's currently executing job.
/// </summary>
public sealed record WorkerExecutionState(
    string WorkerId,
    string JobId,
    string AttemptId,
    string LeaseId,
    DateTimeOffset StartedAtUtc,
    string? Message = null);

public interface IWorkerExecutionStateStore
{
    Task<WorkerExecutionState?> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(WorkerExecutionState state, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}

public sealed class WorkerExecutionStateStore(
    IOptions<WorkerAgentOptions> options,
    ILogger<WorkerExecutionStateStore> logger) : IWorkerExecutionStateStore
{
    private readonly string _statePath = ResolveStatePath(options.Value.WorkerStateFilePath);

    public async Task<WorkerExecutionState?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return null;
            }

            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<WorkerExecutionState>(stream, RenderFarmJson.SerializerOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Worker state file {StatePath} is unreadable; ignoring it", _statePath);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not read worker state file {StatePath}", _statePath);
            return null;
        }
    }

    public async Task WriteAsync(WorkerExecutionState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        await using var stream = File.Create(_statePath);
        await JsonSerializer.SerializeAsync(stream, state, RenderFarmJson.SerializerOptions, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not clear worker state file {StatePath}", _statePath);
        }

        return Task.CompletedTask;
    }

    private static string ResolveStatePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, "RenderFarm")
            : Path.Combine(localAppData, "RenderFarm");
        return Path.Combine(root, "worker.current-job.json");
    }
}