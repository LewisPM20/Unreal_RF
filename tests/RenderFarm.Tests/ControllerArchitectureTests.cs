using Xunit;

namespace RenderFarm.Tests;

public sealed class ControllerArchitectureTests
{
    [Fact]
    public void ProgramUsesEndpointGroupExtensions()
    {
        var repoRoot = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(repoRoot, "src", "RenderFarm.Controller.Api", "Program.cs"));

        Assert.Contains("MapSystemEndpoints", program);
        Assert.Contains("MapWorkerEndpoints", program);
        Assert.Contains("MapProjectEndpoints", program);
        Assert.Contains("MapRenderProfileEndpoints", program);
        Assert.Contains("MapJobEndpoints", program);
        Assert.Contains("MapSettingsEndpoints", program);
        Assert.DoesNotContain(@"app.MapPost(""/api/workers/heartbeat""", program);
    }

    [Fact]
    public void ProgramRegistersStartupAndPeriodicRecoveryServices()
    {
        var repoRoot = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(repoRoot, "src", "RenderFarm.Controller.Api", "Program.cs"));

        Assert.Contains("ControllerStartupRecoveryService", program);
        Assert.Contains("ControllerLeaseRecoveryService", program);
    }

    [Fact]
    public void EndpointGroupFilesExist()
    {
        var repoRoot = FindRepoRoot();
        var endpointFiles = new[]
        {
            "SystemEndpoints.cs",
            "WorkerEndpoints.cs",
            "ProjectEndpoints.cs",
            "RenderProfileEndpoints.cs",
            "JobEndpoints.cs",
            "SettingsEndpoints.cs"
        };

        foreach (var file in endpointFiles)
        {
            Assert.True(File.Exists(Path.Combine(repoRoot, "src", "RenderFarm.Controller.Api", file)), file);
        }
    }


    [Fact]
    public void ProgramAcquiresSingleInstanceGuardBeforeStartup()
    {
        var repoRoot = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(repoRoot, "src", "RenderFarm.Controller.Api", "Program.cs"));
        var guard = File.ReadAllText(Path.Combine(repoRoot, "src", "RenderFarm.Controller.Api", "ControllerSingleInstanceGuard.cs"));

        Assert.Contains("ControllerSingleInstanceGuard.TryAcquire", program);
        Assert.Contains("Environment.ExitCode = 2", program);
        Assert.Contains("new Mutex", guard);
    }    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RenderFarm.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root containing RenderFarm.sln.");
    }
}

