using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RenderFarm.Domain;
using RenderFarm.Shared;

namespace RenderFarm.Worker.Agent;

/// <summary>
/// Pulls leased work from the C# controller and runs Unreal through the controlled C# launcher.
/// </summary>
public sealed class WorkerJobService(
    IHttpClientFactory httpClientFactory,
    Microsoft.Extensions.Options.IOptions<WorkerAgentOptions> options,
    IWorkerIdentityProvider identity,
    IControllerEndpointProvider controllerEndpoints,
    IWorkerExecutionStateStore executionState,
    IUnrealEngineLocator unrealEngineLocator,
    IUnrealProcessLauncher unrealProcessLauncher,
    IUnrealCommandBuilder unrealCommandBuilder,
    ISharedOutputValidator sharedOutputValidator,
    IWorkerPreflightService preflightService,
    ILogger<WorkerJobService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _jobLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileLocalStateAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(2, options.Value.JobPollingSeconds)));
        do
        {
            await TryRunOneJobAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TryRunOneJobAsync(CancellationToken cancellationToken)
    {
        if (!await _jobLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            WorkerHttp.ApplyControllerToken(client, options.Value.ApiToken);
            var controller = await controllerEndpoints.GetControllerBaseUriAsync(cancellationToken);
            var workerId = identity.GetWorkerId();
            var endpoint = new Uri(controller, $"api/workers/{Uri.EscapeDataString(workerId)}/request-job");
            logger.LogDebug("Worker {WorkerId} polling controller {ControllerUrl} for work at {Endpoint}; interval {PollingSeconds}s", workerId, controller, endpoint, Math.Max(2, options.Value.JobPollingSeconds));
            using var assignmentResponse = await client.PostAsync(endpoint, null, cancellationToken);
            if (assignmentResponse.StatusCode == HttpStatusCode.NoContent)
            {
                logger.LogDebug("Worker {WorkerId} poll returned no job from {Endpoint}.", workerId, endpoint);
                return;
            }

            if (!assignmentResponse.IsSuccessStatusCode)
            {
                var body = await assignmentResponse.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Worker {WorkerId} poll failed with HTTP {StatusCode} from {Endpoint}: {Body}", workerId, (int)assignmentResponse.StatusCode, endpoint, TruncateForLog(body));
                assignmentResponse.EnsureSuccessStatusCode();
            }

            var assignment = await assignmentResponse.Content.ReadFromJsonAsync<JobAssignmentDto>(RenderFarmJson.SerializerOptions, cancellationToken);
            if (assignment?.Assigned != true || assignment.Job is null || assignment.Attempt is null || assignment.Lease is null)
            {
                logger.LogInformation("Worker {WorkerId} poll returned no assignment: {Message}", workerId, assignment?.Message ?? "empty response");
                return;
            }

            logger.LogInformation("Worker {WorkerId} leased job {JobId}, attempt {AttemptId}, lease {LeaseId}", workerId, assignment.Job.Id, assignment.Attempt.Id, assignment.Lease.Id);
            await RunAssignedJobAsync(client, controller, assignment, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Worker job polling failed");
        }
        finally
        {
            _jobLock.Release();
        }
    }

    private async Task RunAssignedJobAsync(HttpClient client, Uri controller, JobAssignmentDto assignment, CancellationToken cancellationToken)
    {
        var job = assignment.Job!;
        var attempt = assignment.Attempt!;
        var lease = assignment.Lease!;
        var workerId = identity.GetWorkerId();

        await executionState.WriteAsync(new WorkerExecutionState(workerId, job.Id, attempt.Id, lease.Id, DateTimeOffset.UtcNow, "Assigned by controller"), cancellationToken);
        logger.LogInformation("Worker {WorkerId} accepted job {JobId}, attempt {AttemptId}, lease {LeaseId}", workerId, job.Id, attempt.Id, lease.Id);

        try
        {
            var request = assignment.Execution is not null
                ? BuildRenderRequest(job, attempt, assignment.Execution)
                : await BuildLegacyRenderRequestAsync(client, controller, job, attempt, cancellationToken);
            var preflight = await preflightService.RunAsync(request, controller, cancellationToken);
            await PostPreflightEventAsync(client, controller, job.Id, attempt.Id, workerId, preflight, cancellationToken);
            if (preflight.Status == PreflightOverallStatus.Blocked)
            {
                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, FailureCategory.CommandValidationFailed, preflight.Summary, null, RetryEligible: false, Preflight: preflight), cancellationToken);
                await executionState.ClearAsync(CancellationToken.None);
                return;
            }

            await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/start"), new JobStartRequest(lease.Id, workerId, preflight.Status == PreflightOverallStatus.Warning ? $"Worker started Unreal render after preflight warnings: {preflight.Summary}" : "Worker started Unreal render after preflight passed"), cancellationToken);
            var outputValidation = await sharedOutputValidator.ValidateAsync(new SharedOutputValidationRequest(request.OutputDirectory, ".", true), cancellationToken);
            if (!outputValidation.Ok)
            {
                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, outputValidation.FailureCategory, outputValidation.Message, null, RetryEligible: false), cancellationToken);
                await executionState.ClearAsync(CancellationToken.None);
                return;
            }

            var preparation = await PrepareRenderAsync(job, attempt, request, cancellationToken);
            if (!preparation.Ok)
            {
                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, FailureCategory.CommandValidationFailed, preparation.Error ?? "Render preparation failed.", null, RetryEligible: false), cancellationToken);
                await executionState.ClearAsync(CancellationToken.None);
                return;
            }

            var commandPreview = unrealCommandBuilder.Build(request, job.Id, attempt.AttemptNumber);
            await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/attempts"), attempt with { CommandLine = commandPreview.CommandLine, LogFilePath = commandPreview.LogFilePath }, cancellationToken);

            logger.LogInformation("Worker {WorkerId} prepared job {JobId}, attempt {AttemptId}, lease {LeaseId}, request {RequestJsonPath}", workerId, job.Id, attempt.Id, lease.Id, preparation.RequestJsonPath);
            logger.LogInformation("Worker {WorkerId} launching job {JobId}, attempt {AttemptId}, lease {LeaseId}, process {CommandLine}, output {OutputDirectory}", workerId, job.Id, attempt.Id, lease.Id, commandPreview.CommandLine, request.OutputDirectory);

            using var renderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var cancellationRequestedByController = false;
            var renewalTask = RenewLeaseUntilCancelledAsync(client, controller, job.Id, lease.Id, workerId, renderCts.Token);
            var cancellationWatchTask = WatchJobCancellationUntilCancelledAsync(client, controller, job.Id, renderCts, () => cancellationRequestedByController = true, renderCts.Token);
            ProcessLaunchResult result;
            try
            {
                result = await unrealProcessLauncher.LaunchRenderAsync(request, job.Id, attempt.AttemptNumber, renderCts.Token);
            }
            catch (OperationCanceledException) when (cancellationRequestedByController && !cancellationToken.IsCancellationRequested)
            {
                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, FailureCategory.CancelledByUser, "Render cancelled by operator request.", null, RetryEligible: false), CancellationToken.None);
                await executionState.ClearAsync(CancellationToken.None);
                return;
            }
            finally
            {
                await renderCts.CancelAsync();
                await AwaitQuietlyAsync(renewalTask);
                await AwaitQuietlyAsync(cancellationWatchTask);
            }

            logger.LogInformation("Worker {WorkerId} finished job {JobId}, attempt {AttemptId}, lease {LeaseId}, exit code {ExitCode}, category {FailureCategory}", workerId, job.Id, attempt.Id, lease.Id, result.ExitCode, result.FailureCategory);

            if (cancellationRequestedByController)
            {
                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, FailureCategory.CancelledByUser, "Render cancelled by operator request.", result.ExitCode, RetryEligible: false), CancellationToken.None);
                await executionState.ClearAsync(CancellationToken.None);
                return;
            }

            if (result.FailureCategory == FailureCategory.None)
            {
                var successLog = CombineLogText(result.StandardOutput, result.StandardError);
                var successClassification = RenderLogClassifier.Classify(successLog);
                var blockingDiagnostic = successClassification.Diagnostics.FirstOrDefault(diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase));
                if (blockingDiagnostic is not null)
                {
                    await PostRenderLogDiagnosticsAsync(client, controller, job.Id, attempt.Id, workerId, result.FailureCategory, successClassification, cancellationToken);
                    await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, FailureCategory.RenderProcessFailed, ImproveUnrealFailureMessage(successLog, successClassification), result.ExitCode, RetryEligible: false), cancellationToken);
                    await executionState.ClearAsync(CancellationToken.None);
                    return;
                }

                var verification = VerifyRenderOutput(request, successLog);
                if (!verification.Ok)
                {
                    await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, verification.FailureCategory, verification.Error ?? "Render completed but expected output was not found.", result.ExitCode, RetryEligible: false, OutputValidation: verification.OutputValidation), cancellationToken);
                    await executionState.ClearAsync(CancellationToken.None);
                    return;
                }



                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/complete"), new JobCompletionRequest(lease.Id, workerId, result.ExitCode, verification.ArtifactSummary?.OutputDirectory ?? request.OutputDirectory, verification.Warning ?? "Unreal render completed and output was verified", verification.ArtifactSummary, verification.OutputValidation), cancellationToken);
                await executionState.ClearAsync(CancellationToken.None);
                return;
            }

            var rawLog = CombineLogText(result.StandardOutput, result.StandardError, $"Unreal render failed with exit code {result.ExitCode}");
            var classification = RenderLogClassifier.Classify(rawLog);
            await PostRenderLogDiagnosticsAsync(client, controller, job.Id, attempt.Id, workerId, result.FailureCategory, classification, cancellationToken);
            var error = ImproveUnrealFailureMessage(rawLog, classification);
            await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, result.FailureCategory, error, result.ExitCode), cancellationToken);
            await executionState.ClearAsync(CancellationToken.None);
        }
        catch (RenderExecutionValidationException ex)
        {
            logger.LogError(ex, "Assigned job {JobId}, attempt {AttemptId}, lease {LeaseId} had an invalid execution payload", job.Id, attempt.Id, lease.Id);
            try
            {
                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, ex.FailureCategory, ex.Message, null, RetryEligible: false), CancellationToken.None);
                await executionState.ClearAsync(CancellationToken.None);
            }
            catch (Exception reportEx)
            {
                logger.LogError(reportEx, "Could not report invalid execution payload for job {JobId}, attempt {AttemptId}, lease {LeaseId} to controller", job.Id, attempt.Id, lease.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var category = ex is InvalidOperationException ? FailureCategory.CommandValidationFailed : FailureCategory.UnrealLaunchFailed;
            logger.LogError(ex, "Assigned job {JobId}, attempt {AttemptId}, lease {LeaseId} failed before or during launch", job.Id, attempt.Id, lease.Id);
            try
            {
                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, category, ImproveUnrealFailureMessage(ex.Message, RenderLogClassifier.Classify(ex.Message)), null, RetryEligible: false), CancellationToken.None);
                await executionState.ClearAsync(CancellationToken.None);
            }
            catch (Exception reportEx)
            {
                logger.LogError(reportEx, "Could not report failed job {JobId}, attempt {AttemptId}, lease {LeaseId} to controller", job.Id, attempt.Id, lease.Id);
            }
        }
    }

    private async Task ReconcileLocalStateAsync(CancellationToken cancellationToken)
    {
        var localState = await executionState.ReadAsync(cancellationToken);
        if (localState is null)
        {
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            WorkerHttp.ApplyControllerToken(client, options.Value.ApiToken);
            var controller = await controllerEndpoints.GetControllerBaseUriAsync(cancellationToken);
            using var response = await client.GetAsync(new Uri(controller, $"api/jobs/{Uri.EscapeDataString(localState.JobId)}"), cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogWarning("Worker {WorkerId} found local state for missing job {JobId}; clearing marker", localState.WorkerId, localState.JobId);
                await executionState.ClearAsync(cancellationToken);
                return;
            }

            response.EnsureSuccessStatusCode();
            var job = await response.Content.ReadFromJsonAsync<RenderJobDto>(RenderFarmJson.SerializerOptions, cancellationToken);
            if (job is null || JobStateMachine.IsTerminal(job.State))
            {
                logger.LogInformation("Worker {WorkerId} found local state for terminal job {JobId}; clearing marker", localState.WorkerId, localState.JobId);
                await executionState.ClearAsync(cancellationToken);
                return;
            }

            logger.LogWarning("Worker {WorkerId} recovered local state for job {JobId}, attempt {AttemptId}, lease {LeaseId}; not relaunching. Controller state is {JobState} and lease expiry will decide recovery.", localState.WorkerId, localState.JobId, localState.AttemptId, localState.LeaseId, job.State);
            await executionState.ClearAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not reconcile local worker state at startup; worker will not relaunch the recorded job blindly");
        }
    }

    private async Task RenewLeaseUntilCancelledAsync(HttpClient client, Uri controller, string jobId, string leaseId, string workerId, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.LeaseRenewalSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var leaseSeconds = Math.Max(30, options.Value.LeaseRenewalSeconds * 3);
                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(jobId)}/renew-lease"), new JobLeaseRenewalRequest(leaseId, workerId, leaseSeconds), cancellationToken);
                logger.LogDebug("Worker {WorkerId} renewed lease {LeaseId} for job {JobId}", workerId, leaseId, jobId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Worker {WorkerId} could not renew lease {LeaseId} for job {JobId}", workerId, leaseId, jobId);
            }
        }
    }

    private static async Task WatchJobCancellationUntilCancelledAsync(HttpClient client, Uri controller, string jobId, CancellationTokenSource renderCts, Action onCancellationRequested, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var job = await GetRequiredAsync<RenderJobDto>(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(jobId)}"), cancellationToken);
                if (job.CancellationRequested || job.State is JobState.CancelRequested or JobState.Cancelling or JobState.Cancelled)
                {
                    onCancellationRequested();
                    await renderCts.CancelAsync();
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missed cancellation poll should not fail the render; lease renewal still governs recovery.
            }
        }
    }

    private static async Task<RenderPreparationResultDto> PrepareRenderAsync(RenderJobDto job, JobAttemptDto attempt, UnrealRenderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(request.LogDirectory);
            var prepared = new PreparedRenderRequestDto(
                request.ProjectPath,
                GetSetting(request.Profile, "map") ?? GetSetting(request.Profile, "mapName") ?? GetSetting(request.Profile, "level") ?? GetSetting(request.Profile, "levelName"),
                GetSetting(request.Profile, "sequence") ?? GetSetting(request.Profile, "levelSequence"),
                request.Profile.AssetPath ?? GetSetting(request.Profile, "moviePipelineConfig") ?? GetSetting(request.Profile, "mrqConfig") ?? GetSetting(request.Profile, "queue"),
                request.OutputDirectory,
                request.Profile.DefaultOutputType,
                TryGetInt(GetSetting(request.Profile, "frameStart")),
                TryGetInt(GetSetting(request.Profile, "frameEnd")),
                TryGetInt(GetSetting(request.Profile, "chunkIndex")),
                job.Id,
                attempt.Id);
            var path = Path.Combine(request.LogDirectory, $"{SanitizeFileName(job.Id)}_attempt_{attempt.AttemptNumber:00}_render_request.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(prepared, RenderFarmJson.SerializerOptions), cancellationToken);
            return new RenderPreparationResultDto(true, path, prepared, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallback = new PreparedRenderRequestDto(request.ProjectPath, null, null, request.Profile.AssetPath, request.OutputDirectory, request.Profile.DefaultOutputType, null, null, null, job.Id, attempt.Id);
            return new RenderPreparationResultDto(false, string.Empty, fallback, ex.Message);
        }
    }

    private static RenderOutputVerificationResult VerifyRenderOutput(UnrealRenderRequest request, string logText)
    {
        var summary = RenderOutputValidator.Validate(request, logText);
        if (summary.Status == OutputValidationStatus.Passed && summary.ArtifactSummary is not null)
        {
            return RenderOutputVerificationResult.Success(summary.ArtifactSummary, summary.Message, summary);
        }

        var message = string.IsNullOrWhiteSpace(summary.SuggestedFix)
            ? summary.Message
            : $"{summary.Message} Suggested fix: {summary.SuggestedFix}";
        var category = summary.Mode == OutputValidationMode.StrictFrameSequence
            ? FailureCategory.RenderOutputIncomplete
            : FailureCategory.RenderOutputMissing;
        return RenderOutputVerificationResult.Fail(category, message, summary);
    }

    private static FileInfo[] ScanOutputFiles(string directory, ISet<string> supportedExtensions)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<FileInfo>();
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => supportedExtensions.Contains(Path.GetExtension(path).TrimStart('.')))
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists && file.Length > 0)
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<FileInfo>();
        }
    }

    private static (string Directory, FileInfo[] Files)? FindFallbackOutput(UnrealRenderRequest request, ISet<string> supportedExtensions)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(request.ProjectPath));
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(projectDirectory, "Saved", "MovieRenders"),
            Path.Combine(projectDirectory, "Saved", "Screenshots"),
            Path.Combine(projectDirectory, "Saved", "VideoCaptures")
        };

        foreach (var candidate in candidates)
        {
            var files = ScanOutputFiles(candidate, supportedExtensions);
            if (files.Length > 0)
            {
                return (candidate, files);
            }
        }

        return null;
    }

    private static RenderArtifactSummaryDto BuildArtifactSummary(string outputDirectory, IReadOnlyList<FileInfo> files)
    {
        var samples = files
            .Take(8)
            .Select(file => Path.GetRelativePath(outputDirectory, file.FullName))
            .ToArray();
        var extensions = files
            .Select(file => Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant())
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RenderArtifactSummaryDto(outputDirectory, files.Count, files.Sum(file => file.Length), samples, extensions);
    }

    private static bool LogIndicatesMovieRenderCompleted(string logText)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return false;
        }

        return logText.Contains("Movie Render", StringComparison.OrdinalIgnoreCase) &&
               (logText.Contains("complete", StringComparison.OrdinalIgnoreCase) || logText.Contains("finished", StringComparison.OrdinalIgnoreCase) || logText.Contains("success", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetSupportedOutputExtensions()
    {
        return ["mov", "mp4", "avi", "mkv", "mxf", "png", "jpg", "jpeg", "exr", "tif", "tiff", "bmp", "wav", "aif", "aiff"];
    }

    private static string ImproveUnrealFailureMessage(string error, RenderLogClassificationDto classification)
    {
        var primary = classification.Diagnostics.FirstOrDefault(diagnostic => !string.Equals(diagnostic.Severity, "info", StringComparison.OrdinalIgnoreCase));
        if (primary is not null)
        {
            return primary.Fix is null ? primary.Message : $"{primary.Message} Fix: {primary.Fix}";
        }

        return string.IsNullOrWhiteSpace(error) ? "Unreal render failed without a detailed log message." : error;
    }

    private static string CombineLogText(params string?[] parts) =>
        string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();

    private static string TruncateForLog(string? value, int maxCharacters = 2000)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxCharacters)
        {
            return value ?? string.Empty;
        }

        return value[..maxCharacters] + "...";
    }

    private static async Task PostPreflightEventAsync(HttpClient client, Uri controller, string jobId, string attemptId, string workerId, PreflightResultDto preflight, CancellationToken cancellationToken)
    {
        var evt = new JobEventDto(
            Guid.NewGuid().ToString("N"),
            jobId,
            attemptId,
            workerId,
            JobState.ValidatingWorker,
            preflight.Status == PreflightOverallStatus.Blocked ? FailureCategory.CommandValidationFailed : FailureCategory.None,
            $"Worker preflight: {preflight.Summary}",
            DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(new { preflight }, RenderFarmJson.SerializerOptions));
        await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(jobId)}/events"), evt, cancellationToken);
    }
    private static async Task PostRenderLogDiagnosticsAsync(HttpClient client, Uri controller, string jobId, string attemptId, string workerId, FailureCategory category, RenderLogClassificationDto classification, CancellationToken cancellationToken)
    {
        if (classification.Diagnostics.Count == 0 && classification.LoadErrors.Count == 0 && string.IsNullOrWhiteSpace(classification.RawExcerpt))
        {
            return;
        }

        var evt = new JobEventDto(
            Guid.NewGuid().ToString("N"),
            jobId,
            attemptId,
            workerId,
            null,
            category,
            "Render log diagnostics",
            DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(classification, RenderFarmJson.SerializerOptions));
        await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(jobId)}/events"), evt, cancellationToken);
    }
    private sealed class RenderExecutionValidationException(FailureCategory failureCategory, string message) : Exception(message)
    {
        public FailureCategory FailureCategory { get; } = failureCategory;
    }
    private sealed record RenderOutputVerificationResult(bool Ok, FailureCategory FailureCategory, string? Error, RenderArtifactSummaryDto? ArtifactSummary, string? Warning = null, OutputValidationSummaryDto? OutputValidation = null)
    {
        public static RenderOutputVerificationResult Success(RenderArtifactSummaryDto summary, string? warning = null, OutputValidationSummaryDto? outputValidation = null) => new(true, FailureCategory.None, null, summary, warning, outputValidation);
        public static RenderOutputVerificationResult Fail(FailureCategory category, string error, OutputValidationSummaryDto? outputValidation = null) => new(false, category, error, null, null, outputValidation);
    }

    private async Task<UnrealRenderRequest> BuildLegacyRenderRequestAsync(HttpClient client, Uri controller, RenderJobDto job, JobAttemptDto attempt, CancellationToken cancellationToken)
    {
        var project = await GetRequiredAsync<ProjectProfileDto>(client, new Uri(controller, $"api/projects/{Uri.EscapeDataString(job.ProjectId)}"), cancellationToken);
        var profile = await GetRequiredAsync<RenderProfileDto>(client, new Uri(controller, $"api/render-profiles/{Uri.EscapeDataString(job.RenderProfileId)}"), cancellationToken);
        return BuildRenderRequest(job, attempt, project, profile);
    }

    private UnrealRenderRequest BuildRenderRequest(RenderJobDto job, JobAttemptDto attempt, RenderExecutionDto execution)
    {
        if (string.IsNullOrWhiteSpace(execution.UnrealExecutablePath))
        {
            throw new RenderExecutionValidationException(FailureCategory.UnrealExecutableMissing, "Controller assignment did not include an Unreal executable path.");
        }

        if (string.IsNullOrWhiteSpace(execution.ProjectPath))
        {
            throw new RenderExecutionValidationException(FailureCategory.ProjectPathMissing, "Controller assignment did not include a project .uproject path.");
        }

        if (string.IsNullOrWhiteSpace(execution.OutputDirectory))
        {
            throw new RenderExecutionValidationException(FailureCategory.SharedOutputUnreachable, "Controller assignment did not include an output directory.");
        }

        if (string.IsNullOrWhiteSpace(execution.LogDirectory))
        {
            throw new RenderExecutionValidationException(FailureCategory.CommandValidationFailed, "Controller assignment did not include a log directory.");
        }

        var unrealExecutablePath = Environment.ExpandEnvironmentVariables(execution.UnrealExecutablePath.Trim().Trim('"'));
        var projectPath = Environment.ExpandEnvironmentVariables(execution.ProjectPath.Trim().Trim('"'));
        var outputDirectory = Environment.ExpandEnvironmentVariables(execution.OutputDirectory.Trim().Trim('"'));
        var logDirectory = Environment.ExpandEnvironmentVariables(execution.LogDirectory.Trim().Trim('"'));

        if (!File.Exists(unrealExecutablePath))
        {
            throw new RenderExecutionValidationException(FailureCategory.UnrealExecutableMissing, $"Controller assignment Unreal executable was not found on this worker: {unrealExecutablePath}");
        }

        if (!File.Exists(projectPath) || !projectPath.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase))
        {
            throw new RenderExecutionValidationException(FailureCategory.UProjectMissing, $"Controller assignment project .uproject was not found on this worker: {projectPath}");
        }

        var timeoutSeconds = execution.TimeoutSeconds is > 0 ? execution.TimeoutSeconds.Value : options.Value.RenderTimeoutSeconds;
        var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : (TimeSpan?)null;
        return new UnrealRenderRequest(
            unrealExecutablePath,
            projectPath,
            execution.RenderProfile.ToDomain(),
            outputDirectory,
            logDirectory,
            timeout,
            execution.Variables);
    }
    private UnrealRenderRequest BuildRenderRequest(RenderJobDto job, JobAttemptDto attempt, ProjectProfileDto project, RenderProfileDto profile)
    {
        var workerId = identity.GetWorkerId();
        var workerPath = project.WorkerPaths.FirstOrDefault(x => string.Equals(x.WorkerId, workerId, StringComparison.OrdinalIgnoreCase));
        var projectPath = FirstNonEmpty(workerPath?.ProjectPath, project.UProjectPath, options.Value.ProjectPaths.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidOperationException("No project path is configured for this worker/job.");
        }

        var executable = ResolveUnrealExecutable(project, workerPath);
        var outputRoot = FirstNonEmpty(job.OutputDirectory, options.Value.SharedOutputRoots.FirstOrDefault(), Path.Combine(AppContext.BaseDirectory, "renders"));
        var outputDirectory = Path.GetFullPath(job.OutputDirectory is null ? Path.Combine(outputRoot, job.Id) : outputRoot);
        var logRoot = FirstNonEmpty(options.Value.AttemptLogRoot, workerPath?.LogDirectory, Path.Combine(outputDirectory, "logs"));
        var timeout = options.Value.RenderTimeoutSeconds > 0 ? TimeSpan.FromSeconds(options.Value.RenderTimeoutSeconds) : (TimeSpan?)null;

        return new UnrealRenderRequest(executable, projectPath, profile.ToDomain(), outputDirectory, logRoot, timeout);
    }

    private string ResolveUnrealExecutable(ProjectProfileDto project, WorkerProjectPathDto? workerPath)
    {
        if (!string.IsNullOrWhiteSpace(workerPath?.EnginePath))
        {
            var mappedInstalls = unrealEngineLocator.FindInstallations([workerPath.EnginePath]);
            if (ResolvePreferredInstall(project, mappedInstalls) is { } mappedInstall)
            {
                return mappedInstall.ExecutablePath;
            }
        }

        var installs = unrealEngineLocator.FindInstallations(options.Value.UnrealSearchRoots);
        var preferred = ResolvePreferredInstall(project, installs);
        if (preferred is null)
        {
            throw new FileNotFoundException("No matching UnrealEditor-Cmd.exe was found. Set a worker engine path in the dashboard, pass -UnrealSearchRoots to start_worker.ps1, or set RenderFarm__UnrealSearchRoots__0 to the UE root/executable.");
        }

        return preferred.ExecutablePath;
    }

    private UnrealEngineInstallation? ResolvePreferredInstall(ProjectProfileDto project, IReadOnlyList<UnrealEngineInstallation> installs) =>
        unrealEngineLocator.Resolve(project.PreferredEngineVersion, installs)
        ?? project.AllowedEngineVersions.Select(version => unrealEngineLocator.Resolve(version, installs)).FirstOrDefault(x => x is not null)
        ?? unrealEngineLocator.Resolve(null, installs);

    private static async Task<T> GetRequiredAsync<T>(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RenderFarmJson.SerializerOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Controller returned an empty response for {uri}.");
    }

    private static async Task PostRequiredAsync<T>(HttpClient client, Uri uri, T payload, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(uri, payload, RenderFarmJson.SerializerOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string? GetSetting(RenderProfile profile, string key) =>
        profile.Settings.TryGetValue(key, out var exact)
            ? exact
            : profile.Settings.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static int? TryGetInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
}












