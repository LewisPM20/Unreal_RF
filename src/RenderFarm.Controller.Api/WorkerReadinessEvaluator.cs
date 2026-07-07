using System.Globalization;
using RenderFarm.Domain;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public static class WorkerReadinessEvaluator
{
    public static WorkerProjectReadinessDto Evaluate(Worker worker, ProjectProfile project, RenderProfile? profile, string? requestedOutputDirectory = null, RenderDefaultsDto? defaults = null)
    {
        var reasons = new List<string>();
        var workerPath = project.WorkerPaths.FirstOrDefault(x => string.Equals(x.WorkerId, worker.Id, StringComparison.OrdinalIgnoreCase));
        var projectPath = FirstNonEmpty(workerPath?.ProjectPath, GetSetting(profile, "projectPath"), GetSetting(profile, "uprojectPath"), project.UProjectPath);
        var hasProjectPath = HasProjectPath(worker, project, workerPath, projectPath, reasons);
        var (hasUnreal, unrealVersion) = HasCompatibleUnreal(worker, project, profile, workerPath, defaults, reasons);
        var (canWriteOutput, hasEnoughDisk) = CanWriteOutput(worker, profile, requestedOutputDirectory, defaults, reasons);
        var meetsProfileRequirements = MeetsProfileRequirements(worker, profile, reasons);
        var compatibility = RenderFarmVersion.EvaluateWorkerAgent(worker.AgentVersion);
        if (!compatibility.Compatible)
        {
            reasons.Add(compatibility.Reason);
        }

        var heartbeatFresh = DateTimeOffset.UtcNow - worker.LastHeartbeatUtc <= TimeSpan.FromSeconds(30);
        if (!heartbeatFresh)
        {
            reasons.Add($"Worker heartbeat is stale ({Math.Max(0, (int)(DateTimeOffset.UtcNow - worker.LastHeartbeatUtc).TotalSeconds)}s old).");
        }

        var statusOk = worker.Status is WorkerStatus.Online or WorkerStatus.Idle;
        if (!statusOk)
        {
            reasons.Add($"Worker status {worker.Status} is not schedulable.");
        }

        return new WorkerProjectReadinessDto(
            worker.Id,
            project.Id,
            profile?.Id,
            compatibility.Compatible && heartbeatFresh && statusOk && hasProjectPath && hasUnreal && canWriteOutput && hasEnoughDisk && meetsProfileRequirements,
            hasProjectPath,
            hasUnreal,
            canWriteOutput,
            hasEnoughDisk,
            projectPath,
            unrealVersion,
            reasons);
    }

    private static bool HasProjectPath(Worker worker, ProjectProfile project, WorkerProjectPath? workerPath, string? projectPath, List<string> reasons)
    {
        if (project.WorkerPaths.Count > 0 && workerPath is null)
        {
            reasons.Add("Project has worker-specific paths but none for this worker.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            reasons.Add("No project path is configured.");
            return false;
        }

        var reported = worker.Capabilities.ProjectPaths.FirstOrDefault(x => string.Equals(x.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (reported is { Exists: false })
        {
            reasons.Add($"Worker reports project path missing: {projectPath}");
            return false;
        }

        return true;
    }

    private static (bool HasUnreal, string? Version) HasCompatibleUnreal(Worker worker, ProjectProfile project, RenderProfile? profile, WorkerProjectPath? workerPath, RenderDefaultsDto? defaults, List<string> reasons)
    {
        if (!string.IsNullOrWhiteSpace(workerPath?.EnginePath))
        {
            return (true, project.PreferredEngineVersion ?? project.AllowedEngineVersions.FirstOrDefault());
        }

        var controllerManagedEngine = FirstNonEmpty(
            GetSetting(profile, "unrealExecutablePath"),
            GetSetting(profile, "unrealExe"),
            GetSetting(profile, "unrealCommand"),
            GetSetting(profile, "unrealSearchRoot"),
            defaults?.UnrealExecutablePath,
            defaults?.UnrealSearchRoot);
        if (!string.IsNullOrWhiteSpace(controllerManagedEngine))
        {
            return (true, project.PreferredEngineVersion ?? project.AllowedEngineVersions.FirstOrDefault());
        }

        var desiredVersions = new[] { project.PreferredEngineVersion }
            .Concat(project.AllowedEngineVersions)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var installs = worker.Capabilities.UnrealInstallations.Where(x => x.Exists).ToArray();
        if (desiredVersions.Length == 0)
        {
            if (installs.Length == 0)
            {
                reasons.Add("No controller-managed Unreal path is set and the worker has not reported a usable Unreal installation.");
                return (false, null);
            }

            return (true, installs.Last().Version);
        }

        var match = installs.FirstOrDefault(install => desiredVersions.Contains(install.Version, StringComparer.OrdinalIgnoreCase));
        if (match is null)
        {
            reasons.Add($"No controller-managed Unreal path is set and the worker lacks required Unreal version: {string.Join(", ", desiredVersions)}");
            return (false, null);
        }

        return (true, match.Version);
    }

    private static (bool CanWrite, bool EnoughDisk) CanWriteOutput(Worker worker, RenderProfile? profile, string? requestedOutputDirectory, RenderDefaultsDto? defaults, List<string> reasons)
    {
        var outputRoot = FirstNonEmpty(
            requestedOutputDirectory,
            GetSetting(profile, "defaultOutputRoot"),
            GetSetting(profile, "outputRoot"),
            defaults?.SharedOutputRoot);
        var minFreeGb = TryGetDouble(GetSetting(profile, "minFreeGb"));
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return (true, true);
        }

        var reported = worker.Capabilities.SharedOutputRoots.FirstOrDefault(root =>
            outputRoot.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase) ||
            root.Path.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase));
        if (reported is null)
        {
            reasons.Add($"Output path will be validated by the worker when assigned: {outputRoot}");
            return (true, true);
        }

        if (!reported.Exists || !reported.Writable)
        {
            reasons.Add($"Worker cannot write output root: {reported.Path}");
            return (false, false);
        }

        if (minFreeGb is { } required && reported.FreeDiskGb is { } free && free < required)
        {
            reasons.Add($"Output root has {free:0.##} GB free, below required {required:0.##} GB.");
            return (true, false);
        }

        return (true, true);
    }

    private static bool MeetsProfileRequirements(Worker worker, RenderProfile? profile, List<string> reasons)
    {
        var ok = true;
        if (TryGetInt(GetSetting(profile, "minCpuCores")) is { } minCpuCores)
        {
            if (worker.Capabilities.CpuCores is not { } cores || cores < minCpuCores)
            {
                reasons.Add(worker.Capabilities.CpuCores is { } reported
                    ? $"Worker CPU cores {reported} is below required {minCpuCores}."
                    : $"Worker CPU cores were not reported; required {minCpuCores}.");
                ok = false;
            }
        }

        if (TryGetDouble(GetSetting(profile, "minRamGb")) is { } minRamGb)
        {
            if (worker.Capabilities.RamGb is not { } ramGb || ramGb < minRamGb)
            {
                reasons.Add(worker.Capabilities.RamGb is { } reported
                    ? $"Worker RAM {reported:0.##} GB is below required {minRamGb:0.##} GB."
                    : $"Worker RAM was not reported; required {minRamGb:0.##} GB.");
                ok = false;
            }
        }

        if (TryGetDouble(GetSetting(profile, "minVramGb")) is { } minVramGb)
        {
            if (worker.Capabilities.VramGb is not { } vramGb || vramGb < minVramGb)
            {
                reasons.Add(worker.Capabilities.VramGb is { } reported
                    ? $"Worker VRAM {reported:0.##} GB is below required {minVramGb:0.##} GB."
                    : $"Worker VRAM was not reported; required {minVramGb:0.##} GB.");
                ok = false;
            }
        }

        var requiredGpuName = GetSetting(profile, "gpuNameContains");
        if (!string.IsNullOrWhiteSpace(requiredGpuName) && (worker.Capabilities.GpuName?.Contains(requiredGpuName, StringComparison.OrdinalIgnoreCase) != true))
        {
            reasons.Add($"Worker GPU name does not contain {requiredGpuName}.");
            ok = false;
        }

        return ok;
    }

    private static string? GetSetting(RenderProfile? profile, string key)
    {
        if (profile is null)
        {
            return null;
        }

        return profile.Settings.TryGetValue(key, out var exact)
            ? exact
            : profile.Settings.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static double? TryGetDouble(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static int? TryGetInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
}
