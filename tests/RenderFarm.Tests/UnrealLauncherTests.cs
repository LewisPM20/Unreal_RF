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
            new Dictionary<string, string> { ["map"] = "Minimal_Default1" });
        var request = new UnrealRenderRequest(executable, project, profile, Path.Combine(root, "out"), Path.Combine(root, "logs"), TimeSpan.FromSeconds(1));

        var command = new UnrealCommandBuilder().Build(request, "job-1", 1);

        Assert.Equal(executable, command.ExecutablePath);
        Assert.Equal(project, command.Arguments[0]);
        Assert.Equal("Minimal_Default1", command.Arguments[1]);
        Assert.Contains("-game", command.Arguments);
        Assert.Contains("-MoviePipelineConfig=/Game/Cinematics/myRenderQueue", command.Arguments);
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
            new Dictionary<string, string> { ["map"] = "Minimal_Default1" });
        var request = new UnrealRenderRequest(executable, project, profile, Path.Combine(root, "out"), Path.Combine(root, "logs"), TimeSpan.FromSeconds(1));

        var command = new UnrealCommandBuilder().Build(request, "job-1", 1);

        Assert.Single(command.Arguments.Where(x => x.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(project, command.Arguments[0]);
        Assert.Equal("Minimal_Default1", command.Arguments[1]);
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
}
