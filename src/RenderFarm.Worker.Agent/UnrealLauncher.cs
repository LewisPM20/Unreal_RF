using System.Diagnostics;
using System.Text.RegularExpressions;
using RenderFarm.Domain;
using RenderFarm.Shared;

namespace RenderFarm.Worker.Agent;

public interface IUnrealEngineLocator
{
    IReadOnlyList<UnrealEngineInstallation> FindInstallations(IEnumerable<string> searchRoots);
    UnrealEngineInstallation? Resolve(string? preferredVersion, IEnumerable<UnrealEngineInstallation> installations);
}

public sealed class UnrealEngineLocator : IUnrealEngineLocator
{
    private const string UnrealCmdFileName = "UnrealEditor-Cmd.exe";

    public IReadOnlyList<UnrealEngineInstallation> FindInstallations(IEnumerable<string> searchRoots)
    {
        var roots = searchRoots
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(ExpandSearchRoot)
            .ToList();

        var epicRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
        if (Directory.Exists(epicRoot))
        {
            roots.AddRange(ExpandSearchRoot(epicRoot));
        }

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ToInstallation)
            .Where(x => !string.IsNullOrWhiteSpace(x.RootPath))
            .OrderBy(x => NormalizeVersion(x.Version), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public UnrealEngineInstallation? Resolve(string? preferredVersion, IEnumerable<UnrealEngineInstallation> installations)
    {
        var candidates = installations.Where(x => x.Exists).ToArray();
        if (!string.IsNullOrWhiteSpace(preferredVersion))
        {
            var requested = NormalizeVersion(preferredVersion);
            return candidates.FirstOrDefault(x => string.Equals(NormalizeVersion(x.Version), requested, StringComparison.OrdinalIgnoreCase));
        }

        return candidates.LastOrDefault();
    }

    private static IEnumerable<string> ExpandSearchRoot(string rawRoot)
    {
        var root = Environment.ExpandEnvironmentVariables(rawRoot.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(root))
        {
            yield break;
        }

        if (File.Exists(root) && IsUnrealCommandExecutable(root))
        {
            yield return GetEngineRootFromExecutable(root);
            yield break;
        }

        if (!Directory.Exists(root))
        {
            yield return root;
            yield break;
        }

        if (TryGetEngineRootFromDirectory(root) is { } engineRoot)
        {
            yield return engineRoot;
        }

        foreach (var child in SafeEnumerateDirectories(root, "UE_*"))
        {
            if (TryGetEngineRootFromDirectory(child) is { } childEngineRoot)
            {
                yield return childEngineRoot;
            }
        }
    }

    private static UnrealEngineInstallation ToInstallation(string root)
    {
        var fullRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root.Trim().Trim('"')));
        var executable = Path.Combine(fullRoot, "Engine", "Binaries", "Win64", UnrealCmdFileName);
        var version = Path.GetFileName(fullRoot).Replace("UE_", string.Empty, StringComparison.OrdinalIgnoreCase);
        return new UnrealEngineInstallation(version, fullRoot, executable, File.Exists(executable));
    }

    private static string? TryGetEngineRootFromDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var directExecutable = Path.Combine(fullPath, "Engine", "Binaries", "Win64", UnrealCmdFileName);
        if (File.Exists(directExecutable))
        {
            return fullPath;
        }

        var engineDirectoryExecutable = Path.Combine(fullPath, "Binaries", "Win64", UnrealCmdFileName);
        if (File.Exists(engineDirectoryExecutable) && string.Equals(Path.GetFileName(fullPath), "Engine", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(fullPath, ".."));
        }

        var win64Executable = Path.Combine(fullPath, UnrealCmdFileName);
        if (File.Exists(win64Executable) && string.Equals(Path.GetFileName(fullPath), "Win64", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(fullPath, "..", "..", ".."));
        }

        return null;
    }

    private static string GetEngineRootFromExecutable(string executablePath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executablePath))!, "..", "..", ".."));

    private static bool IsUnrealCommandExecutable(string path) =>
        string.Equals(Path.GetFileName(path), UnrealCmdFileName, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string searchPattern)
    {
        try
        {
            return Directory.EnumerateDirectories(root, searchPattern);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static string NormalizeVersion(string? version) =>
        (version ?? string.Empty)
            .Trim()
            .Replace("UE_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('_', '.');
}

public sealed record UnrealRenderRequest(
    string UnrealExecutablePath,
    string ProjectPath,
    RenderProfile Profile,
    string OutputDirectory,
    string LogDirectory,
    TimeSpan? Timeout,
    IReadOnlyDictionary<string, string>? Variables = null);

public sealed record UnrealRenderCommand(string ExecutablePath, IReadOnlyList<string> Arguments, string CommandLine, string LogFilePath);

public sealed record ProcessLaunchResult(int? ExitCode, bool TimedOut, string StandardOutput, string StandardError, FailureCategory FailureCategory);

public sealed class RenderProcessExecutionOptions
{
    public int MaxCapturedOutputCharacters { get; set; } = 262_144;
    public int NoProgressTimeoutSeconds { get; set; } = 0;
}

public interface IUnrealCommandBuilder
{
    UnrealRenderCommand Build(UnrealRenderRequest request, string jobId, int attemptNumber);
}

public sealed class UnrealCommandBuilder : IUnrealCommandBuilder
{
    public UnrealRenderCommand Build(UnrealRenderRequest request, string jobId, int attemptNumber)
    {
        if (!File.Exists(request.UnrealExecutablePath))
        {
            throw new FileNotFoundException("Unreal command executable was not found.", request.UnrealExecutablePath);
        }

        if (!File.Exists(request.ProjectPath) || !request.ProjectPath.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Unreal project .uproject file was not found.", request.ProjectPath);
        }

        Directory.CreateDirectory(request.OutputDirectory);
        Directory.CreateDirectory(request.LogDirectory);

        var arguments = BuildArguments(request).ToArray();
        var logFile = Path.Combine(request.LogDirectory, $"{SanitizeFileName(jobId)}_attempt_{attemptNumber:00}.log");
        var commandLine = Quote(request.UnrealExecutablePath) + " " + string.Join(" ", arguments.Select(Quote));
        return new UnrealRenderCommand(request.UnrealExecutablePath, arguments, commandLine, logFile);
    }

    private static IEnumerable<string> BuildArguments(UnrealRenderRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Profile.CommandTemplate))
        {
            var expandedArguments = ExpandTemplate(request.Profile.CommandTemplate, request).ToArray();
            if (!expandedArguments.Any(IsProjectArgument))
            {
                yield return request.ProjectPath;
            }

            foreach (var argument in expandedArguments.Where(x => !IsExecutableArgument(x)))
            {
                yield return argument;
            }

            yield break;
        }

        var mapName = FirstNonEmpty(GetSetting(request.Profile, "map"), GetSetting(request.Profile, "mapName"), GetSetting(request.Profile, "level"), GetSetting(request.Profile, "levelName"));
        var levelSequence = FirstNonEmpty(GetSetting(request.Profile, "sequence"), GetSetting(request.Profile, "levelSequence"));
        var moviePipelineConfig = FirstNonEmpty(request.Profile.AssetPath, GetSetting(request.Profile, "moviePipelineConfig"), GetSetting(request.Profile, "mrqConfig"), GetSetting(request.Profile, "queue"));
        var launchMode = ResolveMovieRenderLaunchMode(request.Profile, levelSequence);

        yield return request.ProjectPath;

        if (!string.IsNullOrWhiteSpace(mapName))
        {
            yield return NormalizeOrThrow(mapName, UnrealAssetPathKind.WorldPackagePath, "Map/world path");
        }
        else if (IsMovieRenderProfile(request.Profile))
        {
            throw new InvalidOperationException("MRQ/MRG render profiles must set a map argument such as Minimal_Default1 or /Game/Maps/MainMap. Do not use /Game/Maps/MainMap.MainMap for the map argument.");
        }

        yield return "-game";

        if (IsMovieRenderProfile(request.Profile) && launchMode == MovieRenderLaunchMode.SingleSequence && string.IsNullOrWhiteSpace(levelSequence))
        {
            throw new InvalidOperationException("Single Sequence MRQ mode requires a Level Sequence object path such as /Game/Cinematics/Shot01.Shot01. If your -MoviePipelineConfig value is a saved queue asset instead, set mrqMode/renderMode/movieRenderMode to Queue.");
        }

        if (IsMovieRenderProfile(request.Profile) && launchMode == MovieRenderLaunchMode.SavedQueue && !string.IsNullOrWhiteSpace(levelSequence) && HasExplicitMovieRenderLaunchMode(request.Profile))
        {
            throw new InvalidOperationException("Saved MRQ Queue mode should not set a separate Level Sequence. Remove the sequence field, or switch the profile to Single Sequence + Config mode.");
        }

        if (launchMode == MovieRenderLaunchMode.SingleSequence && !string.IsNullOrWhiteSpace(levelSequence))
        {
            yield return $"-LevelSequence={NormalizeOrThrow(levelSequence, UnrealAssetPathKind.LevelSequenceObjectPath, "Level Sequence path")}";
        }

        if (!string.IsNullOrWhiteSpace(moviePipelineConfig))
        {
            var configKind = launchMode == MovieRenderLaunchMode.SavedQueue
                ? UnrealAssetPathKind.MoviePipelineQueueObjectPath
                : UnrealAssetPathKind.MoviePipelineConfigObjectPath;
            yield return $"-MoviePipelineConfig={NormalizeOrThrow(moviePipelineConfig, configKind, "Movie Pipeline config/queue path")}";
        }
        else if (IsMovieRenderProfile(request.Profile))
        {
            throw new InvalidOperationException(launchMode == MovieRenderLaunchMode.SavedQueue
                ? "Saved MRQ Queue mode requires a saved queue asset path such as /Game/Cinematics/myRenderQueue."
                : "Single Sequence MRQ mode requires a saved Movie Pipeline config preset such as /Game/RenderConfig.RenderConfig.");
        }

        yield return "-windowed";
        yield return "-Log";
        yield return "-StdOut";
        yield return "-allowStdOutLogVerbosity";
        yield return "-Unattended";

        if (IsTruthy(GetSetting(request.Profile, "includeOutputDirArg")) || IsTruthy(GetSetting(request.Profile, "passOutputDirectory")))
        {
            yield return $"-OutputDir={request.OutputDirectory}";
        }

        foreach (var argument in ExpandTemplate(GetSetting(request.Profile, "extraArgs") ?? string.Empty, request))
        {
            yield return argument;
        }
    }

    private static IEnumerable<string> ExpandTemplate(string template, UnrealRenderRequest request)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            yield break;
        }

        var sequence = FirstNonEmpty(GetSetting(request.Profile, "sequence"), GetSetting(request.Profile, "levelSequence"));
        var assetKind = ResolveMovieRenderLaunchMode(request.Profile, sequence) == MovieRenderLaunchMode.SavedQueue
            ? UnrealAssetPathKind.MoviePipelineQueueObjectPath
            : UnrealAssetPathKind.MoviePipelineConfigObjectPath;

        var expanded = template
            .Replace("{project}", request.ProjectPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{uproject}", request.ProjectPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{projectDir}", Path.GetDirectoryName(request.ProjectPath) ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{output}", request.OutputDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{outputDir}", request.OutputDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{asset}", NormalizeTemplateValue(request.Profile.AssetPath, assetKind), StringComparison.OrdinalIgnoreCase)
            .Replace("{moviePipelineConfig}", NormalizeTemplateValue(request.Profile.AssetPath, assetKind), StringComparison.OrdinalIgnoreCase)
            .Replace("{map}", NormalizeTemplateValue(FirstNonEmpty(GetSetting(request.Profile, "map"), GetSetting(request.Profile, "mapName"), GetSetting(request.Profile, "level"), GetSetting(request.Profile, "levelName")), UnrealAssetPathKind.WorldPackagePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{level}", NormalizeTemplateValue(FirstNonEmpty(GetSetting(request.Profile, "level"), GetSetting(request.Profile, "map"), GetSetting(request.Profile, "mapName")), UnrealAssetPathKind.WorldPackagePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{sequence}", NormalizeTemplateValue(sequence, UnrealAssetPathKind.LevelSequenceObjectPath), StringComparison.OrdinalIgnoreCase);

        foreach (var variable in request.Variables ?? new Dictionary<string, string>())
        {
            expanded = expanded.Replace("{" + variable.Key + "}", variable.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        var unresolvedTokens = Regex.Matches(expanded, "\\{[A-Za-z][A-Za-z0-9_]*\\}")
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unresolvedTokens.Length > 0)
        {
            throw new InvalidOperationException($"Render command template contains unresolved token(s): {string.Join(", ", unresolvedTokens)}.");
        }

        foreach (var argument in SplitArguments(expanded))
        {
            yield return argument;
        }
    }

    private static string NormalizeOrThrow(string value, UnrealAssetPathKind kind, string label)
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference(value, kind);
        if (!result.Success || string.IsNullOrWhiteSpace(result.NormalizedPath))
        {
            throw new InvalidOperationException($"{label} is invalid: {result.Error}");
        }

        return result.NormalizedPath;
    }

    private static string NormalizeTemplateValue(string? value, UnrealAssetPathKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return NormalizeOrThrow(value, kind, "Command template asset path");
    }
    private static IEnumerable<string> SplitArguments(string commandLine)
    {
        var current = new List<char>();
        var inQuotes = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Count > 0)
                {
                    yield return new string(current.ToArray());
                    current.Clear();
                }
                continue;
            }

            current.Add(ch);
        }

        if (current.Count > 0)
        {
            yield return new string(current.ToArray());
        }
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

    private static bool IsMovieRenderProfile(RenderProfile profile) =>
        profile.Type is RenderProfileType.MrqQueue or RenderProfileType.MrgGraph;

    private static bool IsExecutableArgument(string value) =>
        value.EndsWith("UnrealEditor-Cmd.exe", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith("UnrealEditor.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectArgument(string value) =>
        value.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase);

    private static string? GetSetting(RenderProfile profile, string key)
    {
        if (profile.Settings.TryGetValue(key, out var exact))
        {
            return exact;
        }

        return profile.Settings.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static bool IsTruthy(string? value) =>
        bool.TryParse(value, out var parsed)
            ? parsed
            : string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(value, "y", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    private static string Quote(string value)
    {
        var equalsIndex = value.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex > 0 && value.StartsWith("-", StringComparison.Ordinal))
        {
            var key = value[..(equalsIndex + 1)];
            var argumentValue = value[(equalsIndex + 1)..];
            return key + '"' + argumentValue.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
        }

        return value.Contains(' ') || value.Contains('"')
            ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
            : value;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}

public interface IProcessLauncher
{
    Task<ProcessLaunchResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, string? logFilePath, TimeSpan? timeout, CancellationToken cancellationToken);
}

public sealed class ProcessLauncher(ILogger<ProcessLauncher> logger, Microsoft.Extensions.Options.IOptions<RenderProcessExecutionOptions>? options = null) : IProcessLauncher
{
    private readonly RenderProcessExecutionOptions executionOptions = options?.Value ?? new RenderProcessExecutionOptions();

    public async Task<ProcessLaunchResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, string? logFilePath, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["RENDERFARM_OWNED_PROCESS"] = "1";
        startInfo.Environment["RENDERFARM_INSTANCE_ROOT"] = AppContext.BaseDirectory;

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var captured = new BoundedProcessOutput(Math.Max(4096, executionOptions.MaxCapturedOutputCharacters));
        var lastProgressUtc = DateTimeOffset.UtcNow;
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                captured.AppendOutput(args.Data);
                lastProgressUtc = DateTimeOffset.UtcNow;
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                captured.AppendError(args.Data);
                lastProgressUtc = DateTimeOffset.UtcNow;
            }
        };

        try
        {
            if (!process.Start())
            {
                return new(null, false, string.Empty, "Process did not start.", FailureCategory.UnrealLaunchFailed);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            logger.LogInformation("Started process {ProcessId}: {Executable} {Arguments}", process.Id, executablePath, string.Join(" ", arguments));
        }
        catch (Exception ex)
        {
            return new(null, false, string.Empty, ex.Message, FailureCategory.UnrealLaunchFailed);
        }

        var timedOut = false;
        var noProgressTimedOut = false;
        var startedUtc = DateTimeOffset.UtcNow;
        var waitTask = process.WaitForExitAsync(cancellationToken);
        try
        {
            while (!waitTask.IsCompleted)
            {
                var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
                if (completed == waitTask)
                {
                    break;
                }

                var now = DateTimeOffset.UtcNow;
                if (timeout is not null && now - startedUtc >= timeout.Value)
                {
                    timedOut = true;
                    TryKill(process);
                    break;
                }

                var noProgressTimeout = executionOptions.NoProgressTimeoutSeconds;
                if (noProgressTimeout > 0 && now - lastProgressUtc >= TimeSpan.FromSeconds(noProgressTimeout))
                {
                    noProgressTimedOut = true;
                    TryKill(process);
                    break;
                }
            }

            await waitTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKill(process);
        }

        var stdout = captured.StandardOutput;
        var stderr = captured.StandardError;
        if (noProgressTimedOut)
        {
            stderr = AppendLine(stderr, $"Render process produced no stdout/stderr progress for {executionOptions.NoProgressTimeoutSeconds} second(s); watchdog stopped it.");
        }
        else if (timedOut)
        {
            stderr = AppendLine(stderr, timeout is null ? "Render process timed out; watchdog stopped it." : $"Render process exceeded timeout {timeout.Value}; watchdog stopped it.");
        }

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFilePath))!);
            await File.WriteAllTextAsync(logFilePath, stdout + Environment.NewLine + stderr, cancellationToken);
        }

        var category = timedOut || noProgressTimedOut
            ? FailureCategory.RenderProcessTimedOut
            : process.ExitCode == 0 ? FailureCategory.None : FailureCategory.RenderProcessFailed;

        logger.LogInformation("Process {ProcessId} exited with code {ExitCode} and category {Category}", process.Id, process.HasExited ? process.ExitCode : null, category);
        if (category != FailureCategory.None)
        {
            logger.LogWarning("Process {Executable} failed with category {Category} and exit code {ExitCode}", executablePath, category, process.HasExited ? process.ExitCode : null);
        }

        return new(process.HasExited ? process.ExitCode : null, timedOut || noProgressTimedOut, stdout, stderr, category);
    }

    private static string AppendLine(string existing, string line) =>
        string.IsNullOrWhiteSpace(existing) ? line : existing + Environment.NewLine + line;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }

    private sealed class BoundedProcessOutput(int maxCharacters)
    {
        private readonly object syncRoot = new();
        private readonly Queue<string> stdout = new();
        private readonly Queue<string> stderr = new();
        private int stdoutLength;
        private int stderrLength;

        public string StandardOutput
        {
            get
            {
                lock (syncRoot)
                {
                    return string.Join(Environment.NewLine, stdout);
                }
            }
        }

        public string StandardError
        {
            get
            {
                lock (syncRoot)
                {
                    return string.Join(Environment.NewLine, stderr);
                }
            }
        }

        public void AppendOutput(string line) => Append(stdout, ref stdoutLength, line);

        public void AppendError(string line) => Append(stderr, ref stderrLength, line);

        private void Append(Queue<string> queue, ref int length, string line)
        {
            lock (syncRoot)
            {
                var value = line.Length > maxCharacters ? line[^maxCharacters..] : line;
                queue.Enqueue(value);
                length += value.Length + Environment.NewLine.Length;
                while (length > maxCharacters && queue.Count > 1)
                {
                    var removed = queue.Dequeue();
                    length -= removed.Length + Environment.NewLine.Length;
                }
            }
        }
    }
}
public interface IUnrealProcessLauncher
{
    Task<ProcessLaunchResult> LaunchRenderAsync(UnrealRenderRequest request, string jobId, int attemptNumber, CancellationToken cancellationToken);
}

public sealed class UnrealProcessLauncher(IUnrealCommandBuilder commandBuilder, IProcessLauncher processLauncher) : IUnrealProcessLauncher
{
    public Task<ProcessLaunchResult> LaunchRenderAsync(UnrealRenderRequest request, string jobId, int attemptNumber, CancellationToken cancellationToken)
    {
        var command = commandBuilder.Build(request, jobId, attemptNumber);
        return processLauncher.RunAsync(command.ExecutablePath, command.Arguments, command.LogFilePath, request.Timeout, cancellationToken);
    }
}




