using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RenderFarm.Worker.Agent;
using Xunit;

namespace RenderFarm.Tests;

public sealed class WorkerExecutionStateStoreTests
{
    [Fact]
    public async Task WorkerExecutionStateRoundTripsAndClears()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rf_worker_state_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "state.json");
        var store = new WorkerExecutionStateStore(
            Options.Create(new WorkerAgentOptions { WorkerStateFilePath = statePath }),
            NullLogger<WorkerExecutionStateStore>.Instance);
        var state = new WorkerExecutionState("worker-1", "job-1", "attempt-1", "lease-1", DateTimeOffset.UtcNow, "running");

        await store.WriteAsync(state, CancellationToken.None);
        var loaded = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(state.WorkerId, loaded?.WorkerId);
        Assert.Equal(state.JobId, loaded?.JobId);
        Assert.Equal(state.AttemptId, loaded?.AttemptId);
        Assert.Equal(state.LeaseId, loaded?.LeaseId);

        await store.ClearAsync(CancellationToken.None);
        Assert.Null(await store.ReadAsync(CancellationToken.None));
    }
}