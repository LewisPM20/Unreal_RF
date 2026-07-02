using System.Text;
using System.Text.Json;
using RenderFarm.Domain;
using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public interface IRenderJobValidator
{
    Task<RenderValidationResultDto> ValidateFastAsync(CreateRenderJobRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Performs fast controller-side render validation without launching Unreal.
/// </summary>
public sealed class RenderJobValidator(
    IProjectRepository projects,
    IRenderProfileRepository profiles,
    IWorkerRepository workers,
    ISettingsRepository settings) : IRenderJobValidator
{
    public async Task<RenderValidationResultDto> ValidateFastAsync(CreateRenderJobRequest request, CancellationToken cancellationToken)
    {
        var issues = new List<RenderValidationIssueDto>();
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        var profile = await profiles.GetAsync(request.RenderProfileId, cancellationToken);
        if (project is null)
        {
            issues.Add(Error("project-missing", "Project was not found.", nameof(request.ProjectId)));
        }

        if (profile is null || project is not null && !string.Equals(profile.ProjectId, project.Id, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error("profile-missing", "Render profile was not found for the selected project.", nameof(request.RenderProfileId)));
        }

        if (project is null || profile is null)
        {
            return BuildResult(issues, null);
        }

        var defaults = await ControllerRenderDefaults.LoadAsync(settings, cancellationToken);
        var workerList = await workers.ListAsync(cancellationToken);
        var projectPath = ResolveProjectPath(project, profile);
        var normalizedProjectPath = NormalizeFilePath(projectPath, "ProjectPath", issues);
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            issues.Add(Error("uproject-required", "Project path is required before queueing. Set the project .uproject path or a profile projectPath override.", "ProjectPath"));
        }
        else if (!projectPath.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error("uproject-extension", "Project path must point to a .uproject file.", "ProjectPath", projectPath));
        }
        else if (normalizedProjectPath is null || !File.Exists(normalizedProjectPath))
        {
            issues.Add(Error("uproject-not-found", $"Project .uproject file was not found: {projectPath}", "ProjectPath", projectPath));
        }

        var unrealCandidate = ResolveUnrealCandidate(profile, project, defaults, workerList);
        var unrealExecutable = ResolveUnrealExecutable(unrealCandidate, issues);
        if (string.IsNullOrWhiteSpace(unrealCandidate))
        {
            issues.Add(Error("unreal-executable-required", "UnrealEditor-Cmd.exe or an Unreal engine search root is required before queueing.", "UnrealExecutable"));
        }
        else if (unrealExecutable is null || !File.Exists(unrealExecutable))
        {
            issues.Add(Error("unreal-executable-not-found", $"UnrealEditor-Cmd.exe was not found: {unrealCandidate}", "UnrealExecutable", unrealCandidate));
        }

        var outputRoot = ResolveOutputRoot(request, profile, defaults, workerList);
        var normalizedOutputRoot = NormalizeFilePath(outputRoot, "OutputRoot", issues);
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            issues.Add(Error("output-required", "Output directory is required. Enter an output folder or configure Controller Render Defaults.", "OutputRoot"));
        }
        else if (!CanWriteOutputDirectory(normalizedOutputRoot ?? outputRoot, out var outputError))
        {
            issues.Add(Error("output-not-writable", outputError, "OutputRoot", outputRoot));
        }

        ValidateRenderProfile(profile, issues);
        var command = BuildCommandPreview(unrealExecutable ?? unrealCandidate, normalizedProjectPath ?? projectPath, profile, normalizedOutputRoot ?? outputRoot, issues);
        if (command is null)
        {
            issues.Add(Error("command-preview-unavailable", "Command preview could not be generated until blocking validation issues are fixed.", "CommandPreview"));
        }
        else if (!CommandLooksQuoted(command, normalizedProjectPath ?? projectPath, unrealExecutable ?? unrealCandidate))
        {
            issues.Add(Error("command-quoting", "Generated command quoting did not pass the fast safety check.", "CommandPreview", command));
        }

        return BuildResult(issues, command);
    }

    private static RenderValidationResultDto BuildResult(IReadOnlyList<RenderValidationIssueDto> issues, string? command)
    {
        var status = issues.Any(issue => issue.Severity == RenderValidationSeverity.Error)
            ? RenderValidationStatus.Blocked
            : issues.Any(issue => issue.Severity == RenderValidationSeverity.Warning)
                ? RenderValidationStatus.NeedsAttention
                : RenderValidationStatus.Ready;
        var summary = status switch
        {
            RenderValidationStatus.Blocked => issues.First(issue => issue.Severity == RenderValidationSeverity.Error).Message,
            RenderValidationStatus.NeedsAttention => "Fast validation passed with safe fixes or warnings. Path format looks valid; asset existence requires deep validation.",
            _ => "Fast validation passed. Path format looks valid; asset existence requires deep validation."
        };
        return new RenderValidationResultDto(status, "fast", false, summary, command, issues);
    }

    private static void ValidateRenderProfile(RenderProfile profile, List<RenderValidationIssueDto> issues)
    {
        if (profile.Type == RenderProfileType.CommandTemplate)
        {
            if (string.IsNullOrWhiteSpace(profile.CommandTemplate))
            {
                issues.Add(Error("command-template-required", "Command/template mode requires a command template.", "RenderProfile.CommandTemplate"));
            }
            return;
        }

        if (profile.Type is not (RenderProfileType.MrqQueue or RenderProfileType.MrgGraph))
        {
            return;
        }

        var mapPath = FirstNonEmpty(GetSetting(profile, "map"), GetSetting(profile, "mapName"), GetSetting(profile, "level"), GetSetting(profile, "levelName"));
        var sequencePath = FirstNonEmpty(GetSetting(profile, "sequence"), GetSetting(profile, "levelSequence"));
        var configPath = FirstNonEmpty(profile.AssetPath, GetSetting(profile, "moviePipelineConfig"), GetSetting(profile, "mrqConfig"), GetSetting(profile, "queue"));
        var launchMode = ResolveMovieRenderLaunchMode(profile, sequencePath);

        ValidateUnrealPath(issues, "RenderProfile.Map", mapPath, UnrealAssetPathKind.WorldPackagePath, "Map/world argument is required and must look like Minimal_Default1 or /Game/Maps/MainMap.");

        if (launchMode == MovieRenderLaunchMode.SingleSequence)
        {
            ValidateUnrealPath(issues, "RenderProfile.Sequence", sequencePath, UnrealAssetPathKind.LevelSequenceObjectPath, "Single Sequence MRQ mode requires a Level Sequence object path such as /Game/Cinematics/Seq01.Seq01.");
        }
        else if (!string.IsNullOrWhiteSpace(sequencePath) && HasExplicitMovieRenderLaunchMode(profile))
        {
            issues.Add(Error("mrq-queue-mode-sequence-set", "Saved MRQ Queue mode should not set a separate Level Sequence. Remove the sequence field, or switch the profile to Single Sequence + Config mode.", "RenderProfile.Sequence", sequencePath));
        }

        var configKind = launchMode == MovieRenderLaunchMode.SavedQueue
            ? UnrealAssetPathKind.MoviePipelineQueueObjectPath
            : UnrealAssetPathKind.MoviePipelineConfigObjectPath;
        var configMessage = launchMode == MovieRenderLaunchMode.SavedQueue
            ? "Saved MRQ Queue mode requires a queue asset path such as /Game/Cinematics/myRenderQueue."
            : "Single Sequence MRQ mode requires a config preset path such as /Game/RenderConfig.RenderConfig.";
        ValidateUnrealPath(issues, "RenderProfile.Config", configPath, configKind, configMessage);
    }

    private static void ValidateUnrealPath(List<RenderValidationIssueDto> issues, string field, string? value, UnrealAssetPathKind kind, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(Error("unreal-path-required", missingMessage, field));
            return;
        }

        var result = UnrealPathNormalizer.TryNormalizeUnrealReference(value, kind);
        if (!result.Success)
        {
            issues.Add(Error("unreal-path-invalid", result.Error ?? missingMessage, field, value));
            return;
        }

        if (!string.Equals(value.Trim(), result.NormalizedPath, StringComparison.Ordinal))
        {
            issues.Add(new RenderValidationIssueDto(
                "unreal-path-auto-fix",
                RenderValidationSeverity.Warning,
                $"{field} can be normalised safely. Path format looks valid; asset existence requires deep validation.",
                field,
                "Apply the normalised Unreal asset path before queueing.",
                value,
                result.NormalizedPath,
                AutoFixAvailable: true));
        }
    }

    private static string? BuildCommandPreview(string unrealExecutable, string projectPath, RenderProfile profile, string outputRoot, List<RenderValidationIssueDto> issues)
    {
        if (profile.Type is not (RenderProfileType.MrqQueue or RenderProfileType.MrgGraph))
        {
            return profile.Type == RenderProfileType.CommandTemplate && !string.IsNullOrWhiteSpace(profile.CommandTemplate)
                ? profile.CommandTemplate
                : null;
        }

        var mapRaw = FirstNonEmpty(GetSetting(profile, "map"), GetSetting(profile, "mapName"), GetSetting(profile, "level"), GetSetting(profile, "levelName"));
        var sequenceRaw = FirstNonEmpty(GetSetting(profile, "sequence"), GetSetting(profile, "levelSequence"));
        var configRaw = FirstNonEmpty(profile.AssetPath, GetSetting(profile, "moviePipelineConfig"), GetSetting(profile, "mrqConfig"), GetSetting(profile, "queue"));
        var launchMode = ResolveMovieRenderLaunchMode(profile, sequenceRaw);
        var map = UnrealPathNormalizer.TryNormalizeUnrealReference(mapRaw, UnrealAssetPathKind.WorldPackagePath);
        var sequence = launchMode == MovieRenderLaunchMode.SingleSequence
            ? UnrealPathNormalizer.TryNormalizeUnrealReference(sequenceRaw, UnrealAssetPathKind.LevelSequenceObjectPath)
            : null;
        var configKind = launchMode == MovieRenderLaunchMode.SavedQueue
            ? UnrealAssetPathKind.MoviePipelineQueueObjectPath
            : UnrealAssetPathKind.MoviePipelineConfigObjectPath;
        var config = UnrealPathNormalizer.TryNormalizeUnrealReference(configRaw, configKind);
        if (!map.Success || sequence is { Success: false } || !config.Success)
        {
            return null;
        }

        var args = new List<string>
        {
            QuoteArgument(unrealExecutable),
            QuoteArgument(projectPath),
            QuoteArgument(map.NormalizedPath!),
            "-game",
            QuoteSwitchValue("-MoviePipelineConfig", config.NormalizedPath!),
            "-windowed",
            "-Log",
            "-StdOut",
            "-allowStdOutLogVerbosity",
            "-Unattended"
        };
        if (sequence is { NormalizedPath: { Length: > 0 } sequenceValue })
        {
            args.Insert(4, QuoteSwitchValue("-LevelSequence", sequenceValue));
        }

        var extraArgs = GetSetting(profile, "extraArgs");
        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            args.Add(extraArgs);
        }

        return string.Join(" ", args);
    }


    private enum MovieRenderLaunchMode
    {
        SavedQueue,
        SingleSequence
    }

    private static MovieRenderLaunchMode ResolveMovieRenderLaunchMode(RenderProfile profile, string? levelSequence)
    {
        var raw = GetMovieRenderLaunchModeValue(profile);
        if (IsSingleSequenceMode(raw))
        {
            return MovieRenderLaunchMode.SingleSequence;
        }

        if (IsQueueMode(raw))
        {
            return MovieRenderLaunchMode.SavedQueue;
        }

        return string.IsNullOrWhiteSpace(levelSequence)
            ? MovieRenderLaunchMode.SavedQueue
            : MovieRenderLaunchMode.SingleSequence;
    }

    private static bool HasExplicitMovieRenderLaunchMode(RenderProfile profile) =>
        !string.IsNullOrWhiteSpace(GetMovieRenderLaunchModeValue(profile));

    private static string GetMovieRenderLaunchModeValue(RenderProfile profile) =>
        FirstNonEmpty(
            GetSetting(profile, "mrqMode"),
            GetSetting(profile, "renderMode"),
            GetSetting(profile, "movieRenderMode"),
            GetSetting(profile, "moviePipelineMode"),
            GetSetting(profile, "launchMode"));

    private static bool IsSingleSequenceMode(string? value)
    {
        var key = NormalizeModeKey(value);
        return key is "single" or "singlesequence" or "sequence" or "levelsequence" or "config" or "configpreset" or "sequenceconfig" or "singlelevelsequence";
    }

    private static bool IsQueueMode(string? value)
    {
        var key = NormalizeModeKey(value);
        return key is "queue" or "savedqueue" or "queuepreset" or "mrqqueue" or "moviepipelinequeue";
    }

    private static string NormalizeModeKey(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool CommandLooksQuoted(string command, string projectPath, string unrealExecutable)
    {
        foreach (var value in new[] { projectPath, unrealExecutable }.Where(value => !string.IsNullOrWhiteSpace(value) && value.Contains(' ')))
        {
            if (!command.Contains(QuoteArgument(value), StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static string QuoteArgument(string value)
    {
        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return value.Contains(' ') || value.Contains('"')
            ? '"' + escaped + '"'
            : escaped;
    }

    private static string QuoteSwitchValue(string switchName, string value)
    {
        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"{switchName}=\"{escaped}\"";
    }

    private static string ResolveProjectPath(ProjectProfile project, RenderProfile profile) =>
        FirstNonEmpty(GetSetting(profile, "projectPath"), GetSetting(profile, "uprojectPath"), project.UProjectPath, project.WorkerPaths.Select(path => path.ProjectPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)));

    private static string ResolveUnrealCandidate(RenderProfile profile, ProjectProfile project, RenderDefaultsDto defaults, IReadOnlyList<Worker> workerList) =>
        FirstNonEmpty(
            GetSetting(profile, "unrealExecutablePath"),
            GetSetting(profile, "unrealExe"),
            GetSetting(profile, "unrealCommand"),
            GetSetting(profile, "unrealSearchRoot"),
            defaults.UnrealExecutablePath,
            defaults.UnrealSearchRoot,
            project.WorkerPaths.Select(path => path.EnginePath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)),
            workerList.SelectMany(worker => worker.Capabilities.UnrealInstallations).FirstOrDefault(install => install.Exists)?.ExecutablePath);

    private static string ResolveOutputRoot(CreateRenderJobRequest request, RenderProfile profile, RenderDefaultsDto defaults, IReadOnlyList<Worker> workerList) =>
        FirstNonEmpty(
            request.OutputDirectory,
            GetSetting(profile, "outputDirectory"),
            GetSetting(profile, "defaultOutputDirectory"),
            GetSetting(profile, "defaultOutputRoot"),
            GetSetting(profile, "outputRoot"),
            defaults.SharedOutputRoot,
            workerList.SelectMany(worker => worker.Capabilities.SharedOutputRoots).FirstOrDefault(root => root.Exists && root.Writable)?.Path);

    private static string? ResolveUnrealExecutable(string candidate, List<RenderValidationIssueDto> issues)
    {
        var normalized = NormalizeFilePath(candidate, "UnrealExecutable", issues);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (File.Exists(normalized) && Path.GetFileName(normalized).Equals("UnrealEditor-Cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var fromRoot = Path.Combine(normalized, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
        if (File.Exists(fromRoot))
        {
            return fromRoot;
        }

        var alreadyInEngine = Path.Combine(normalized, "Binaries", "Win64", "UnrealEditor-Cmd.exe");
        if (File.Exists(alreadyInEngine))
        {
            return alreadyInEngine;
        }

        return normalized;
    }

    private static string? NormalizeFilePath(string? value, string field, List<RenderValidationIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            var fullPath = Path.GetFullPath(expanded);
            if (!string.Equals(value.Trim().Trim('"'), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new RenderValidationIssueDto("filesystem-path-normalized", RenderValidationSeverity.Info, $"{field} was normalised for validation.", field, "Use the normalised absolute path.", value, fullPath, AutoFixAvailable: true));
            }
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(Error("filesystem-path-invalid", $"{field} could not be normalised: {ex.Message}", field, value));
            return null;
        }
    }

    private static bool CanWriteOutputDirectory(string path, out string error)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".renderfarm_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "renderfarm output probe");
            File.Delete(probe);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            error = $"Output directory is not writable: {ex.Message}";
            return false;
        }
    }

    private static RenderValidationIssueDto Error(string code, string message, string? field = null, string? originalValue = null) =>
        new(code, RenderValidationSeverity.Error, message, field, null, originalValue);

    private static string? GetSetting(RenderProfile profile, string key) =>
        profile.Settings.TryGetValue(key, out var exact)
            ? exact
            : profile.Settings.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}







