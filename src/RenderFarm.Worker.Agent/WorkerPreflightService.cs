using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RenderFarm.Domain;
using RenderFarm.Shared;

namespace RenderFarm.Worker.Agent;

/// <summary>
/// Strongly typed worker preflight settings.
/// </summary>
public sealed class WorkerPreflightOptions
{
    public double MinFreeDiskWarningGb { get; set; } = 25;
    public double MinFreeDiskBlockGb { get; set; } = 5;
    public bool WarningsBlockRendering { get; set; }
    public string? ExpectedUnrealExecutablePath { get; set; }
    public OutputValidationMode? DefaultOutputValidationMode { get; set; }
}

public interface IWorkerPreflightService
{
    Task<PreflightResultDto> RunAsync(UnrealRenderRequest request, Uri controllerBaseUri, CancellationToken cancellationToken);
}

/// <summary>
/// Performs local worker checks before launching Unreal.
/// </summary>
public sealed class WorkerPreflightService(
    IHttpClientFactory httpClientFactory,
    IUnrealCommandBuilder commandBuilder,
    IOptions<WorkerPreflightOptions> options,
    ILogger<WorkerPreflightService> logger) : IWorkerPreflightService
{
    public async Task<PreflightResultDto> RunAsync(UnrealRenderRequest request, Uri controllerBaseUri, CancellationToken cancellationToken)
    {
        var checks = new List<PreflightCheckDto>();
        var settings = options.Value;

        CheckFile(checks, "Unreal executable", request.UnrealExecutablePath, mustEndWith: "UnrealEditor-Cmd.exe", "Set Controller Render Defaults or the profile Unreal executable override to a worker-visible UnrealEditor-Cmd.exe path.");
        if (!string.IsNullOrWhiteSpace(settings.ExpectedUnrealExecutablePath) && !PathEquals(settings.ExpectedUnrealExecutablePath, request.UnrealExecutablePath))
        {
            checks.Add(new("Expected Unreal path", PreflightCheckStatus.Warning, $"Job uses {request.UnrealExecutablePath}, but worker preflight expected {settings.ExpectedUnrealExecutablePath}.", "Confirm the controller default/profile override is intentional.", "UnrealExecutablePath"));
        }

        CheckFile(checks, "Project .uproject", request.ProjectPath, mustEndWith: ".uproject", "Set the project path or worker-specific project mapping to a valid .uproject file.");
        CheckUProjectLooksValid(checks, request.ProjectPath);
        CheckOutputPath(checks, request.OutputDirectory);
        await CheckWritableDirectoryAsync(checks, "Output directory", request.OutputDirectory, cancellationToken);
        await CheckWritableDirectoryAsync(checks, "Log directory", request.LogDirectory, cancellationToken);
        await CheckWritableDirectoryAsync(checks, "Worker temp directory", Path.GetTempPath(), cancellationToken);
        CheckDisk(checks, request.OutputDirectory, settings.MinFreeDiskWarningGb, settings.MinFreeDiskBlockGb);
        CheckValidationMode(checks, request.Profile, settings.DefaultOutputValidationMode);
        CheckCommandBuild(checks, request);
        await CheckControllerReachableAsync(checks, controllerBaseUri, cancellationToken);

        var hasFailure = checks.Any(check => check.Status == PreflightCheckStatus.Fail);
        var hasWarning = checks.Any(check => check.Status == PreflightCheckStatus.Warning);
        var status = hasFailure || (settings.WarningsBlockRendering && hasWarning)
            ? PreflightOverallStatus.Blocked
            : hasWarning ? PreflightOverallStatus.Warning : PreflightOverallStatus.Ready;
        var summary = status switch
        {
            PreflightOverallStatus.Ready => "Worker preflight passed.",
            PreflightOverallStatus.Warning => "Worker preflight passed with warnings.",
            _ => "Worker preflight blocked this render."
        };

        logger.LogInformation("Worker preflight finished with {Status}: {Summary}", status, summary);
        return new PreflightResultDto(status, DateTimeOffset.UtcNow, checks, summary);
    }

    private static void CheckFile(List<PreflightCheckDto> checks, string name, string path, string mustEndWith, string fix)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            checks.Add(new(name, PreflightCheckStatus.Fail, $"{name} path is empty.", fix));
            return;
        }

        var expanded = Expand(path);
        if (!expanded.EndsWith(mustEndWith, StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new(name, PreflightCheckStatus.Fail, $"{name} path does not end with {mustEndWith}: {expanded}", fix));
            return;
        }

        checks.Add(File.Exists(expanded)
            ? new(name, PreflightCheckStatus.Pass, $"Found {expanded}.")
            : new(name, PreflightCheckStatus.Fail, $"Missing {name}: {expanded}", fix));
    }

    private static void CheckUProjectLooksValid(List<PreflightCheckDto> checks, string path)
    {
        if (!File.Exists(path))
        {
            checks.Add(new("Project file shape", PreflightCheckStatus.NotApplicable, "Project file could not be inspected because it does not exist."));
            return;
        }

        try
        {
            var text = File.ReadAllText(path);
            checks.Add(text.TrimStart().StartsWith('{')
                ? new("Project file shape", PreflightCheckStatus.Pass, ".uproject file looks like JSON.")
                : new("Project file shape", PreflightCheckStatus.Warning, ".uproject file does not look like JSON.", "Open the project once in Unreal or restore a valid .uproject file."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            checks.Add(new("Project file shape", PreflightCheckStatus.Warning, $"Could not read .uproject file: {ex.Message}", "Check file permissions for the worker account."));
        }
    }

    private static void CheckOutputPath(List<PreflightCheckDto> checks, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            checks.Add(new("Output path", PreflightCheckStatus.Fail, "Output directory is empty.", "Set an output folder or configure Controller Render Defaults.", "OutputDirectory"));
            return;
        }

        try
        {
            var full = Path.GetFullPath(Expand(path));
            var root = Path.GetPathRoot(full);
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(new("Output path", PreflightCheckStatus.Fail, $"Output directory points at a drive/share root: {full}", "Use a job-specific subfolder under a shared output root.", "OutputDirectory"));
                return;
            }

            checks.Add(new("Output path", PreflightCheckStatus.Pass, $"Output path normalized to {full}."));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            checks.Add(new("Output path", PreflightCheckStatus.Fail, $"Output path is invalid: {ex.Message}", "Use a normal local or UNC folder path.", "OutputDirectory"));
        }
    }

    private static async Task CheckWritableDirectoryAsync(List<PreflightCheckDto> checks, string name, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            checks.Add(new(name, PreflightCheckStatus.Fail, $"{name} is empty.", "Configure this path before rendering."));
            return;
        }

        try
        {
            var directory = Path.GetFullPath(Expand(path));
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".renderfarm_preflight_{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probe, "renderfarm", cancellationToken);
            File.Delete(probe);
            checks.Add(new(name, PreflightCheckStatus.Pass, $"{name} is writable: {directory}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            checks.Add(new(name, PreflightCheckStatus.Fail, $"{name} is not writable: {ex.Message}", "Grant write access to the worker account or choose another folder."));
        }
    }

    private static void CheckDisk(List<PreflightCheckDto> checks, string path, double warningGb, double blockGb)
    {
        var free = TryGetFreeDiskGb(path);
        if (free is null)
        {
            checks.Add(new("Free disk", PreflightCheckStatus.Warning, "Free disk space could not be measured.", "Check available space on the output volume before large renders."));
            return;
        }

        if (blockGb > 0 && free < blockGb)
        {
            checks.Add(new("Free disk", PreflightCheckStatus.Fail, $"Output volume has {free:0.##} GB free, below blocking threshold {blockGb:0.##} GB.", "Free disk space or move output to a larger volume."));
            return;
        }

        checks.Add(warningGb > 0 && free < warningGb
            ? new("Free disk", PreflightCheckStatus.Warning, $"Output volume has {free:0.##} GB free, below warning threshold {warningGb:0.##} GB.", "Large renders may fail if the volume fills up.")
            : new("Free disk", PreflightCheckStatus.Pass, $"Output volume has {free:0.##} GB free."));
    }

    private static void CheckValidationMode(List<PreflightCheckDto> checks, RenderProfile profile, OutputValidationMode? defaultMode)
    {
        var mode = RenderOutputValidator.DetermineMode(profile);
        checks.Add(new("Output validation mode", PreflightCheckStatus.Pass, $"Post-render output validation will use {mode}."));
        if (defaultMode is not null && mode != defaultMode)
        {
            checks.Add(new("Default validation mode", PreflightCheckStatus.Warning, $"Profile validation mode {mode} differs from worker default {defaultMode}.", "Confirm the profile output format matches MRQ/MRG settings."));
        }
    }

    private void CheckCommandBuild(List<PreflightCheckDto> checks, UnrealRenderRequest request)
    {
        try
        {
            var command = commandBuilder.Build(request, "preflight", 1);
            checks.Add(new("Command preview", PreflightCheckStatus.Pass, "Render command can be constructed safely.", null, "CommandLine"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or ArgumentException)
        {
            checks.Add(new("Command preview", PreflightCheckStatus.Fail, $"Render command could not be built: {ex.Message}", "Fix project/profile paths and command template fields before queueing.", "CommandLine"));
        }
    }

    private async Task CheckControllerReachableAsync(List<PreflightCheckDto> checks, Uri controllerBaseUri, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(new Uri(controllerBaseUri, "health"), cancellationToken);
            checks.Add(response.IsSuccessStatusCode
                ? new("Controller reachability", PreflightCheckStatus.Pass, "Worker can reach controller health endpoint.")
                : new("Controller reachability", PreflightCheckStatus.Warning, $"Controller health returned HTTP {(int)response.StatusCode}.", "Check controller URL, API token, and firewall."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            checks.Add(new("Controller reachability", PreflightCheckStatus.Warning, $"Controller health check failed: {ex.Message}", "The current assignment came from the controller, but network instability may affect lease renewal."));
        }
    }

    private static double? TryGetFreeDiskGb(string path)
    {
        try
        {
            var full = Path.GetFullPath(Expand(path));
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace / 1024d / 1024d / 1024d : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(Expand(left)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(Expand(right)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
}
