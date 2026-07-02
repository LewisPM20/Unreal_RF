using System.Text.Json;
using RenderFarm.Domain;
using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

/// <summary>
/// Configuration for controller-owned job leases and retry policy.
/// </summary>
public sealed class JobSchedulerOptions
{
    public int LeaseSeconds { get; set; } = 120;
    public int MaxAttempts { get; set; } = 3;
    public int RetryDelaySeconds { get; set; }
    public int LeaseRecoverySeconds { get; set; } = 15;
    public double RetryBackoffMultiplier { get; set; } = 1;
    public Dictionary<string, FailureRetryPolicyOptions> FailurePolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Per-failure-category retry settings. Null values use controller defaults.
/// </summary>
public sealed class FailureRetryPolicyOptions
{
    public bool? Retryable { get; set; }
    public int? MaxAttempts { get; set; }
    public int? RetryDelaySeconds { get; set; }
}

public sealed record RetryDecision(bool ShouldRetry, TimeSpan Delay, string Reason);

public interface IRetryPolicy
{
    RetryDecision GetDecision(RenderJob job, int attemptCount, FailureCategory category, bool retryEligible);
}

/// <summary>
/// Configurable failure-category retry matrix for controller-owned scheduling.
/// </summary>
public sealed class ConfiguredRetryPolicy(Microsoft.Extensions.Options.IOptions<JobSchedulerOptions> options) : IRetryPolicy
{
    private static readonly IReadOnlyDictionary<FailureCategory, FailureRetryPolicyOptions> DefaultPolicies = new Dictionary<FailureCategory, FailureRetryPolicyOptions>
    {
        [FailureCategory.WorkerOffline] = Retryable(),
        [FailureCategory.WorkerStale] = Retryable(),
        [FailureCategory.SharedOutputUnreachable] = Retryable(),
        [FailureCategory.SharedOutputNotWritable] = NotRetryable(),
        [FailureCategory.ProjectPathMissing] = NotRetryable(),
        [FailureCategory.UProjectMissing] = NotRetryable(),
        [FailureCategory.EngineVersionMissing] = NotRetryable(),
        [FailureCategory.UnrealExecutableMissing] = NotRetryable(),
        [FailureCategory.UnrealLaunchFailed] = Retryable(),
        [FailureCategory.UnrealPythonFailed] = Retryable(),
        [FailureCategory.MrqAssetMissing] = NotRetryable(),
        [FailureCategory.RenderProcessFailed] = Retryable(),
        [FailureCategory.RenderProcessTimedOut] = Retryable(),
        [FailureCategory.RenderOutputMissing] = NotRetryable(),
        [FailureCategory.RenderOutputIncomplete] = NotRetryable(),
        [FailureCategory.CommandValidationFailed] = NotRetryable(),
        [FailureCategory.CancelledByUser] = NotRetryable(),
        [FailureCategory.Unknown] = Retryable()
    };

    public RetryDecision GetDecision(RenderJob job, int attemptCount, FailureCategory category, bool retryEligible)
    {
        if (!retryEligible)
        {
            return new(false, TimeSpan.Zero, "Retry was disabled for this failure.");
        }

        var policy = ResolvePolicy(category);
        if (policy.Retryable is false)
        {
            return new(false, TimeSpan.Zero, $"{category} is not retryable.");
        }

        var maxAttempts = Math.Max(1, policy.MaxAttempts ?? options.Value.MaxAttempts);
        if (attemptCount >= maxAttempts)
        {
            return new(false, TimeSpan.Zero, $"Retry limit reached for {category} ({attemptCount}/{maxAttempts}).");
        }

        var delaySeconds = Math.Max(0, policy.RetryDelaySeconds ?? options.Value.RetryDelaySeconds);
        if (delaySeconds > 0 && options.Value.RetryBackoffMultiplier > 1 && attemptCount > 1)
        {
            delaySeconds = (int)Math.Round(delaySeconds * Math.Pow(options.Value.RetryBackoffMultiplier, attemptCount - 1), MidpointRounding.AwayFromZero);
        }

        return new(true, TimeSpan.FromSeconds(delaySeconds), $"{category} is retryable ({attemptCount}/{maxAttempts}).");
    }

    private FailureRetryPolicyOptions ResolvePolicy(FailureCategory category)
    {
        var configured = options.Value.FailurePolicies.FirstOrDefault(pair => Enum.TryParse<FailureCategory>(pair.Key, true, out var parsed) && parsed == category).Value;
        var defaults = DefaultPolicies.TryGetValue(category, out var defaultPolicy) ? defaultPolicy : Retryable();
        return new FailureRetryPolicyOptions
        {
            Retryable = configured?.Retryable ?? defaults.Retryable,
            MaxAttempts = configured?.MaxAttempts ?? defaults.MaxAttempts,
            RetryDelaySeconds = configured?.RetryDelaySeconds ?? defaults.RetryDelaySeconds
        };
    }

    private static FailureRetryPolicyOptions Retryable() => new() { Retryable = true };

    private static FailureRetryPolicyOptions NotRetryable() => new() { Retryable = false, MaxAttempts = 1 };
}
public interface IJobScheduler
{
    Task<RenderJob> CreateJobAsync(CreateRenderJobRequest request, CancellationToken cancellationToken);
    Task<RenderJobDto?> RetryFailedJobAsNewAsync(string sourceJobId, CancellationToken cancellationToken);
    Task<JobAssignmentDto> RequestJobAsync(string workerId, CancellationToken cancellationToken);
    Task<JobLeaseDto?> RenewLeaseAsync(string jobId, JobLeaseRenewalRequest request, CancellationToken cancellationToken);
    Task<RenderJobDto?> StartJobAsync(string jobId, JobStartRequest request, CancellationToken cancellationToken);
    Task<RenderJobDto?> CompleteJobAsync(string jobId, JobCompletionRequest request, CancellationToken cancellationToken);
    Task<RenderJobDto?> FailJobAsync(string jobId, JobFailureRequest request, CancellationToken cancellationToken);
    Task<int> ExpireLeasesAsync(CancellationToken cancellationToken);
    Task<int> RecoverStartupAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Controller-owned scheduler for queued jobs, worker leases, attempts, state transitions, and retry hooks.
/// </summary>
public sealed class JobScheduler(
    IJobRepository jobs,
    IJobAttemptRepository attempts,
    IJobLeaseRepository leases,
    IJobEventRepository events,
    ISchedulerStateRepository schedulerState,
    IWorkerRepository workers,
    ISettingsRepository settings,
    IProjectRepository projects,
    IRenderProfileRepository profiles,
    IRetryPolicy retryPolicy,
    Microsoft.Extensions.Options.IOptions<JobSchedulerOptions> options,
    IJobNotificationSink? notificationSink = null,
    ILogger<JobScheduler>? logger = null) : IJobScheduler
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly IJobNotificationSink _notificationSink = notificationSink ?? NullJobNotificationSink.Instance;

    public async Task<RenderJob> CreateJobAsync(CreateRenderJobRequest request, CancellationToken cancellationToken)
    {
        if (request.FrameStart is not null || request.FrameEnd is not null || request.ChunkSizeFrames is not null)
        {
            throw new InvalidOperationException("Frame ranges and chunking are modelled but intentionally disabled until Unreal MRQ/MRG profile handling can guarantee deterministic per-chunk output.");
        }

        var now = DateTimeOffset.UtcNow;
        var job = new RenderJob(
            Id: Guid.NewGuid().ToString("N"),
            ProjectId: request.ProjectId,
            RenderProfileId: request.RenderProfileId,
            Name: request.Name,
            State: JobState.Queued,
            Priority: request.Priority,
            AssignedWorkerId: null,
            FailureCategory: FailureCategory.None,
            Error: null,
            OutputDirectory: request.OutputDirectory,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            QueuedAtUtc: now,
            StartedAtUtc: null,
            FinishedAtUtc: null,
            CancellationRequested: false);

        await jobs.UpsertAsync(job, cancellationToken);
        await AppendEventAsync(job.Id, null, null, JobState.Queued, FailureCategory.None, "Job queued", null, cancellationToken);
        return job;
    }

    public async Task<RenderJobDto?> RetryFailedJobAsNewAsync(string sourceJobId, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var source = await jobs.GetAsync(sourceJobId, cancellationToken);
            if (source is null)
            {
                return null;
            }

            if (source.State != JobState.Failed)
            {
                throw new InvalidOperationException($"Only terminal failed jobs can be retried as a new job. Current state is {source.State}.");
            }

            var now = DateTimeOffset.UtcNow;
            var retryJob = source with
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = source.Name.EndsWith(" retry", StringComparison.OrdinalIgnoreCase) ? source.Name : source.Name + " retry",
                State = JobState.Queued,
                AssignedWorkerId = null,
                FailureCategory = FailureCategory.None,
                Error = null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                QueuedAtUtc = now,
                StartedAtUtc = null,
                FinishedAtUtc = null,
                CancellationRequested = false
            };

            var retryData = JsonSerializer.Serialize(new { sourceJobId = source.Id, retryJobId = retryJob.Id, action = "RetryAsNewJob" }, RenderFarmJson.SerializerOptions);
            await schedulerState.ApplyAsync(new SchedulerStateMutation(
                Job: retryJob,
                Event: CreateEvent(retryJob.Id, null, null, JobState.Queued, FailureCategory.None, $"Retry job created from failed source job {source.Id}", retryData)), cancellationToken);
            await AppendEventAsync(source.Id, null, source.AssignedWorkerId, source.State, source.FailureCategory, $"Operator retry created new queued job {retryJob.Id}", retryData, cancellationToken);
            return retryJob.ToDto();
        }
        finally
        {
            _sync.Release();
        }
    }
    public async Task<JobAssignmentDto> RequestJobAsync(string workerId, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            await ExpireLeasesCoreAsync(cancellationToken);
            await PromoteDueRetriesAsync(cancellationToken);
            var worker = await workers.GetAsync(workerId, cancellationToken);
            if (worker is null)
            {
                return new(false, null, null, null, "Worker is not registered.");
            }

            if (!IsWorkerAvailable(worker))
            {
                return new(false, null, null, null, $"Worker status {worker.Status} is not eligible for scheduling.");
            }

            var schedulingMode = await WorkerScheduling.GetAsync(settings, worker.Id, cancellationToken);
            if (schedulingMode != WorkerSchedulingMode.Active)
            {
                return new(false, null, null, null, $"Worker scheduling mode {schedulingMode} does not allow new assignments.");
            }

            var defaults = await ControllerRenderDefaults.LoadAsync(settings, cancellationToken);
            foreach (var job in (await jobs.ListAsync(cancellationToken)).Where(x => x.State == JobState.Queued && (x.QueuedAtUtc is null || x.QueuedAtUtc <= DateTimeOffset.UtcNow)).OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAtUtc))
            {
                if (await leases.GetActiveForJobAsync(job.Id, cancellationToken) is not null)
                {
                    continue;
                }

                var project = await projects.GetAsync(job.ProjectId, cancellationToken);
                var profile = await profiles.GetAsync(job.RenderProfileId, cancellationToken);
                if (project is null || profile is null || profile.ProjectId != project.Id)
                {
                    continue;
                }

                var readiness = WorkerReadinessEvaluator.Evaluate(worker, project, profile, job.OutputDirectory, defaults);
                if (!readiness.CanRun)
                {
                    logger?.LogDebug("Worker {WorkerId} is not ready for job {JobId}: {Reasons}", worker.Id, job.Id, string.Join("; ", readiness.Reasons));
                    continue;
                }

                JobStateMachine.EnsureCanTransition(job.State, JobState.Reserved);
                var now = DateTimeOffset.UtcNow;
                var attemptNumber = (await attempts.ListForJobAsync(job.Id, cancellationToken)).Count + 1;
                var attempt = new JobAttempt(
                    Guid.NewGuid().ToString("N"),
                    job.Id,
                    attemptNumber,
                    worker.Id,
                    JobState.Reserved,
                    FailureCategory.None,
                    null,
                    null,
                    null,
                    now,
                    null,
                    null);
                var lease = new JobLease(
                    Guid.NewGuid().ToString("N"),
                    job.Id,
                    attempt.Id,
                    worker.Id,
                    now,
                    now.AddSeconds(Math.Max(10, options.Value.LeaseSeconds)),
                    null,
                    null,
                    null,
                    true);
                var reservedJob = job with
                {
                    State = JobState.Reserved,
                    AssignedWorkerId = worker.Id,
                    FailureCategory = FailureCategory.None,
                    Error = null,
                    UpdatedAtUtc = now
                };

                RenderExecutionDto execution;
                try
                {
                    execution = BuildExecutionPayload(worker, reservedJob, attempt, project, profile, defaults);
                }
                catch (InvalidOperationException ex)
                {
                    logger?.LogWarning(ex, "Controller could not resolve execution payload for job {JobId} and worker {WorkerId}", job.Id, worker.Id);
                    continue;
                }

                await schedulerState.ApplyAsync(new SchedulerStateMutation(
                    Job: reservedJob,
                    Attempt: attempt,
                    Lease: lease,
                    Event: CreateEvent(job.Id, attempt.Id, worker.Id, JobState.Reserved, FailureCategory.None, $"Job reserved by worker {worker.Id}", JsonSerializer.Serialize(execution, RenderFarmJson.SerializerOptions))), cancellationToken);
                return new(true, reservedJob.ToDto(), attempt.ToDto(), lease.ToDto(), "Job assigned.", execution);
            }
            return new(false, null, null, null, "No suitable queued job is available.");
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<JobLeaseDto?> RenewLeaseAsync(string jobId, JobLeaseRenewalRequest request, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            await ExpireLeasesCoreAsync(cancellationToken);
            var lease = await ValidateActiveLeaseAsync(jobId, request.LeaseId, request.WorkerId, cancellationToken);
            if (lease is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var renewed = lease with
            {
                RenewedAtUtc = now,
                ExpiresAtUtc = now.AddSeconds(Math.Max(10, request.LeaseSeconds ?? options.Value.LeaseSeconds))
            };
            await leases.UpsertAsync(renewed, cancellationToken);
            await AppendEventAsync(jobId, lease.JobAttemptId, lease.WorkerId, null, FailureCategory.None, "Lease renewed", null, cancellationToken);
            return renewed.ToDto();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<RenderJobDto?> StartJobAsync(string jobId, JobStartRequest request, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            await ExpireLeasesCoreAsync(cancellationToken);
            var job = await jobs.GetAsync(jobId, cancellationToken);
            if (job is not null && IsTerminal(job.State))
            {
                return job.ToDto();
            }

            var lease = await ValidateActiveLeaseAsync(jobId, request.LeaseId, request.WorkerId, cancellationToken);
            var attempt = lease is null ? null : await attempts.GetAsync(lease.JobAttemptId, cancellationToken);
            if (lease is null || job is null || attempt is null)
            {
                return null;
            }

            JobStateMachine.EnsureCanTransition(job.State, JobState.Running);
            JobStateMachine.EnsureCanTransition(attempt.State, JobState.Running);
            var now = DateTimeOffset.UtcNow;
            var runningJob = job with { State = JobState.Running, StartedAtUtc = job.StartedAtUtc ?? now, UpdatedAtUtc = now };
            var runningAttempt = attempt with { State = JobState.Running };
            await schedulerState.ApplyAsync(new SchedulerStateMutation(
                Job: runningJob,
                Attempt: runningAttempt,
                Event: CreateEvent(jobId, attempt.Id, lease.WorkerId, JobState.Running, FailureCategory.None, request.Message ?? "Job started", null)), cancellationToken);
            return runningJob.ToDto();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<RenderJobDto?> CompleteJobAsync(string jobId, JobCompletionRequest request, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var job = await jobs.GetAsync(jobId, cancellationToken);
            if (job is not null && IsTerminal(job.State))
            {
                return job.ToDto();
            }

            var lease = await ValidateActiveLeaseAsync(jobId, request.LeaseId, request.WorkerId, cancellationToken);
            var attempt = lease is null ? null : await attempts.GetAsync(lease.JobAttemptId, cancellationToken);
            if (lease is null || job is null || attempt is null)
            {
                return null;
            }

            JobStateMachine.EnsureCanTransition(job.State, JobState.Succeeded);
            JobStateMachine.EnsureCanTransition(attempt.State, JobState.Succeeded);
            var now = DateTimeOffset.UtcNow;
            var completedJob = job with
            {
                State = JobState.Succeeded,
                FailureCategory = FailureCategory.None,
                Error = null,
                OutputDirectory = request.OutputDirectory ?? job.OutputDirectory,
                UpdatedAtUtc = now,
                FinishedAtUtc = now
            };
            var completedAttempt = attempt with { State = JobState.Succeeded, FailureCategory = FailureCategory.None, Error = null, FinishedAtUtc = now, ExitCode = request.ExitCode };
            var releasedLease = lease with { IsActive = false, ReleasedAtUtc = now, ReleaseReason = "completed" };
            await schedulerState.ApplyAsync(new SchedulerStateMutation(
                Job: completedJob,
                Attempt: completedAttempt,
                Lease: releasedLease,
                Event: CreateEvent(jobId, attempt.Id, lease.WorkerId, JobState.Succeeded, FailureCategory.None, request.Message ?? "Job completed", request.ArtifactSummary is null ? null : JsonSerializer.Serialize(request.ArtifactSummary, RenderFarmJson.SerializerOptions))), cancellationToken);
            await NotifyTerminalJobAsync(completedJob, lease.WorkerId, cancellationToken);
            return completedJob.ToDto();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<RenderJobDto?> FailJobAsync(string jobId, JobFailureRequest request, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var job = await jobs.GetAsync(jobId, cancellationToken);
            if (job is not null && IsTerminal(job.State))
            {
                return job.ToDto();
            }

            var lease = await ValidateActiveLeaseAsync(jobId, request.LeaseId, request.WorkerId, cancellationToken);
            var attempt = lease is null ? null : await attempts.GetAsync(lease.JobAttemptId, cancellationToken);
            if (lease is null || job is null || attempt is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (request.FailureCategory == FailureCategory.CancelledByUser || job.CancellationRequested || job.State == JobState.CancelRequested)
            {
                JobStateMachine.EnsureCanTransition(job.State, JobState.Cancelled);
                JobStateMachine.EnsureCanTransition(attempt.State, JobState.Cancelled);

                var cancelledAttempt = attempt with
                {
                    State = JobState.Cancelled,
                    FailureCategory = FailureCategory.CancelledByUser,
                    Error = request.Error,
                    FinishedAtUtc = now,
                    ExitCode = request.ExitCode
                };
                var cancelledJob = job with
                {
                    State = JobState.Cancelled,
                    AssignedWorkerId = null,
                    FailureCategory = FailureCategory.CancelledByUser,
                    Error = request.Error,
                    CancellationRequested = true,
                    UpdatedAtUtc = now,
                    FinishedAtUtc = now
                };
                var cancelledLease = lease with { IsActive = false, ReleasedAtUtc = now, ReleaseReason = "cancelled" };

                await schedulerState.ApplyAsync(new SchedulerStateMutation(
                    Job: cancelledJob,
                    Attempt: cancelledAttempt,
                    Lease: cancelledLease,
                    Event: CreateEvent(jobId, attempt.Id, lease.WorkerId, JobState.Cancelled, FailureCategory.CancelledByUser, $"Job cancelled: {request.Error}", null)), cancellationToken);
                await NotifyTerminalJobAsync(cancelledJob, lease.WorkerId, cancellationToken);
                return cancelledJob.ToDto();
            }

            var attemptCount = (await attempts.ListForJobAsync(jobId, cancellationToken)).Count;
            var decision = retryPolicy.GetDecision(job, attemptCount, request.FailureCategory, request.RetryEligible);
            var nextJobState = decision.ShouldRetry ? (decision.Delay > TimeSpan.Zero ? JobState.RetryWait : JobState.Queued) : JobState.Failed;
            var retryDueAt = decision.ShouldRetry ? now.Add(decision.Delay) : job.QueuedAtUtc;
            JobStateMachine.EnsureCanTransition(job.State, nextJobState);
            JobStateMachine.EnsureCanTransition(attempt.State, JobState.Failed);
            var failedAttempt = attempt with { State = JobState.Failed, FailureCategory = request.FailureCategory, Error = request.Error, FinishedAtUtc = now, ExitCode = request.ExitCode };
            var nextJob = decision.ShouldRetry
                ? job with { State = nextJobState, AssignedWorkerId = null, FailureCategory = request.FailureCategory, Error = request.Error, UpdatedAtUtc = now, QueuedAtUtc = retryDueAt }
                : job with { State = nextJobState, FailureCategory = request.FailureCategory, Error = request.Error, UpdatedAtUtc = now, FinishedAtUtc = now };
            var releasedLease = lease with { IsActive = false, ReleasedAtUtc = now, ReleaseReason = decision.ShouldRetry ? (decision.Delay > TimeSpan.Zero ? "failed-retry-wait" : "failed-requeued") : "failed" };
            await schedulerState.ApplyAsync(new SchedulerStateMutation(
                Job: nextJob,
                Attempt: failedAttempt,
                Lease: releasedLease,
                Event: CreateEvent(jobId, attempt.Id, lease.WorkerId, nextJob.State, request.FailureCategory, decision.ShouldRetry ? $"Job failed and will retry: {request.Error}. {decision.Reason}" : $"Job failed: {request.Error}. {decision.Reason}", null)), cancellationToken);
            if (nextJob.State == JobState.Failed)
            {
                await NotifyTerminalJobAsync(nextJob, lease.WorkerId, cancellationToken);
            }

            return nextJob.ToDto();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<int> ExpireLeasesAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var expired = await ExpireLeasesCoreAsync(cancellationToken);
            await PromoteDueRetriesAsync(cancellationToken);
            return expired;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<int> RecoverStartupAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var recovered = await ExpireLeasesCoreAsync(cancellationToken);
            await PromoteDueRetriesAsync(cancellationToken);
            recovered += await RecoverJobsWithoutActiveLeasesAsync(cancellationToken);
            return recovered;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task PromoteDueRetriesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in (await jobs.ListAsync(cancellationToken)).Where(x => x.State == JobState.RetryWait && x.QueuedAtUtc <= now))
        {
            JobStateMachine.EnsureCanTransition(job.State, JobState.Queued);
            var queued = job with { State = JobState.Queued, UpdatedAtUtc = now, QueuedAtUtc = now };
            await schedulerState.ApplyAsync(new SchedulerStateMutation(
                Job: queued,
                Event: CreateEvent(job.Id, null, null, JobState.Queued, job.FailureCategory, "Retry delay elapsed; job requeued", null)), cancellationToken);
        }
    }
    private async Task<int> RecoverJobsWithoutActiveLeasesAsync(CancellationToken cancellationToken)
    {
        var activeLeases = await leases.ListActiveAsync(cancellationToken);
        var leasedJobIds = activeLeases.Select(lease => lease.JobId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recoverableStates = new HashSet<JobState>
        {
            JobState.Reserved,
            JobState.Running,
            JobState.ValidatingWorker,
            JobState.PreparingUnrealQueue,
            JobState.LaunchingUnreal,
            JobState.Rendering,
            JobState.VerifyingOutputs
        };
        var now = DateTimeOffset.UtcNow;
        var count = 0;

        foreach (var job in (await jobs.ListAsync(cancellationToken)).Where(job => recoverableStates.Contains(job.State) && !leasedJobIds.Contains(job.Id)))
        {
            var jobAttempts = await attempts.ListForJobAsync(job.Id, cancellationToken);
            var attempt = jobAttempts.OrderByDescending(item => item.AttemptNumber).FirstOrDefault();
            var attemptCount = jobAttempts.Count;
            var decision = retryPolicy.GetDecision(job, attemptCount, FailureCategory.WorkerStale, retryEligible: true);
            var desiredRetryState = decision.Delay > TimeSpan.Zero ? JobState.RetryWait : JobState.Queued;
            var nextJobState = decision.ShouldRetry && JobStateMachine.CanTransition(job.State, desiredRetryState) ? desiredRetryState : JobState.Failed;
            var retryDueAt = nextJobState == JobState.RetryWait ? now.Add(decision.Delay) : nextJobState == JobState.Queued ? now : job.QueuedAtUtc;
            JobStateMachine.EnsureCanTransition(job.State, nextJobState);

            JobAttempt? recoveredAttempt = null;
            if (attempt is not null)
            {
                var attemptState = JobStateMachine.CanTransition(attempt.State, JobState.Stale) ? JobState.Stale : JobState.Failed;
                recoveredAttempt = attempt with { State = attemptState, FailureCategory = FailureCategory.WorkerStale, Error = "Controller startup found no active lease for this job.", FinishedAtUtc = now };
            }

            var nextJob = nextJobState == JobState.Failed
                ? job with { State = JobState.Failed, FailureCategory = FailureCategory.WorkerStale, Error = "Controller startup found active job without a valid lease; retry was not possible.", UpdatedAtUtc = now, FinishedAtUtc = now }
                : job with { State = nextJobState, AssignedWorkerId = null, FailureCategory = FailureCategory.WorkerStale, Error = "Controller startup recovered job without an active lease.", UpdatedAtUtc = now, QueuedAtUtc = retryDueAt };

            await schedulerState.ApplyAsync(new SchedulerStateMutation(
                Job: nextJob,
                Attempt: recoveredAttempt,
                Event: CreateEvent(job.Id, attempt?.Id, job.AssignedWorkerId, nextJob.State, FailureCategory.WorkerStale, $"Startup recovery: job had state {job.State} but no active lease. {decision.Reason}", null)), cancellationToken);
            if (nextJob.State == JobState.Failed)
            {
                await NotifyTerminalJobAsync(nextJob, nextJob.AssignedWorkerId, cancellationToken);
            }
            count++;
        }

        return count;
    }

    private async Task<int> ExpireLeasesCoreAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await leases.ListExpiredAsync(now, cancellationToken);
        var count = 0;
        foreach (var lease in expired)
        {
            var job = await jobs.GetAsync(lease.JobId, cancellationToken);
            var attempt = await attempts.GetAsync(lease.JobAttemptId, cancellationToken);
            if (job is null || attempt is null)
            {
                await schedulerState.ApplyAsync(new SchedulerStateMutation(Lease: lease with { IsActive = false, ReleasedAtUtc = now, ReleaseReason = "expired-orphaned" }), cancellationToken);
                count++;
                continue;
            }

            if (IsTerminal(job.State))
            {
                await schedulerState.ApplyAsync(new SchedulerStateMutation(Lease: lease with { IsActive = false, ReleasedAtUtc = now, ReleaseReason = "expired-terminal" }), cancellationToken);
                count++;
                continue;
            }

            var attemptCount = (await attempts.ListForJobAsync(job.Id, cancellationToken)).Count;
            var decision = retryPolicy.GetDecision(job, attemptCount, FailureCategory.WorkerStale, retryEligible: true);
            var nextJobState = decision.ShouldRetry ? (decision.Delay > TimeSpan.Zero ? JobState.RetryWait : JobState.Queued) : JobState.Failed;
            var retryDueAt = decision.ShouldRetry ? now.Add(decision.Delay) : job.QueuedAtUtc;
            JobStateMachine.EnsureCanTransition(job.State, nextJobState);
            JobStateMachine.EnsureCanTransition(attempt.State, JobState.Stale);
            var staleAttempt = attempt with { State = JobState.Stale, FailureCategory = FailureCategory.WorkerStale, Error = "Lease expired", FinishedAtUtc = now };
            var nextJob = decision.ShouldRetry
                ? job with { State = nextJobState, AssignedWorkerId = null, FailureCategory = FailureCategory.WorkerStale, Error = "Lease expired; job will retry", UpdatedAtUtc = now, QueuedAtUtc = retryDueAt }
                : job with { State = nextJobState, FailureCategory = FailureCategory.WorkerStale, Error = "Lease expired; retry limit reached", UpdatedAtUtc = now, FinishedAtUtc = now };
            await schedulerState.ApplyAsync(new SchedulerStateMutation(
                Job: nextJob,
                Attempt: staleAttempt,
                Lease: lease with { IsActive = false, ReleasedAtUtc = now, ReleaseReason = decision.ShouldRetry ? (decision.Delay > TimeSpan.Zero ? "expired-retry-wait" : "expired-requeued") : "expired-failed" },
                Event: CreateEvent(job.Id, attempt.Id, lease.WorkerId, nextJob.State, FailureCategory.WorkerStale, $"{nextJob.Error ?? "Lease expired"}. {decision.Reason}", null)), cancellationToken);
            if (nextJob.State == JobState.Failed)
            {
                await NotifyTerminalJobAsync(nextJob, nextJob.AssignedWorkerId, cancellationToken);
            }
            count++;
        }

        return count;
    }

    private async Task<JobLease?> ValidateActiveLeaseAsync(string jobId, string leaseId, string workerId, CancellationToken cancellationToken)
    {
        var lease = await leases.GetAsync(leaseId, cancellationToken);
        if (lease is null || !lease.IsActive || lease.JobId != jobId || !string.Equals(lease.WorkerId, workerId, StringComparison.OrdinalIgnoreCase) || lease.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return lease;
    }

    private RenderExecutionDto BuildExecutionPayload(Worker worker, RenderJob job, JobAttempt attempt, ProjectProfile project, RenderProfile profile, RenderDefaultsDto defaults)
    {
        var workerPath = project.WorkerPaths.FirstOrDefault(x => string.Equals(x.WorkerId, worker.Id, StringComparison.OrdinalIgnoreCase));
        var projectPath = FirstNonEmpty(
            workerPath?.ProjectPath,
            GetSetting(profile, "projectPath"),
            GetSetting(profile, "uprojectPath"),
            project.UProjectPath);
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidOperationException("No project .uproject path is configured for the selected project or worker mapping.");
        }

        var unrealExecutable = FirstNonEmpty(
            ResolveUnrealExecutableCandidate(workerPath?.EnginePath),
            ResolveUnrealExecutableCandidate(GetSetting(profile, "unrealExecutablePath")),
            ResolveUnrealExecutableCandidate(GetSetting(profile, "unrealExe")),
            ResolveUnrealExecutableCandidate(GetSetting(profile, "unrealCommand")),
            ResolveUnrealExecutableCandidate(GetSetting(profile, "unrealSearchRoot")),
            ResolveUnrealExecutableCandidate(defaults.UnrealExecutablePath),
            ResolveUnrealExecutableCandidate(defaults.UnrealSearchRoot),
            ResolveReportedUnrealExecutable(worker, project));
        if (string.IsNullOrWhiteSpace(unrealExecutable))
        {
            throw new InvalidOperationException("No Unreal executable is configured by the controller and the worker has not reported a usable Unreal installation.");
        }

        var outputRoot = FirstNonEmpty(
            GetSetting(profile, "defaultOutputRoot"),
            GetSetting(profile, "outputRoot"),
            defaults.SharedOutputRoot,
            worker.Capabilities.SharedOutputRoots.FirstOrDefault(root => root.Exists && root.Writable)?.Path,
            Path.Combine(AppContext.BaseDirectory, "renders"));

        var seedVariables = BuildExecutionVariables(worker, job, attempt, project, profile, projectPath, unrealExecutable, outputRoot, string.Empty, string.Empty);
        var configuredOutput = FirstNonEmpty(
            job.OutputDirectory,
            GetSetting(profile, "outputDirectory"),
            GetSetting(profile, "defaultOutputDirectory"));
        var outputDirectory = string.IsNullOrWhiteSpace(configuredOutput)
            ? Path.Combine(ExpandTemplateValue(outputRoot, seedVariables), ExpandTemplateValue(FirstNonEmpty(GetSetting(profile, "outputSubfolderPattern"), defaults.OutputSubfolderPattern, "{JobId}"), seedVariables))
            : ExpandTemplateValue(configuredOutput, seedVariables);
        outputDirectory = Environment.ExpandEnvironmentVariables(outputDirectory.Trim());

        var logDirectory = Environment.ExpandEnvironmentVariables(ExpandTemplateValue(FirstNonEmpty(
            workerPath?.LogDirectory,
            GetSetting(profile, "logDirectory"),
            Path.Combine(outputDirectory, "logs")), seedVariables).Trim());
        var variables = BuildExecutionVariables(worker, job, attempt, project, profile, projectPath, unrealExecutable, outputRoot, outputDirectory, logDirectory);
        var timeoutSeconds = TryGetInt(GetSetting(profile, "timeoutSeconds")) ?? TryGetInt(GetSetting(profile, "renderTimeoutSeconds"));

        return new RenderExecutionDto(
            unrealExecutable,
            projectPath,
            profile.ToDto(),
            outputDirectory,
            logDirectory,
            variables,
            timeoutSeconds);
    }

    private static Dictionary<string, string> BuildExecutionVariables(Worker worker, RenderJob job, JobAttempt attempt, ProjectProfile project, RenderProfile profile, string projectPath, string unrealExecutable, string outputRoot, string outputDirectory, string logDirectory)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add("ProjectPath", projectPath);
        Add("ProjectDir", Path.GetDirectoryName(projectPath) ?? string.Empty);
        Add("UnrealExe", unrealExecutable);
        Add("OutputRoot", outputRoot);
        Add("OutputFolder", string.IsNullOrWhiteSpace(outputDirectory) ? string.Empty : Path.GetFileName(outputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        Add("OutputDirectory", outputDirectory);
        Add("LogDirectory", logDirectory);
        Add("JobId", job.Id);
        Add("JobName", job.Name);
        Add("AttemptId", attempt.Id);
        Add("AttemptNumber", attempt.AttemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("ProjectId", project.Id);
        Add("ProjectName", project.DisplayName);
        Add("ProfileId", profile.Id);
        Add("ProfileName", profile.DisplayName);
        Add("WorkerId", worker.Id);
        Add("WorkerName", worker.Name);
        Add("Map", FirstNonEmpty(GetSetting(profile, "map"), GetSetting(profile, "mapName"), GetSetting(profile, "level"), GetSetting(profile, "levelName")));
        Add("Sequence", FirstNonEmpty(GetSetting(profile, "sequence"), GetSetting(profile, "levelSequence")));
        Add("RenderConfig", FirstNonEmpty(profile.AssetPath, GetSetting(profile, "moviePipelineConfig"), GetSetting(profile, "mrqConfig"), GetSetting(profile, "queue")));
        Add("MoviePipelineConfig", FirstNonEmpty(profile.AssetPath, GetSetting(profile, "moviePipelineConfig"), GetSetting(profile, "mrqConfig"), GetSetting(profile, "queue")));
        return variables;

        void Add(string key, string? value) => variables[key] = value ?? string.Empty;
    }

    private static string ExpandTemplateValue(string value, IReadOnlyDictionary<string, string> variables)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        foreach (var variable in variables)
        {
            expanded = expanded.Replace("{" + variable.Key + "}", variable.Value, StringComparison.OrdinalIgnoreCase);
        }

        return expanded;
    }

    private static string? ResolveUnrealExecutableCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        if (string.Equals(Path.GetFileName(candidate), "Engine", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(candidate, "Binaries", "Win64", "UnrealEditor-Cmd.exe");
        }

        return Path.Combine(candidate, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
    }

    private static string? ResolveReportedUnrealExecutable(Worker worker, ProjectProfile project)
    {
        var desiredVersions = new[] { project.PreferredEngineVersion }
            .Concat(project.AllowedEngineVersions)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var installs = worker.Capabilities.UnrealInstallations.Where(install => install.Exists).ToArray();
        if (desiredVersions.Length == 0)
        {
            return installs.LastOrDefault()?.ExecutablePath;
        }

        return installs.FirstOrDefault(install => desiredVersions.Contains(install.Version, StringComparer.OrdinalIgnoreCase))?.ExecutablePath;
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

    private static int? TryGetInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    private static bool IsWorkerAvailable(Worker worker) => worker.Status is WorkerStatus.Online or WorkerStatus.Idle;

    private static bool IsTerminal(JobState state) => JobStateMachine.IsTerminal(state);

    private async Task NotifyTerminalJobAsync(RenderJob job, string? workerId, CancellationToken cancellationToken)
    {
        var payload = new JobNotificationPayloadDto(
            job.Id,
            job.Name,
            job.State,
            job.ProjectId,
            job.RenderProfileId,
            workerId,
            job.FailureCategory,
            job.Error,
            job.OutputDirectory,
            job.CreatedAtUtc,
            job.QueuedAtUtc,
            job.StartedAtUtc,
            job.FinishedAtUtc);

        try
        {
            await _notificationSink.NotifyTerminalAsync(payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Terminal job notification failed for job {JobId}", job.Id);
        }
    }

    private static JobEvent CreateEvent(string jobId, string? attemptId, string? workerId, JobState? state, FailureCategory category, string message, string? dataJson) =>
        new(Guid.NewGuid().ToString("N"), jobId, attemptId, workerId, state, category, message, DateTimeOffset.UtcNow, dataJson);

    private Task AppendEventAsync(string jobId, string? attemptId, string? workerId, JobState? state, FailureCategory category, string message, string? dataJson, CancellationToken cancellationToken) =>
        events.AppendAsync(CreateEvent(jobId, attemptId, workerId, state, category, message, dataJson), cancellationToken);
}

