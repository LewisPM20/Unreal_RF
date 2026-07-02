using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RenderFarm.Domain;
using RenderFarm.Worker.Agent;
using Xunit;

namespace RenderFarm.Tests;

public sealed class UnrealLauncherTests
{
    [Fact]
    public void CommandBuilderConstructsMovieRenderQueueCommandForKnownProjectAndProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_launcher_tests", Guid.NewGuid().ToString("N"));
        var engineRoot = Path.Combine(root, "UE_5.8");
        var executableDirectory = Path.Combine(engineRoot, "Engine", "Binaries", "Win64");
        Directory.CreateDirectory(executableDirectory);
        var executable = Path.Combine(executableDirectory, "UnrealEditor-Cmd.exe");
        File.WriteAllText(executable, "placeholder");
        var project = Path.Combine(root, "MRQCommandLine.uproject");
        File.WriteAllText(project, "{}");
        var profile = new RenderProfile(
            "profile-1",
            "project-1",
            "Main Queue",
            RenderProfileType.MrqQueue,
            "/Game/Cinematics/myRenderQueue",
            null,
            "png",
            false,
            new Dictionary<string, string> { ["map"] = "/Game/Maps/Minimal_Default1", ["sequence"] = "Game/Cinematics/Seq01" });
        var request = new UnrealRenderRequest(executable, project, profile, Path.Combine(root, "out"), Path.Combine(root, "logs"), TimeSpan.FromSeconds(1));

        var command = new UnrealCommandBuilder().Build(request, "job-1", 1);

        Assert.Equal(executable, command.ExecutablePath);
        Assert.Equal(project, command.Arguments[0]);
        Assert.Equal("/Game/Maps/Minimal_Default1", command.Arguments[1]);
        Assert.Contains("-game", command.Arguments);
        Assert.Contains("-LevelSequence=/Game/Cinematics/Seq01.Seq01", command.Arguments);
        Assert.Contains("-MoviePipelineConfig=/Game/Cinematics/myRenderQueue.myRenderQueue", command.Arguments);
        Assert.Contains("-windowed", command.Arguments);
        Assert.Contains("-Log", command.Arguments);
        Assert.Contains("-StdOut", command.Arguments);
        Assert.Contains("-allowStdOutLogVerbosity", command.Arguments);
        Assert.Contains("-Unattended", command.Arguments);
        Assert.EndsWith("job-1_attempt_01.log", command.LogFilePath);
    }

    [Fact]
    public void CommandBuilderAcceptsAFullKnownGoodTemplateWithoutDuplicatingProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_launcher_tests", Guid.NewGuid().ToString("N"));
        var executableDirectory = Path.Combine(root, "UE_5.8", "Engine", "Binaries", "Win64");
        Directory.CreateDirectory(executableDirectory);
        var executable = Path.Combine(executableDirectory, "UnrealEditor-Cmd.exe");
        File.WriteAllText(executable, "placeholder");
        var project = Path.Combine(root, "MRQCommandLine.uproject");
        File.WriteAllText(project, "{}");
        var profile = new RenderProfile(
            "profile-1",
            "project-1",
            "Main Queue",
            RenderProfileType.CommandTemplate,
            "/Game/Cinematics/myRenderQueue",
            "{project} {map} -game -MoviePipelineConfig={asset} -windowed -Log -StdOut -allowStdOutLogVerbosity -Unattended",
            "png",
            false,
            new Dictionary<string, string> { ["map"] = "/Game/Maps/Minimal_Default1", ["sequence"] = "Game/Cinematics/Seq01" });
        var request = new UnrealRenderRequest(executable, project, profile, Path.Combine(root, "out"), Path.Combine(root, "logs"), TimeSpan.FromSeconds(1));

        var command = new UnrealCommandBuilder().Build(request, "job-1", 1);

        Assert.Single(command.Arguments.Where(x => x.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(project, command.Arguments[0]);
        Assert.Equal("/Game/Maps/Minimal_Default1", command.Arguments[1]);
    }

    [Fact]
    public void CommandBuilderRejectsUnresolvedTemplateTokens()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_launcher_tests", Guid.NewGuid().ToString("N"));
        var executableDirectory = Path.Combine(root, "UE_5.8", "Engine", "Binaries", "Win64");
        Directory.CreateDirectory(executableDirectory);
        var executable = Path.Combine(executableDirectory, "UnrealEditor-Cmd.exe");
        File.WriteAllText(executable, "placeholder");
        var project = Path.Combine(root, "MRQCommandLine.uproject");
        File.WriteAllText(project, "{}");
        var profile = new RenderProfile(
            "profile-1",
            "project-1",
            "Broken Template",
            RenderProfileType.CommandTemplate,
            "/Game/Cinematics/myRenderQueue",
            "{project} {MissingToken} -game",
            "png",
            false,
            new Dictionary<string, string>());
        var request = new UnrealRenderRequest(executable, project, profile, Path.Combine(root, "out"), Path.Combine(root, "logs"), TimeSpan.FromSeconds(1));

        var error = Assert.Throws<InvalidOperationException>(() => new UnrealCommandBuilder().Build(request, "job-1", 1));

        Assert.Contains("{MissingToken}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineLocatorAcceptsExactExecutablePathAndNormalizesVersionUnderscores()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_locator_tests", Guid.NewGuid().ToString("N"));
        var engineRoot = Path.Combine(root, "UE_5_8");
        var executableDirectory = Path.Combine(engineRoot, "Engine", "Binaries", "Win64");
        Directory.CreateDirectory(executableDirectory);
        var executable = Path.Combine(executableDirectory, "UnrealEditor-Cmd.exe");
        File.WriteAllText(executable, "placeholder");
        var locator = new UnrealEngineLocator();

        var installs = locator.FindInstallations([executable]);
        var resolved = locator.Resolve("5.8", installs);

        Assert.NotNull(resolved);
        Assert.Equal(executable, resolved.ExecutablePath);
    }

    [Fact]
    public async Task ProcessLauncherCapturesHarmlessProcessOutput()
    {
        var launcher = new ProcessLauncher(NullLogger<ProcessLauncher>.Instance);
        var result = await launcher.RunAsync("cmd.exe", ["/c", "echo renderfarm"], null, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(FailureCategory.None, result.FailureCategory);
        Assert.Contains("renderfarm", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandBuilderPreservesSimpleMapAndQueuePackagePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_launcher_tests", Guid.NewGuid().ToString("N"));
        var executableDirectory = Path.Combine(root, "UE_5.8", "Engine", "Binaries", "Win64");
        Directory.CreateDirectory(executableDirectory);
        var executable = Path.Combine(executableDirectory, "UnrealEditor-Cmd.exe");
        File.WriteAllText(executable, "placeholder");
        var project = Path.Combine(root, "DHLPractise.uproject");
        File.WriteAllText(project, "{}");
        var profile = new RenderProfile(
            "profile-queue",
            "project-1",
            "Saved Queue",
            RenderProfileType.MrqQueue,
            "/Game/RenderConfig",
            null,
            "mov",
            false,
            new Dictionary<string, string> { ["map"] = "V2", ["mrqMode"] = "Queue" });
        var request = new UnrealRenderRequest(executable, project, profile, Path.Combine(root, "out"), Path.Combine(root, "logs"), TimeSpan.FromSeconds(1));

        var command = new UnrealCommandBuilder().Build(request, "job-queue", 1);

        Assert.Equal(project, command.Arguments[0]);
        Assert.Equal("V2", command.Arguments[1]);
        Assert.DoesNotContain(command.Arguments, argument => argument.StartsWith("-LevelSequence=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("-MoviePipelineConfig=/Game/RenderConfig", command.Arguments);
        Assert.Contains(" -MoviePipelineConfig=\"/Game/RenderConfig\" ", $" {command.CommandLine} ", StringComparison.Ordinal);
    }

    [Fact]
    public void CommandBuilderAppendsConfigObjectPathOnlyInSingleSequenceMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_launcher_tests", Guid.NewGuid().ToString("N"));
        var executableDirectory = Path.Combine(root, "UE_5.8", "Engine", "Binaries", "Win64");
        Directory.CreateDirectory(executableDirectory);
        var executable = Path.Combine(executableDirectory, "UnrealEditor-Cmd.exe");
        File.WriteAllText(executable, "placeholder");
        var project = Path.Combine(root, "DHLPractise.uproject");
        File.WriteAllText(project, "{}");
        var profile = new RenderProfile(
            "profile-single",
            "project-1",
            "Single Sequence",
            RenderProfileType.MrqQueue,
            "/Game/RenderConfig",
            null,
            "png",
            false,
            new Dictionary<string, string>
            {
                ["map"] = "V2",
                ["sequence"] = "/Game/C4DFORUNREALV2/V2.V2",
                ["mrqMode"] = "SingleSequence"
            });
        var request = new UnrealRenderRequest(executable, project, profile, Path.Combine(root, "out"), Path.Combine(root, "logs"), TimeSpan.FromSeconds(1));

        var command = new UnrealCommandBuilder().Build(request, "job-single", 1);

        Assert.Equal("V2", command.Arguments[1]);
        Assert.Contains("-LevelSequence=/Game/C4DFORUNREALV2/V2.V2", command.Arguments);
        Assert.Contains("-MoviePipelineConfig=/Game/RenderConfig.RenderConfig", command.Arguments);
        Assert.Contains("-LevelSequence=\"/Game/C4DFORUNREALV2/V2.V2\"", command.CommandLine, StringComparison.Ordinal);
        Assert.Contains("-MoviePipelineConfig=\"/Game/RenderConfig.RenderConfig\"", command.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerOutputVerifierAcceptsMovArtifactWithoutPng()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_output_tests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "shot.mov"), [1, 2, 3, 4]);
        var request = BuildVerifierRequest(root, output);

        var verification = InvokeOutputVerification(request, "");
        var ok = (bool)verification.GetType().GetProperty("Ok")!.GetValue(verification)!;
        var summary = (RenderFarm.Shared.RenderArtifactSummaryDto?)verification.GetType().GetProperty("ArtifactSummary")!.GetValue(verification);

        Assert.True(ok);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.FileCount);
        Assert.Contains("mov", summary.DetectedExtensions ?? []);
    }

    [Fact]
    public void WorkerOutputVerifierSucceedsWithWarningWhenCompletionLogHasNoArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_output_tests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "empty-output");
        Directory.CreateDirectory(output);
        var request = BuildVerifierRequest(root, output);

        var verification = InvokeOutputVerification(request, "Movie Render finished successfully");
        var ok = (bool)verification.GetType().GetProperty("Ok")!.GetValue(verification)!;
        var warning = (string?)verification.GetType().GetProperty("Warning")!.GetValue(verification);

        Assert.True(ok);
        Assert.NotNull(warning);
        Assert.Contains("no output files were found", warning, StringComparison.OrdinalIgnoreCase);
    }

    private static UnrealRenderRequest BuildVerifierRequest(string root, string output)
    {
        var executable = Path.Combine(root, "UE_5.8", "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "placeholder");
        var project = Path.Combine(root, "Project.uproject");
        File.WriteAllText(project, "{}");
        var profile = new RenderProfile("profile", "project", "Queue", RenderProfileType.MrqQueue, "/Game/RenderConfig", null, "mov", false, new Dictionary<string, string> { ["map"] = "V2", ["mrqMode"] = "Queue" });
        return new UnrealRenderRequest(executable, project, profile, output, Path.Combine(root, "logs"), TimeSpan.FromSeconds(1));
    }

    private static object InvokeOutputVerification(UnrealRenderRequest request, string logText)
    {
        var method = typeof(WorkerJobService).GetMethod("VerifyRenderOutput", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, [request, logText])!;
    }

    [Fact]
    public async Task ProcessLauncherReportsNonZeroExit()
    {
        var launcher = new ProcessLauncher(NullLogger<ProcessLauncher>.Instance);

        var result = await launcher.RunAsync("cmd.exe", ["/c", "exit 7"], null, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(FailureCategory.RenderProcessFailed, result.FailureCategory);
    }

    [Fact]
    public async Task ProcessLauncherReportsTimeout()
    {
        var launcher = new ProcessLauncher(NullLogger<ProcessLauncher>.Instance);

        var result = await launcher.RunAsync("cmd.exe", ["/c", "ping -n 6 127.0.0.1 > nul"], null, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(FailureCategory.RenderProcessTimedOut, result.FailureCategory);
    }

    [Fact]
    public async Task ProcessLauncherReportsNoProgressWatchdog()
    {
        var launcher = new ProcessLauncher(
            NullLogger<ProcessLauncher>.Instance,
            Microsoft.Extensions.Options.Options.Create(new RenderProcessExecutionOptions { NoProgressTimeoutSeconds = 1 }));

        var result = await launcher.RunAsync("cmd.exe", ["/c", "ping -n 6 127.0.0.1 > nul"], null, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(FailureCategory.RenderProcessTimedOut, result.FailureCategory);
        Assert.Contains("no stdout/stderr progress", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessLauncherHonoursCancellation()
    {
        var launcher = new ProcessLauncher(NullLogger<ProcessLauncher>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<OperationCanceledException>(() => launcher.RunAsync("cmd.exe", ["/c", "ping -n 6 127.0.0.1 > nul"], null, TimeSpan.FromSeconds(10), cts.Token));
    }

    [Fact]
    public async Task ProcessLauncherBoundsCapturedLogs()
    {
        var launcher = new ProcessLauncher(
            NullLogger<ProcessLauncher>.Instance,
            Microsoft.Extensions.Options.Options.Create(new RenderProcessExecutionOptions { MaxCapturedOutputCharacters = 4096 }));

        var result = await launcher.RunAsync("cmd.exe", ["/c", "for /L %i in (1,1,1200) do @echo RenderFarmLogLine%i"], null, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(FailureCategory.None, result.FailureCategory);
        Assert.True(result.StandardOutput.Length <= 4500);
    }

    [Fact]
    public void ProcessOwnershipDeciderIsConservative()
    {
        var currentRoot = Path.Combine(Path.GetTempPath(), "RenderFarmA");

        Assert.Equal(ProcessOwnershipDecision.OwnedByThisInstance, ProcessOwnershipDecider.Decide(new OwnedProcessMetadata(10, "UnrealEditor-Cmd.exe", currentRoot, true), currentRoot));
        Assert.Equal(ProcessOwnershipDecision.NotOwned, ProcessOwnershipDecider.Decide(new OwnedProcessMetadata(11, "UnrealEditor-Cmd.exe", currentRoot, false), currentRoot));
        Assert.Equal(ProcessOwnershipDecision.Uncertain, ProcessOwnershipDecider.Decide(new OwnedProcessMetadata(12, "UnrealEditor-Cmd.exe", Path.Combine(Path.GetTempPath(), "OtherFarm"), true), currentRoot));
    }}







