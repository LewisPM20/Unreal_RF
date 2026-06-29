using RenderFarm.Controller.Api;
using RenderFarm.Domain;
using Xunit;

namespace RenderFarm.Tests;

using DomainWorker = RenderFarm.Domain.Worker;

public sealed class WorkerReadinessEvaluatorTests
{
    [Fact]
    public void MissingRequirementsPreserveCurrentBehaviour()
    {
        var result = WorkerReadinessEvaluator.Evaluate(CreateWorker(), CreateProject(), CreateProfile());

        Assert.True(result.CanRun);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void MinCpuCoresRequirementBlocksSmallWorker()
    {
        var result = WorkerReadinessEvaluator.Evaluate(CreateWorker(cpuCores: 8), CreateProject(), CreateProfile(new Dictionary<string, string> { ["minCpuCores"] = "16" }));

        Assert.False(result.CanRun);
        Assert.Contains(result.Reasons, reason => reason.Contains("CPU cores 8", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MinRamGbRequirementBlocksSmallWorker()
    {
        var result = WorkerReadinessEvaluator.Evaluate(CreateWorker(ramGb: 32), CreateProject(), CreateProfile(new Dictionary<string, string> { ["minRamGb"] = "64" }));

        Assert.False(result.CanRun);
        Assert.Contains(result.Reasons, reason => reason.Contains("RAM 32", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MinVramGbRequirementBlocksSmallWorker()
    {
        var result = WorkerReadinessEvaluator.Evaluate(CreateWorker(vramGb: 8), CreateProject(), CreateProfile(new Dictionary<string, string> { ["minVramGb"] = "12" }));

        Assert.False(result.CanRun);
        Assert.Contains(result.Reasons, reason => reason.Contains("VRAM 8", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GpuNameContainsRequirementBlocksMismatchedWorker()
    {
        var result = WorkerReadinessEvaluator.Evaluate(CreateWorker(gpuName: "NVIDIA RTX 3080"), CreateProject(), CreateProfile(new Dictionary<string, string> { ["gpuNameContains"] = "RTX 4090" }));

        Assert.False(result.CanRun);
        Assert.Contains(result.Reasons, reason => reason.Contains("does not contain RTX 4090", StringComparison.OrdinalIgnoreCase));
    }

    private static DomainWorker CreateWorker(int? cpuCores = 16, double? ramGb = 64, string? gpuName = "NVIDIA RTX 4090", double? vramGb = 24) =>
        new(
            "worker-1",
            "Worker 1",
            "host",
            "127.0.0.1",
            null,
            WorkerStatus.Idle,
            null,
            null,
            "test",
            new WorkerCapabilities(
                cpuCores,
                ramGb,
                gpuName,
                vramGb,
                500,
                [new UnrealEngineInstallation("5.7", "C:\\UE_5.7", "C:\\UE_5.7\\Engine\\Binaries\\Win64\\UnrealEditor-Cmd.exe", true)],
                [new ProjectPathStatus("D:\\Projects\\Demo\\Demo.uproject", true)],
                [new SharedOutputStatus("\\\\server\\renders", true, true, 100)]),
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static ProjectProfile CreateProject() =>
        new(
            "project-1",
            "Demo",
            "D:\\Projects\\Demo\\Demo.uproject",
            "5.7",
            ["5.7"],
            null,
            [new WorkerProjectPath("path-1", "project-1", "worker-1", "C:\\UE_5.7", "D:\\Projects\\Demo\\Demo.uproject", null)]);

    private static RenderProfile CreateProfile(IReadOnlyDictionary<string, string>? settings = null) =>
        new("profile-1", "project-1", "Profile", RenderProfileType.MrqQueue, "/Game/MRQ", null, "png", false, settings ?? new Dictionary<string, string>());
}
