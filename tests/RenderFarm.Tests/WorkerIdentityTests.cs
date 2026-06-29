using Microsoft.Extensions.Options;
using RenderFarm.Worker.Agent;
using Xunit;

namespace RenderFarm.Tests;

public sealed class WorkerIdentityTests
{
    [Fact]
    public void ManualWorkerIdWinsOverIdentityFile()
    {
        var provider = new WorkerIdentityProvider(Options.Create(new WorkerAgentOptions
        {
            WorkerId = "manual-worker",
            IdentityFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "worker.identity")
        }));

        Assert.Equal("manual-worker", provider.GetWorkerId());
    }

    [Fact]
    public void GeneratedWorkerIdPersistsAcrossProviderInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rf_worker_identity_tests", Guid.NewGuid().ToString("N"));
        var identityFile = Path.Combine(directory, "worker.identity");
        var options = new WorkerAgentOptions { IdentityFilePath = identityFile };

        var first = new WorkerIdentityProvider(Options.Create(options)).GetWorkerId();
        var second = new WorkerIdentityProvider(Options.Create(options)).GetWorkerId();

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
        Assert.Equal(first, File.ReadAllText(identityFile).Trim());
    }
}
