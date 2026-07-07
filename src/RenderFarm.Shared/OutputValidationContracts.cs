using RenderFarm.Domain;

namespace RenderFarm.Shared;

/// <summary>
/// Operator-facing output validation modes used after Unreal exits.
/// </summary>
public enum OutputValidationMode
{
    AnyRenderOutput,
    SingleVideo,
    ImageSequence,
    StrictFrameSequence
}

/// <summary>
/// Overall result for post-render output validation.
/// </summary>
public enum OutputValidationStatus
{
    Passed,
    Warning,
    Failed
}

/// <summary>
/// Concise persisted summary of render output validation.
/// </summary>
public sealed record OutputValidationSummaryDto(
    OutputValidationStatus Status,
    OutputValidationMode Mode,
    string OutputDirectory,
    IReadOnlyList<string> ExpectedExtensions,
    IReadOnlyList<string> DetectedExtensions,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> SampleFiles,
    string Message,
    string? SuggestedFix = null,
    int? FrameCount = null,
    IReadOnlyList<string>? MissingFrames = null,
    RenderArtifactSummaryDto? ArtifactSummary = null);

/// <summary>
/// Status for a single worker or job preflight check.
/// </summary>
public enum PreflightCheckStatus
{
    Pass,
    Warning,
    Fail,
    NotApplicable
}

/// <summary>
/// Overall readiness state for a preflight result.
/// </summary>
public enum PreflightOverallStatus
{
    Ready,
    Warning,
    Blocked
}

/// <summary>
/// One operator-readable preflight check result.
/// </summary>
public sealed record PreflightCheckDto(
    string Name,
    PreflightCheckStatus Status,
    string Message,
    string? SuggestedFix = null,
    string? Field = null);

/// <summary>
/// Collection of preflight checks for one worker/job attempt.
/// </summary>
public sealed record PreflightResultDto(
    PreflightOverallStatus Status,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<PreflightCheckDto> Checks,
    string Summary);
