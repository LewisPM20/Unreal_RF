namespace RenderFarm.Domain;

/// <summary>
/// A controller-known render worker and the latest state reported by its agent.
/// </summary>
public sealed record Worker(
    string Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? ServiceUrl,
    WorkerStatus Status,
    string? Stage,
    string? CurrentJobId,
    string? AgentVersion,
    WorkerCapabilities Capabilities,
    string? LastError,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastHeartbeatUtc);

/// <summary>
/// Runtime status for a worker agent.
/// </summary>
public enum WorkerStatus
{
    Unknown,
    Pending,
    Rejected,
    Online,
    Stale,
    Offline,
    Idle,
    Busy,
    Error,
    IncompatibleVersion,
    Disabled
}

/// <summary>
/// Hardware, filesystem, and Unreal capabilities reported by a worker.
/// </summary>
public sealed record WorkerCapabilities(
    int? CpuCores,
    double? RamGb,
    string? GpuName,
    double? VramGb,
    double? FreeDiskGb,
    IReadOnlyList<UnrealEngineInstallation> UnrealInstallations,
    IReadOnlyList<ProjectPathStatus> ProjectPaths,
    IReadOnlyList<SharedOutputStatus> SharedOutputRoots)
{
    /// <summary>
    /// Empty capability report for a worker that could not probe local resources.
    /// </summary>
    public static WorkerCapabilities Empty { get; } = new(
        null,
        null,
        null,
        null,
        null,
        Array.Empty<UnrealEngineInstallation>(),
        Array.Empty<ProjectPathStatus>(),
        Array.Empty<SharedOutputStatus>());
}

/// <summary>
/// Installed Unreal Engine command-line executable visible to a worker.
/// </summary>
public sealed record UnrealEngineInstallation(
    string Version,
    string RootPath,
    string ExecutablePath,
    bool Exists);

/// <summary>
/// A project path probe reported by a worker.
/// </summary>
public sealed record ProjectPathStatus(string Path, bool Exists);

/// <summary>
/// A shared render-output root probe reported by a worker.
/// </summary>
public sealed record SharedOutputStatus(string Path, bool Exists, bool Writable, double? FreeDiskGb = null, string? Message = null);

/// <summary>
/// Policy for writing rendered frames to a shared output location.
/// </summary>
public sealed record SharedOutputPolicy(
    string Id,
    string Name,
    string RootPath,
    bool CreateJobSubdirectory,
    bool AllowOverwrite,
    double? MinFreeGb);

/// <summary>
/// Farm-level Unreal project definition. Worker-specific drive mappings live in WorkerPaths.
/// </summary>
public sealed record ProjectProfile(
    string Id,
    string DisplayName,
    string? UProjectPath,
    string? PreferredEngineVersion,
    IReadOnlyList<string> AllowedEngineVersions,
    string? SharedOutputPolicyId,
    IReadOnlyList<WorkerProjectPath> WorkerPaths);

/// <summary>
/// Per-worker engine/project/log path mapping for a project.
/// </summary>
public sealed record WorkerProjectPath(
    string Id,
    string ProjectId,
    string WorkerId,
    string EnginePath,
    string ProjectPath,
    string? LogDirectory);

/// <summary>
/// A renderable queue, graph, or controlled command profile inside an Unreal project.
/// </summary>
public sealed record RenderProfile(
    string Id,
    string ProjectId,
    string DisplayName,
    RenderProfileType Type,
    string? AssetPath,
    string? CommandTemplate,
    string DefaultOutputType,
    bool SupportsChunking,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// Supported render profile kinds.
/// </summary>
public enum RenderProfileType
{
    MrqQueue,
    MrgGraph,
    CommandTemplate,
    Manual
}

/// <summary>
/// Render job tracked and scheduled by the controller.
/// </summary>
public sealed record RenderJob(
    string Id,
    string ProjectId,
    string RenderProfileId,
    string Name,
    JobState State,
    int Priority,
    string? AssignedWorkerId,
    FailureCategory FailureCategory,
    string? Error,
    string? OutputDirectory,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    bool CancellationRequested,
    string? ValidationJson = null);

/// <summary>
/// One execution attempt for a render job.
/// </summary>
public sealed record JobAttempt(
    string Id,
    string JobId,
    int AttemptNumber,
    string? WorkerId,
    JobState State,
    FailureCategory FailureCategory,
    string? Error,
    string? CommandLine,
    string? LogFilePath,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int? ExitCode);

/// <summary>
/// Append-only event describing a state transition or diagnostic for a job.
/// </summary>
public sealed record JobEvent(
    string Id,
    string JobId,
    string? JobAttemptId,
    string? WorkerId,
    JobState? State,
    FailureCategory FailureCategory,
    string Message,
    DateTimeOffset CreatedAtUtc,
    string? DataJson);
/// <summary>
/// Controller setting stored as caller-owned JSON.
/// </summary>
public sealed record FarmSetting(string Key, string ValueJson, DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Time-bounded reservation that prevents a queued job from being assigned to multiple workers.
/// </summary>
public sealed record JobLease(
    string Id,
    string JobId,
    string JobAttemptId,
    string WorkerId,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RenewedAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    string? ReleaseReason,
    bool IsActive);


