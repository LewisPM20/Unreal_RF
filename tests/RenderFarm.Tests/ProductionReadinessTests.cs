using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RenderFarm.Domain;
using RenderFarm.Launcher;
using RenderFarm.Shared;
using RenderFarm.Worker.Agent;
using Xunit;

namespace RenderFarm.Tests;

public sealed class ProductionReadinessTests
{
    [Fact]
    public void RuntimeMetadataRoundTripsAndClearsStaleRecords()
    {
        var file = Path.Combine(Path.GetTempPath(), "rf_runtime_tests", Guid.NewGuid().ToString("N"), "processes.json");
        var record = new RuntimeProcessRecord("controller", -12345, "C:\\RenderFarm\\controller.exe", DateTimeOffset.UtcNow, "http://127.0.0.1:9200/", "http://127.0.0.1:9200/health");

        LauncherRuntimeState.Save([record], file);
        var loaded = LauncherRuntimeState.Load(file);
        var removed = LauncherRuntimeState.ClearStale(file);

        Assert.Single(loaded);
        Assert.Equal("controller", loaded[0].Role);
        Assert.Equal(1, removed);
        Assert.Empty(LauncherRuntimeState.Load(file));
    }

    [Fact]
    public void RuntimeMetadataDoesNotTreatDifferentPidAsTrackedProcess()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var record = new RuntimeProcessRecord("controller", process.Id + 100000, process.MainModule?.FileName ?? "dotnet.exe", DateTimeOffset.UtcNow);

        Assert.False(LauncherRuntimeState.IsTrackedProcess(record, process));
    }

    [Fact]
    public void WorkerSingleInstanceMutexNameIsStablePerIdentity()
    {
        var left = WorkerSingleInstanceService.BuildMutexName("worker-01");
        var right = WorkerSingleInstanceService.BuildMutexName("WORKER-01");
        var other = WorkerSingleInstanceService.BuildMutexName("worker-02");

        Assert.Equal(left, right);
        Assert.NotEqual(left, other);
        Assert.StartsWith("Global\\RenderFarm.Worker.Agent.", left, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputValidationAcceptsSingleMovWithoutPng()
    {
        var root = CreateTempRoot();
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "Shot01.mov"), [1, 2, 3]);
        var request = BuildRequest(root, output, "mov");

        var result = RenderOutputValidator.Validate(request, "");

        Assert.Equal(OutputValidationStatus.Passed, result.Status);
        Assert.Equal(OutputValidationMode.SingleVideo, result.Mode);
        Assert.Contains("mov", result.DetectedExtensions);
    }

    [Fact]
    public void OutputValidationAcceptsPngAndExrSequences()
    {
        var root = CreateTempRoot();
        var pngOutput = Path.Combine(root, "png");
        var exrOutput = Path.Combine(root, "exr");
        Directory.CreateDirectory(pngOutput);
        Directory.CreateDirectory(exrOutput);
        File.WriteAllBytes(Path.Combine(pngOutput, "Shot_0001.png"), [1]);
        File.WriteAllBytes(Path.Combine(pngOutput, "Shot_0002.png"), [1]);
        File.WriteAllBytes(Path.Combine(exrOutput, "Shot_0001.exr"), [1]);

        var png = RenderOutputValidator.Validate(BuildRequest(root, pngOutput, "png"), "");
        var exr = RenderOutputValidator.Validate(BuildRequest(root, exrOutput, "exr"), "");

        Assert.Equal(OutputValidationStatus.Passed, png.Status);
        Assert.Equal(OutputValidationStatus.Passed, exr.Status);
        Assert.Contains("png", png.DetectedExtensions);
        Assert.Contains("exr", exr.DetectedExtensions);
    }

    [Fact]
    public void OutputValidationAnyModeAcceptsPlausibleRenderOutput()
    {
        var root = CreateTempRoot();
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "Preview.jpg"), [5]);
        var profile = new RenderProfile("profile", "project", "Any", RenderProfileType.Manual, null, null, "", false, new Dictionary<string, string> { ["outputValidationMode"] = "AnyRenderOutput" });
        var request = BuildRequest(root, output, "", profile);

        var result = RenderOutputValidator.Validate(request, "");

        Assert.Equal(OutputValidationStatus.Passed, result.Status);
        Assert.Equal(OutputValidationMode.AnyRenderOutput, result.Mode);
    }

    [Fact]
    public void OutputValidationFailsEmptyAndWrongModeWithUsefulMessage()
    {
        var root = CreateTempRoot();
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "Shot01.mov"), [1, 2, 3]);
        var imageRequest = BuildRequest(root, output, "png");
        var emptyOutput = Path.Combine(root, "empty");
        Directory.CreateDirectory(emptyOutput);
        File.WriteAllBytes(Path.Combine(emptyOutput, "Frame_0001.png"), []);

        var wrongMode = RenderOutputValidator.Validate(imageRequest, "");
        var empty = RenderOutputValidator.Validate(BuildRequest(root, emptyOutput, "png"), "");

        Assert.Equal(OutputValidationStatus.Failed, wrongMode.Status);
        Assert.Contains("Change validation mode to SingleVideo", wrongMode.SuggestedFix, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OutputValidationStatus.Failed, empty.Status);
        Assert.Contains("empty", empty.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerPreflightBlocksMissingUnrealExecutable()
    {
        var root = CreateTempRoot();
        var project = Path.Combine(root, "Project.uproject");
        File.WriteAllText(project, "{}");
        var output = Path.Combine(root, "out");
        var request = new UnrealRenderRequest(Path.Combine(root, "Missing", "UnrealEditor-Cmd.exe"), project, Profile("png"), output, Path.Combine(root, "logs"), TimeSpan.FromSeconds(1));
        var service = CreatePreflightService();

        var result = await service.RunAsync(request, new Uri("http://127.0.0.1:9200/"), CancellationToken.None);

        Assert.Equal(PreflightOverallStatus.Blocked, result.Status);
        Assert.Contains(result.Checks, check => check.Name == "Unreal executable" && check.Status == PreflightCheckStatus.Fail);
    }

    [Fact]
    public async Task WorkerPreflightTreatsLowDiskWarningAsNonBlockingByDefault()
    {
        var root = CreateTempRoot();
        var request = BuildRequest(root, Path.Combine(root, "out"), "png");
        var service = CreatePreflightService(new WorkerPreflightOptions { MinFreeDiskWarningGb = double.MaxValue, MinFreeDiskBlockGb = 0, WarningsBlockRendering = false });

        var result = await service.RunAsync(request, new Uri("http://127.0.0.1:9200/"), CancellationToken.None);

        Assert.Equal(PreflightOverallStatus.Warning, result.Status);
        Assert.DoesNotContain(result.Checks, check => check.Status == PreflightCheckStatus.Fail);
    }

    [Fact]
    public async Task WorkerPreflightBlocksDangerousRootOutputPath()
    {
        var root = CreateTempRoot();
        var driveRoot = Path.GetPathRoot(root)!;
        var request = BuildRequest(root, driveRoot, "png");
        var service = CreatePreflightService();

        var result = await service.RunAsync(request, new Uri("http://127.0.0.1:9200/"), CancellationToken.None);

        Assert.Equal(PreflightOverallStatus.Blocked, result.Status);
        Assert.Contains(result.Checks, check => check.Name == "Output path" && check.Status == PreflightCheckStatus.Fail);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_prod_readiness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static UnrealRenderRequest BuildRequest(string root, string output, string outputType, RenderProfile? profile = null)
    {
        var executable = Path.Combine(root, "UE_5.8", "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "placeholder");
        var project = Path.Combine(root, "Project.uproject");
        File.WriteAllText(project, "{}");
        return new UnrealRenderRequest(executable, project, profile ?? Profile(outputType), output, Path.Combine(root, "logs"), TimeSpan.FromSeconds(1));
    }

    private static RenderProfile Profile(string outputType) =>
        new("profile", "project", "Profile", RenderProfileType.MrqQueue, "/Game/RenderConfig", null, outputType, false, new Dictionary<string, string> { ["map"] = "Map" });

    private static WorkerPreflightService CreatePreflightService(WorkerPreflightOptions? options = null) =>
        new(new StubHttpClientFactory(), new UnrealCommandBuilder(), Options.Create(options ?? new WorkerPreflightOptions { MinFreeDiskWarningGb = 0, MinFreeDiskBlockGb = 0 }), NullLogger<WorkerPreflightService>.Instance);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler()) { BaseAddress = new Uri("http://127.0.0.1:9200/") };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
