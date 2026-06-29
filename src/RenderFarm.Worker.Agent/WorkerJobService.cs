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
            using var assignmentResponse = await client.PostAsync(new Uri(controller, $"api/workers/{Uri.EscapeDataString(workerId)}/request-job"), null, cancellationToken);
            if (assignmentResponse.StatusCode == HttpStatusCode.NoContent)
            {
                return;
            }

            assignmentResponse.EnsureSuccessStatusCode();
            var assignment = await assignmentResponse.Content.ReadFromJsonAsync<JobAssignmentDto>(RenderFarmJson.SerializerOptions, cancellationToken);
            if (assignment?.Assigned != true || assignment.Job is null || assignment.Attempt is null || assignment.Lease is null)
            {
                return;
            }

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
            await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/start"), new JobStartRequest(lease.Id, workerId, "Worker started Unreal render"), cancellationToken);
            var project = await GetRequiredAsync<ProjectProfileDto>(client, new Uri(controller, $"api/projects/{Uri.EscapeDataString(job.ProjectId)}"), cancellationToken);
            var profile = await GetRequiredAsync<RenderProfileDto>(client, new Uri(controller, $"api/render-profiles/{Uri.EscapeDataString(job.RenderProfileId)}"), cancellationToken);
            var request = BuildRenderRequest(job, attempt, project, profile);
            var preparation = await PrepareRenderAsync(job, attempt, request, cancellationToken);
            if (!preparation.Ok)
            {
                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, FailureCategory.CommandValidationFailed, preparation.Error ?? "Render preparation failed.", null, RetryEligible: false), cancellationToken);
                await executionState.ClearAsync(CancellationToken.None);
                return;
            }

            logger.LogInformation("Worker {WorkerId} prepared job {JobId}, attempt {AttemptId}, lease {LeaseId}, request {RequestJsonPath}", workerId, job.Id, attempt.Id, lease.Id, preparation.RequestJsonPath);
            logger.LogInformation("Worker {WorkerId} launching job {JobId}, attempt {AttemptId}, lease {LeaseId}, executable {Executable}, project {ProjectPath}, output {OutputDirectory}", workerId, job.Id, attempt.Id, lease.Id, request.UnrealExecutablePath, request.ProjectPath, request.OutputDirectory);

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
                var verification = VerifyRenderOutput(request);
                if (!verification.Ok)
                {
                    await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, verification.FailureCategory, verification.Error ?? "Render completed but expected output was not found.", result.ExitCode, RetryEligible: false), cancellationToken);
                    await executionState.ClearAsync(CancellationToken.None);
                    return;
                }

                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/complete"), new JobCompletionRequest(lease.Id, workerId, result.ExitCode, request.OutputDirectory, "Unreal render completed and output was verified", verification.ArtifactSummary), cancellationToken);
                await executionState.ClearAsync(CancellationToken.None);
                return;
            }

            var error = FirstNonEmpty(result.StandardError, result.StandardOutput, $"Unreal render failed with exit code {result.ExitCode}");
            await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, result.FailureCategory, error, result.ExitCode), cancellationToken);
            await executionState.ClearAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Assigned job {JobId}, attempt {AttemptId}, lease {LeaseId} failed before or during launch", job.Id, attempt.Id, lease.Id);
            try
            {
                await PostRequiredAsync(client, new Uri(controller, $"api/jobs/{Uri.EscapeDataString(job.Id)}/fail"), new JobFailureRequest(lease.Id, workerId, FailureCategory.UnrealLaunchFailed, ex.Message), CancellationToken.None);
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

    private static RenderOutputVerificationResult VerifyRenderOutput(UnrealRenderRequest request)
    {
        if (!Directory.Exists(request.OutputDirectory))
        {
            return RenderOutputVerificationResult.Fail(FailureCategory.RenderOutputMissing, $"Render output directory does not exist: {request.OutputDirectory}");
        }

        var expectedExtensions = GetExpectedOutputExtensions(request.Profile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(request.OutputDirectory, "*", SearchOption.AllDirectories)
            .Where(path => expectedExtensions.Contains(Path.GetExtension(path).TrimStart('.')))
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.Length > 0)
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            var expected = string.Join(", ", expectedExtensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return RenderOutputVerificationResult.Fail(FailureCategory.RenderOutputMissing, $"Render completed but no non-empty {expected} output files were found in {request.OutputDirectory}.");
        }

        var samples = files
            .Take(8)
            .Select(file => Path.GetRelativePath(request.OutputDirectory, file.FullName))
            .ToArray();
        var summary = new RenderArtifactSummaryDto(request.OutputDirectory, files.Length, files.Sum(file => file.Length), samples);
        return RenderOutputVerificationResult.Success(summary);
    }

    private static IEnumerable<string> GetExpectedOutputExtensions(RenderProfile profile)
    {
        var raw = FirstNonEmpty(profile.DefaultOutputType, GetSetting(profile, "outputType"), GetSetting(profile, "fileType"), GetSetting(profile, "fileFormat"));
        var tokens = raw.Split([',', ';', ' ', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim().TrimStart('.').ToLowerInvariant())
            .ToArray();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mp4", "mov", "avi", "mxf", "png", "jpg", "jpeg", "exr" };
        var resolved = tokens.Where(known.Contains).ToArray();
        return resolved.Length > 0 ? resolved : ["mp4", "mov", "avi", "mxf", "png", "jpg", "jpeg", "exr"];
    }

    private sealed record RenderOutputVerificationResult(bool Ok, FailureCategory FailureCategory, string? Error, RenderArtifactSummaryDto? ArtifactSummary)
    {
        public static RenderOutputVerificationResult Success(RenderArtifactSummaryDto summary) => new(true, FailureCategory.None, null, summary);
        public static RenderOutputVerificationResult Fail(FailureCategory category, string error) => new(false, category, error, null);
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
