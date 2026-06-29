using System.Diagnostics;
using RenderFarm.Domain;

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
    TimeSpan? Timeout);

public sealed record UnrealRenderCommand(string ExecutablePath, IReadOnlyList<string> Arguments, string CommandLine, string LogFilePath);

public sealed record ProcessLaunchResult(int? ExitCode, bool TimedOut, string StandardOutput, string StandardError, FailureCategory FailureCategory);

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
        var moviePipelineConfig = FirstNonEmpty(request.Profile.AssetPath, GetSetting(request.Profile, "moviePipelineConfig"), GetSetting(request.Profile, "mrqConfig"), GetSetting(request.Profile, "queue"));

        yield return request.ProjectPath;

        if (!string.IsNullOrWhiteSpace(mapName))
        {
            yield return mapName;
        }
        else if (IsMovieRenderProfile(request.Profile))
        {
            throw new InvalidOperationException("MRQ render profiles must set settings.map or settings.level, for example Minimal_Default1. Without a map, Unreal can open like a normal project instead of running the command-line render.");
        }

        yield return "-game";

        if (!string.IsNullOrWhiteSpace(moviePipelineConfig))
        {
            yield return $"-MoviePipelineConfig={moviePipelineConfig}";
        }
        else if (IsMovieRenderProfile(request.Profile))
        {
            throw new InvalidOperationException("MRQ render profiles must set assetPath or settings.moviePipelineConfig, for example /Game/Cinematics/myRenderQueue.");
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

        var expanded = template
            .Replace("{project}", request.ProjectPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{uproject}", request.ProjectPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{projectDir}", Path.GetDirectoryName(request.ProjectPath) ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{output}", request.OutputDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{outputDir}", request.OutputDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{asset}", request.Profile.AssetPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{moviePipelineConfig}", request.Profile.AssetPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{map}", FirstNonEmpty(GetSetting(request.Profile, "map"), GetSetting(request.Profile, "mapName"), GetSetting(request.Profile, "level"), GetSetting(request.Profile, "levelName")), StringComparison.OrdinalIgnoreCase)
            .Replace("{level}", FirstNonEmpty(GetSetting(request.Profile, "level"), GetSetting(request.Profile, "map"), GetSetting(request.Profile, "mapName")), StringComparison.OrdinalIgnoreCase);

        foreach (var argument in SplitArguments(expanded))
        {
            yield return argument;
        }
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

    private static string Quote(string value) => value.Contains(' ') || value.Contains('"')
        ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
        : value;

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

public sealed class ProcessLauncher(ILogger<ProcessLauncher> logger) : IProcessLauncher
{
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

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                return new(null, false, string.Empty, "Process did not start.", FailureCategory.UnrealLaunchFailed);
            }

            logger.LogInformation("Started process {ProcessId}: {Executable} {Arguments}", process.Id, executablePath, string.Join(" ", arguments));
        }
        catch (Exception ex)
        {
            return new(null, false, string.Empty, ex.Message, FailureCategory.UnrealLaunchFailed);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }

        var stdout = await SafeReadAsync(stdoutTask);
        var stderr = await SafeReadAsync(stderrTask);
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFilePath))!);
            await File.WriteAllTextAsync(logFilePath, stdout + Environment.NewLine + stderr, cancellationToken);
        }

        var category = timedOut
            ? FailureCategory.RenderProcessTimedOut
            : process.ExitCode == 0 ? FailureCategory.None : FailureCategory.RenderProcessFailed;

        logger.LogInformation("Process {ProcessId} exited with code {ExitCode} and category {Category}", process.Id, process.HasExited ? process.ExitCode : null, category);
        if (category != FailureCategory.None)
        {
            logger.LogWarning("Process {Executable} failed with category {Category} and exit code {ExitCode}", executablePath, category, process.HasExited ? process.ExitCode : null);
        }

        return new(process.HasExited ? process.ExitCode : null, timedOut, stdout, stderr, category);
    }

    private static async Task<string> SafeReadAsync(Task<string> readTask)
    {
        try
        {
            return await readTask;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

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
