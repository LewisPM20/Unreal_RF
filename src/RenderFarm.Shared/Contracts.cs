using RenderFarm.Domain;

namespace RenderFarm.Shared;

/// <summary>
/// Operator-controlled scheduling mode for a worker.
/// </summary>
public enum WorkerSchedulingMode
{
    Active,
    Draining,
    Disabled
}

/// <summary>
/// Heartbeat sent from worker agent to controller.
/// </summary>
public sealed record WorkerHeartbeatDto(
    string WorkerId,
    string? Name,
    string? Hostname,
    string? Ip,
    string? ServiceUrl,
    string Status,
    string? Stage,
    string? CurrentJobId,
    string? AgentVersion,
    WorkerCapabilitiesDto Capabilities,
    string? LastError);

/// <summary>
/// Worker information returned by the controller.
/// </summary>
public sealed record WorkerDto(
    string Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? ServiceUrl,
    WorkerStatus Status,
    string? Stage,
    string? CurrentJobId,
    string? AgentVersion,
    WorkerCapabilitiesDto Capabilities,
    string? LastError,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastHeartbeatUtc);

/// <summary>
/// Serializable worker capability DTO used by controller/worker APIs.
/// </summary>
public sealed record WorkerCapabilitiesDto(
    int? CpuCores,
    double? RamGb,
    string? GpuName,
    double? VramGb,
    double? FreeDiskGb,
    IReadOnlyList<UnrealEngineInstallationDto> UnrealInstallations,
    IReadOnlyList<ProjectPathStatusDto> ProjectPaths,
    IReadOnlyList<SharedOutputStatusDto> SharedOutputRoots);

/// <summary>
/// Backwards-compatible alias for older heartbeat payloads.
/// </summary>
public sealed record UnrealInstallDto(string Version, string Root, string CommandPath, bool Exists);

/// <summary>
/// Unreal install report from a worker.
/// </summary>
public sealed record UnrealEngineInstallationDto(string Version, string RootPath, string ExecutablePath, bool Exists);

/// <summary>
/// Generic path status report.
/// </summary>
public sealed record PathStatusDto(string Path, bool Exists, bool Writable);

/// <summary>
/// Project path probe returned by a worker.
/// </summary>
public sealed record ProjectPathStatusDto(string Path, bool Exists);

/// <summary>
/// Shared output root probe returned by a worker.
/// </summary>
public sealed record SharedOutputStatusDto(string Path, bool Exists, bool Writable, double? FreeDiskGb = null, string? Message = null);

/// <summary>
/// Request to validate direct render output to a shared root.
/// </summary>
public sealed record SharedOutputValidationRequest(string SharedOutputRoot, string JobOutputDirectory, bool CreateTestFile = true, double? MinFreeGb = null);

/// <summary>
/// Result of shared output validation.
/// </summary>
public sealed record SharedOutputValidationResult(bool Ok, FailureCategory FailureCategory, string Message, string? JobOutputDirectory = null);

/// <summary>
/// Shared output write policy exposed by the controller API.
/// </summary>
public sealed record SharedOutputPolicyDto(string Id, string Name, string RootPath, bool CreateJobSubdirectory, bool AllowOverwrite, double? MinFreeGb);

/// <summary>
/// Controller-side evaluation of whether a worker can run a project/profile.
/// </summary>
public sealed record WorkerProjectReadinessDto(
    string WorkerId,
    string ProjectId,
    string? RenderProfileId,
    bool CanRun,
    bool HasProjectPath,
    bool HasCompatibleUnreal,
    bool CanWriteOutput,
    bool HasEnoughDisk,
    string? ProjectPath,
    string? UnrealVersion,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Readiness matrix for one project/profile across known workers.
/// </summary>
public sealed record ReadinessMatrixDto(string ProjectId, string? RenderProfileId, IReadOnlyList<WorkerProjectReadinessDto> Workers);

/// <summary>
/// Result from the optional Unreal project scanner.
/// </summary>
public sealed record UnrealProjectScanResultDto(
    string ProjectPath,
    string? EngineVersion,
    IReadOnlyList<string> Maps,
    IReadOnlyList<string> LevelSequences,
    IReadOnlyList<string> MovieRenderQueueConfigs,
    IReadOnlyList<string> MovieRenderGraphs,
    IReadOnlyList<string> RelevantPlugins,
    bool UsedUnrealBridge,
    bool Ok,
    string? Error);

/// <summary>
/// Request to scan a project for maps, sequences, MRQ configs, and related assets.
/// </summary>
public sealed record UnrealProjectScanRequest(string? WorkerId = null, string? ProjectPath = null, bool UseUnrealBridge = false, int TimeoutSeconds = 120);

/// <summary>
/// Structured render request persisted before launching Unreal.
/// </summary>
public sealed record PreparedRenderRequestDto(
    string ProjectPath,
    string? Map,
    string? Sequence,
    string? MoviePipelineConfig,
    string OutputDirectory,
    string FileFormat,
    int? FrameStart,
    int? FrameEnd,
    int? ChunkIndex,
    string JobId,
    string AttemptId);

/// <summary>
/// Output of render preparation.
/// </summary>
public sealed record RenderPreparationResultDto(bool Ok, string RequestJsonPath, PreparedRenderRequestDto Request, string? Error);

/// <summary>
/// Summary of files produced by a render attempt.
/// </summary>
public sealed record RenderArtifactSummaryDto(string OutputDirectory, int FileCount, long TotalBytes, IReadOnlyList<string> SampleFiles);

/// <summary>
/// Generic terminal job notification payload sent by the controller webhook sink.
/// </summary>
public sealed record JobNotificationPayloadDto(
    string JobId,
    string Name,
    JobState FinalState,
    string ProjectId,
    string RenderProfileId,
    string? WorkerId,
    FailureCategory FailureCategory,
    string? Error,
    string? OutputDirectory,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc);

/// <summary>
/// Farm-level project definition DTO.
/// </summary>
public sealed record ProjectProfileDto(
    string Id,
    string DisplayName,
    string? UProjectPath,
    string? PreferredEngineVersion,
    IReadOnlyList<string> AllowedEngineVersions,
    string? SharedOutputPolicyId,
    IReadOnlyList<WorkerProjectPathDto> WorkerPaths);

/// <summary>
/// Per-worker path mapping DTO.
/// </summary>
public sealed record WorkerProjectPathDto(string Id, string ProjectId, string WorkerId, string EnginePath, string ProjectPath, string? LogDirectory);

/// <summary>
/// Render profile DTO used by controller APIs.
/// </summary>
public sealed record RenderProfileDto(
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
/// Render job DTO used by controller APIs.
/// </summary>
public sealed record RenderJobDto(
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
    bool CancellationRequested);

/// <summary>
/// Job attempt DTO used by controller APIs.
/// </summary>
public sealed record JobAttemptDto(
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
/// Append-only job event DTO used by controller APIs.
/// </summary>
public sealed record JobEventDto(
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
/// Controller setting DTO. Values are JSON strings so callers keep type ownership.
/// </summary>
public sealed record SettingDto(string Key, string ValueJson, DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Request to create a queued render job with controller-owned defaults.
/// </summary>
public sealed record CreateRenderJobRequest(
    string ProjectId,
    string RenderProfileId,
    string Name,
    int Priority = 0,
    string? OutputDirectory = null,
    int? FrameStart = null,
    int? FrameEnd = null,
    int? ChunkSizeFrames = null);

/// <summary>
/// Request to preview deterministic frame chunks without creating executable chunk jobs.
/// </summary>
public sealed record ChunkPreviewRequest(string ProjectId, string RenderProfileId, int FrameStart, int FrameEnd, int ChunkSizeFrames, string? OutputDirectory = null);

/// <summary>
/// One previewed frame chunk with an output naming hint.
/// </summary>
public sealed record ChunkPreviewItemDto(int ChunkIndex, int FrameStart, int FrameEnd, int TotalChunks, string OutputNameHint);

/// <summary>
/// Dry-run response for chunk planning. This does not enable distributed chunk execution.
/// </summary>
public sealed record ChunkPreviewResponseDto(string ProjectId, string RenderProfileId, string? OutputDirectory, IReadOnlyList<ChunkPreviewItemDto> Chunks);
/// <summary>
/// Lease returned when a worker successfully reserves a job.
/// </summary>
public sealed record JobLeaseDto(
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

/// <summary>
/// Response from the worker pull scheduler.
/// </summary>
public sealed record JobAssignmentDto(
    bool Assigned,
    RenderJobDto? Job,
    JobAttemptDto? Attempt,
    JobLeaseDto? Lease,
    string Message);

/// <summary>
/// Request to extend an active job lease.
/// </summary>
public sealed record JobLeaseRenewalRequest(string LeaseId, string WorkerId, int? LeaseSeconds = null);

/// <summary>
/// Request to mark a leased job as running.
/// </summary>
public sealed record JobStartRequest(string LeaseId, string WorkerId, string? Message = null);

/// <summary>
/// Request to mark a leased job as complete.
/// </summary>
public sealed record JobCompletionRequest(string LeaseId, string WorkerId, int? ExitCode = 0, string? OutputDirectory = null, string? Message = null, RenderArtifactSummaryDto? ArtifactSummary = null);

/// <summary>
/// Request to mark a leased job as failed.
/// </summary>
public sealed record JobFailureRequest(string LeaseId, string WorkerId, FailureCategory FailureCategory, string Error, int? ExitCode = null, bool RetryEligible = true);

/// <summary>
/// Request to change whether a worker may receive new jobs.
/// </summary>
public sealed record WorkerSchedulingModeRequest(WorkerSchedulingMode Mode);

/// <summary>
/// Compact controller summary used by the dashboard snapshot.
/// </summary>
public sealed record DashboardSummaryDto(
    bool Ok,
    string Service,
    string Version,
    string Runtime,
    int Workers,
    int Projects,
    int RenderProfiles,
    int Jobs,
    IReadOnlyDictionary<string, int> JobStates);

/// <summary>
/// Worker row optimized for the dashboard.
/// </summary>
public sealed record DashboardWorkerDto(
    string Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? ServiceUrl,
    WorkerStatus Status,
    string EffectiveStatus,
    string Approval,
    WorkerSchedulingMode SchedulingMode,
    string? Stage,
    string? CurrentJobId,
    string? AgentVersion,
    WorkerCapabilitiesDto Capabilities,
    string? LastError,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastHeartbeatUtc,
    int SecondsSinceHeartbeat);

/// <summary>
/// Job row optimized for the dashboard.
/// </summary>
public sealed record DashboardJobDto(RenderJobDto Job, int AttemptCount);

/// <summary>
/// Single request payload for the dashboard frontend.
/// </summary>
public sealed record DashboardSnapshotDto(
    DateTimeOffset GeneratedAtUtc,
    DashboardSummaryDto Summary,
    IReadOnlyList<DashboardWorkerDto> Workers,
    IReadOnlyList<ProjectProfileDto> Projects,
    IReadOnlyList<RenderProfileDto> RenderProfiles,
    IReadOnlyList<DashboardJobDto> Jobs);

/// <summary>
/// Shared mapping helpers between DTO contracts and domain records.
/// </summary>
public static class RenderFarmContractMapper
{
    public static Worker ToWorker(this WorkerHeartbeatDto heartbeat, DateTimeOffset receivedAtUtc) => new(
        Id: heartbeat.WorkerId,
        Name: string.IsNullOrWhiteSpace(heartbeat.Name) ? heartbeat.WorkerId : heartbeat.Name,
        Hostname: heartbeat.Hostname,
        IpAddress: heartbeat.Ip,
        ServiceUrl: heartbeat.ServiceUrl,
        Status: Enum.TryParse<WorkerStatus>(heartbeat.Status, true, out var status) ? status : WorkerStatus.Unknown,
        Stage: heartbeat.Stage,
        CurrentJobId: heartbeat.CurrentJobId,
        AgentVersion: heartbeat.AgentVersion,
        Capabilities: heartbeat.Capabilities.ToDomain(),
        LastError: heartbeat.LastError,
        RegisteredAtUtc: receivedAtUtc,
        LastHeartbeatUtc: receivedAtUtc);

    public static WorkerDto ToDto(this Worker worker) => new(
        worker.Id,
        worker.Name,
        worker.Hostname,
        worker.IpAddress,
        worker.ServiceUrl,
        worker.Status,
        worker.Stage,
        worker.CurrentJobId,
        worker.AgentVersion,
        worker.Capabilities.ToDto(),
        worker.LastError,
        worker.RegisteredAtUtc,
        worker.LastHeartbeatUtc);

    public static WorkerCapabilities ToDomain(this WorkerCapabilitiesDto dto) => new(
        dto.CpuCores,
        dto.RamGb,
        dto.GpuName,
        dto.VramGb,
        dto.FreeDiskGb,
        dto.UnrealInstallations.Select(x => new UnrealEngineInstallation(x.Version, x.RootPath, x.ExecutablePath, x.Exists)).ToArray(),
        dto.ProjectPaths.Select(x => new ProjectPathStatus(x.Path, x.Exists)).ToArray(),
        dto.SharedOutputRoots.Select(x => new SharedOutputStatus(x.Path, x.Exists, x.Writable, x.FreeDiskGb, x.Message)).ToArray());

    public static WorkerCapabilitiesDto ToDto(this WorkerCapabilities capabilities) => new(
        capabilities.CpuCores,
        capabilities.RamGb,
        capabilities.GpuName,
        capabilities.VramGb,
        capabilities.FreeDiskGb,
        capabilities.UnrealInstallations.Select(x => new UnrealEngineInstallationDto(x.Version, x.RootPath, x.ExecutablePath, x.Exists)).ToArray(),
        capabilities.ProjectPaths.Select(x => new ProjectPathStatusDto(x.Path, x.Exists)).ToArray(),
        capabilities.SharedOutputRoots.Select(x => new SharedOutputStatusDto(x.Path, x.Exists, x.Writable, x.FreeDiskGb, x.Message)).ToArray());

    public static ProjectProfile ToDomain(this ProjectProfileDto dto) => new(dto.Id, dto.DisplayName, dto.UProjectPath, dto.PreferredEngineVersion, dto.AllowedEngineVersions, dto.SharedOutputPolicyId, dto.WorkerPaths.Select(x => x.ToDomain()).ToArray());

    public static ProjectProfileDto ToDto(this ProjectProfile project) => new(project.Id, project.DisplayName, project.UProjectPath, project.PreferredEngineVersion, project.AllowedEngineVersions, project.SharedOutputPolicyId, project.WorkerPaths.Select(x => x.ToDto()).ToArray());

    public static WorkerProjectPath ToDomain(this WorkerProjectPathDto dto) => new(dto.Id, dto.ProjectId, dto.WorkerId, dto.EnginePath, dto.ProjectPath, dto.LogDirectory);

    public static WorkerProjectPathDto ToDto(this WorkerProjectPath path) => new(path.Id, path.ProjectId, path.WorkerId, path.EnginePath, path.ProjectPath, path.LogDirectory);

    public static RenderProfile ToDomain(this RenderProfileDto dto) => new(dto.Id, dto.ProjectId, dto.DisplayName, dto.Type, dto.AssetPath, dto.CommandTemplate, dto.DefaultOutputType, dto.SupportsChunking, dto.Settings);

    public static RenderProfileDto ToDto(this RenderProfile profile) => new(profile.Id, profile.ProjectId, profile.DisplayName, profile.Type, profile.AssetPath, profile.CommandTemplate, profile.DefaultOutputType, profile.SupportsChunking, profile.Settings);

    public static RenderJob ToDomain(this RenderJobDto dto) => new(dto.Id, dto.ProjectId, dto.RenderProfileId, dto.Name, dto.State, dto.Priority, dto.AssignedWorkerId, dto.FailureCategory, dto.Error, dto.OutputDirectory, dto.CreatedAtUtc, dto.UpdatedAtUtc, dto.QueuedAtUtc, dto.StartedAtUtc, dto.FinishedAtUtc, dto.CancellationRequested);

    public static RenderJobDto ToDto(this RenderJob job) => new(job.Id, job.ProjectId, job.RenderProfileId, job.Name, job.State, job.Priority, job.AssignedWorkerId, job.FailureCategory, job.Error, job.OutputDirectory, job.CreatedAtUtc, job.UpdatedAtUtc, job.QueuedAtUtc, job.StartedAtUtc, job.FinishedAtUtc, job.CancellationRequested);

    public static JobAttempt ToDomain(this JobAttemptDto dto) => new(dto.Id, dto.JobId, dto.AttemptNumber, dto.WorkerId, dto.State, dto.FailureCategory, dto.Error, dto.CommandLine, dto.LogFilePath, dto.StartedAtUtc, dto.FinishedAtUtc, dto.ExitCode);

    public static JobAttemptDto ToDto(this JobAttempt attempt) => new(attempt.Id, attempt.JobId, attempt.AttemptNumber, attempt.WorkerId, attempt.State, attempt.FailureCategory, attempt.Error, attempt.CommandLine, attempt.LogFilePath, attempt.StartedAtUtc, attempt.FinishedAtUtc, attempt.ExitCode);

    public static JobEvent ToDomain(this JobEventDto dto) => new(dto.Id, dto.JobId, dto.JobAttemptId, dto.WorkerId, dto.State, dto.FailureCategory, dto.Message, dto.CreatedAtUtc, dto.DataJson);

    public static JobEventDto ToDto(this JobEvent evt) => new(evt.Id, evt.JobId, evt.JobAttemptId, evt.WorkerId, evt.State, evt.FailureCategory, evt.Message, evt.CreatedAtUtc, evt.DataJson);

    public static JobLease ToDomain(this JobLeaseDto dto) => new(dto.Id, dto.JobId, dto.JobAttemptId, dto.WorkerId, dto.AcquiredAtUtc, dto.ExpiresAtUtc, dto.RenewedAtUtc, dto.ReleasedAtUtc, dto.ReleaseReason, dto.IsActive);

    public static JobLeaseDto ToDto(this JobLease lease) => new(lease.Id, lease.JobId, lease.JobAttemptId, lease.WorkerId, lease.AcquiredAtUtc, lease.ExpiresAtUtc, lease.RenewedAtUtc, lease.ReleasedAtUtc, lease.ReleaseReason, lease.IsActive);
}