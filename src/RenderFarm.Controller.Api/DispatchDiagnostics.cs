using System.Collections.Concurrent;
using RenderFarm.Domain;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

/// <summary>
/// Recent scheduler decisions used to explain why queued jobs did or did not dispatch.
/// </summary>
public sealed record DispatchDecisionDto(
    DateTimeOffset TimestampUtc,
    string WorkerId,
    string? WorkerName,
    string? WorkerStatus,
    string? WorkerVersion,
    bool WorkerCompatible,
    string? WorkerCompatibilityReason,
    string? JobId,
    string Decision,
    string Reason,
    bool Assigned,
    int? SecondsSinceHeartbeat);

public interface IDispatchDiagnostics
{
    void Record(Worker? worker, string workerId, RenderJob? job, string decision, string reason, bool assigned);
    IReadOnlyList<DispatchDecisionDto> ListRecent(int count = 100);
    DispatchDecisionDto? GetLatestForWorker(string workerId);
    DispatchDecisionDto? GetLatestForJob(string jobId);
}

public sealed class InMemoryDispatchDiagnostics : IDispatchDiagnostics
{
    private const int MaxEntries = 500;
    private readonly ConcurrentQueue<DispatchDecisionDto> _entries = new();

    public void Record(Worker? worker, string workerId, RenderJob? job, string decision, string reason, bool assigned)
    {
        var now = DateTimeOffset.UtcNow;
        var compatibility = worker is null
            ? VersionCompatibility.Incompatible(null, null, null, "Worker is not registered.")
            : RenderFarmVersion.EvaluateWorkerAgent(worker.AgentVersion);
        var entry = new DispatchDecisionDto(
            now,
            worker?.Id ?? workerId,
            worker?.Name,
            worker?.Status.ToString(),
            worker?.AgentVersion,
            compatibility.Compatible,
            compatibility.Reason,
            job?.Id,
            decision,
            reason,
            assigned,
            worker is null ? null : Math.Max(0, (int)(now - worker.LastHeartbeatUtc).TotalSeconds));

        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<DispatchDecisionDto> ListRecent(int count = 100) =>
        _entries.Reverse().Take(Math.Clamp(count, 1, MaxEntries)).ToArray();

    public DispatchDecisionDto? GetLatestForWorker(string workerId) =>
        _entries.Reverse().FirstOrDefault(entry => string.Equals(entry.WorkerId, workerId, StringComparison.OrdinalIgnoreCase));

    public DispatchDecisionDto? GetLatestForJob(string jobId) =>
        _entries.Reverse().FirstOrDefault(entry => string.Equals(entry.JobId, jobId, StringComparison.OrdinalIgnoreCase));
}
