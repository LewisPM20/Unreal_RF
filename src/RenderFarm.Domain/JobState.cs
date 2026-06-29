namespace RenderFarm.Domain;

/// <summary>
/// High-level lifecycle state for a render job or attempt.
/// </summary>
public enum JobState
{
    Created,
    Queued,
    Reserved,
    Running,
    ValidatingWorker,
    PreparingUnrealQueue,
    LaunchingUnreal,
    Rendering,
    VerifyingOutputs,
    Succeeded,
    Failed,
    CancelRequested,
    Cancelling,
    Cancelled,
    Stale,
    RetryWait
}

/// <summary>
/// Structured reason why a job, worker, or output validation operation failed.
/// </summary>
public enum FailureCategory
{
    None,
    WorkerOffline,
    WorkerStale,
    SharedOutputUnreachable,
    SharedOutputNotWritable,
    ProjectPathMissing,
    UProjectMissing,
    EngineVersionMissing,
    UnrealExecutableMissing,
    UnrealLaunchFailed,
    UnrealPythonFailed,
    MrqAssetMissing,
    RenderProcessFailed,
    RenderProcessTimedOut,
    RenderOutputMissing,
    RenderOutputIncomplete,
    CommandValidationFailed,
    CancelledByUser,
    Unknown
}