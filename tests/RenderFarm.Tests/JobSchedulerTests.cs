using System.Text.Json;
using Microsoft.Extensions.Options;
using RenderFarm.Controller.Api;
using RenderFarm.Domain;
using RenderFarm.Persistence;
using RenderFarm.Shared;
using Xunit;

namespace RenderFarm.Tests;

public sealed class JobSchedulerTests
{
    [Fact]
    public async Task RequestJobCreatesSingleActiveLease()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);

        var first = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        var second = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);

        Assert.True(first.Assigned);
        Assert.NotNull(first.Job);
        Assert.NotNull(first.Attempt);
        Assert.NotNull(first.Lease);
        Assert.NotNull(first.Execution);
        Assert.Equal("D:\\Projects\\Demo\\Demo.uproject", first.Execution.ProjectPath);
        Assert.EndsWith("UnrealEditor-Cmd.exe", first.Execution.UnrealExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("worker-1", first.Execution.Variables["WorkerId"]);
        Assert.Equal(JobState.Reserved, first.Job.State);
        Assert.False(second.Assigned);
        Assert.Contains("No suitable queued job", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await harness.Attempts.ListForJobAsync(first.Job.Id, CancellationToken.None));
        Assert.Single(await harness.Leases.ListActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredLeaseMarksAttemptStaleAndRequeuesRetryableJob()
    {
        var harness = await SchedulerHarness.CreateAsync(maxAttempts: 3);
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        await harness.Leases.UpsertAsync(assignment.Lease.ToDomain() with
        {
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
        }, CancellationToken.None);

        var expired = await harness.Scheduler.ExpireLeasesAsync(CancellationToken.None);
        var job = await harness.Jobs.GetAsync(assignment.Job.Id, CancellationToken.None);
        var attempts = await harness.Attempts.ListForJobAsync(assignment.Job.Id, CancellationToken.None);
        var events = await harness.Events.ListForJobAsync(assignment.Job.Id, CancellationToken.None);

        Assert.Equal(1, expired);
        Assert.NotNull(job);
        Assert.Equal(JobState.Queued, job.State);
        Assert.Null(job.AssignedWorkerId);
        Assert.Equal(FailureCategory.WorkerStale, job.FailureCategory);
        Assert.Single(attempts);
        Assert.Equal(JobState.Stale, attempts[0].State);
        Assert.Empty(await harness.Leases.ListActiveAsync(CancellationToken.None));
        Assert.Contains(events, evt => evt.State == JobState.Queued && evt.FailureCategory == FailureCategory.WorkerStale);
    }

    [Fact]
    public async Task RequestJobUsesControllerOwnedDefaultsForExecutionPayload()
    {
        var harness = await SchedulerHarness.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var defaults = new RenderDefaultsDto(
            "C:\\Program Files\\Epic Games\\UE_5.7\\Engine\\Binaries\\Win64\\UnrealEditor-Cmd.exe",
            null,
            "\\\\server\\renders",
            "{ProjectName}\\{JobId}");
        await harness.Settings.UpsertAsync(new FarmSetting("render.defaults", JsonSerializer.Serialize(defaults, RenderFarmJson.SerializerOptions), now), CancellationToken.None);
        await harness.Projects.UpsertAsync(new ProjectProfile(
            "project-1",
            "Demo Project",
            "D:\\Projects\\Demo\\Demo.uproject",
            "5.7",
            ["5.7"],
            null,
            []), CancellationToken.None);
        await harness.Profiles.UpsertAsync(new RenderProfile(
            "profile-1",
            "project-1",
            "Main Queue",
            RenderProfileType.MrqQueue,
            "/Game/MainQueue",
            null,
            "png",
            false,
            new Dictionary<string, string> { ["map"] = "/Game/Maps/Main" }), CancellationToken.None);
        await harness.Workers.UpsertAsync(new RenderFarm.Domain.Worker(
            "worker-1",
            "Worker 1",
            "host",
            "127.0.0.1",
            null,
            WorkerStatus.Idle,
            null,
            null,
            "test",
            new WorkerCapabilities(
                16,
                64,
                "GPU",
                12,
                500,
                [],
                [new ProjectPathStatus("D:\\Projects\\Demo\\Demo.uproject", true)],
                []),
            null,
            now,
            now), CancellationToken.None);
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);

        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);

        Assert.True(assignment.Assigned);
        Assert.NotNull(assignment.Execution);
        Assert.Equal(defaults.UnrealExecutablePath, assignment.Execution.UnrealExecutablePath);
        Assert.Equal("D:\\Projects\\Demo\\Demo.uproject", assignment.Execution.ProjectPath);
        Assert.Contains("Demo Project", assignment.Execution.OutputDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(assignment.Job!.Id, assignment.Execution.OutputDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PendingWorkerCannotReserveJob()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync(WorkerStatus.Pending);
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);

        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);

        Assert.False(assignment.Assigned);
        Assert.Contains("not eligible", assignment.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Leases.ListActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DrainingWorkerCannotReserveNewJob()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Settings.UpsertAsync(new FarmSetting("worker.scheduling.worker-1", "\"Draining\"", DateTimeOffset.UtcNow), CancellationToken.None);
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);

        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);

        Assert.False(assignment.Assigned);
        Assert.Contains("Draining", assignment.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Leases.ListActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DisabledWorkerCannotReserveNewJob()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Settings.UpsertAsync(new FarmSetting("worker.scheduling.worker-1", "\"Disabled\"", DateTimeOffset.UtcNow), CancellationToken.None);
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);

        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);

        Assert.False(assignment.Assigned);
        Assert.Contains("Disabled", assignment.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Leases.ListActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancelledByUserReportsCancelledNotFailed()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        var cancelled = await harness.Scheduler.FailJobAsync(
            assignment.Job.Id,
            new JobFailureRequest(assignment.Lease.Id, "worker-1", FailureCategory.CancelledByUser, "Render cancelled by operator.", null, RetryEligible: false),
            CancellationToken.None);

        Assert.NotNull(cancelled);
        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.Equal(FailureCategory.CancelledByUser, cancelled.FailureCategory);
        Assert.Empty(await harness.Leases.ListActiveAsync(CancellationToken.None));
        var attempts = await harness.Attempts.ListForJobAsync(assignment.Job.Id, CancellationToken.None);
        Assert.Equal(JobState.Cancelled, attempts.Single().State);
    }

    [Fact]
    public async Task StartupRecoveryRequeuesActiveJobWithoutLease()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);
        await harness.Scheduler.StartJobAsync(assignment.Job.Id, new JobStartRequest(assignment.Lease.Id, "worker-1"), CancellationToken.None);
        await harness.Leases.UpsertAsync(assignment.Lease.ToDomain() with { IsActive = false, ReleasedAtUtc = DateTimeOffset.UtcNow, ReleaseReason = "simulated-crash" }, CancellationToken.None);

        var recovered = await harness.Scheduler.RecoverStartupAsync(CancellationToken.None);
        var job = await harness.Jobs.GetAsync(assignment.Job.Id, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.NotNull(job);
        Assert.Equal(JobState.Queued, job.State);
        Assert.Null(job.AssignedWorkerId);
    }

    [Fact]
    public async Task StartupRecoveryLeavesTerminalJobUntouched()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);
        await harness.Scheduler.CompleteJobAsync(assignment.Job.Id, new JobCompletionRequest(assignment.Lease.Id, "worker-1", 0, @"D:\renders\done"), CancellationToken.None);

        var recovered = await harness.Scheduler.RecoverStartupAsync(CancellationToken.None);
        var job = await harness.Jobs.GetAsync(assignment.Job.Id, CancellationToken.None);

        Assert.Equal(0, recovered);
        Assert.NotNull(job);
        Assert.Equal(JobState.Succeeded, job.State);
    }


    [Fact]
    public async Task RetryFailedJobAsNewCreatesCleanQueuedCloneWithTraceability()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);
        var failed = await harness.Scheduler.FailJobAsync(assignment.Job.Id, new JobFailureRequest(assignment.Lease.Id, "worker-1", FailureCategory.ProjectPathMissing, "Missing project."), CancellationToken.None);
        Assert.NotNull(failed);
        Assert.Equal(JobState.Failed, failed.State);

        var retry = await harness.Scheduler.RetryFailedJobAsNewAsync(failed.Id, CancellationToken.None);
        var original = await harness.Jobs.GetAsync(failed.Id, CancellationToken.None);
        var retryDomain = retry is null ? null : await harness.Jobs.GetAsync(retry.Id, CancellationToken.None);
        var originalEvents = await harness.Events.ListForJobAsync(failed.Id, CancellationToken.None);
        IReadOnlyList<JobEvent> retryEvents = retry is null ? Array.Empty<JobEvent>() : await harness.Events.ListForJobAsync(retry.Id, CancellationToken.None);

        Assert.NotNull(retry);
        Assert.NotEqual(failed.Id, retry.Id);
        Assert.NotNull(original);
        Assert.Equal(JobState.Failed, original.State);
        Assert.NotNull(retryDomain);
        Assert.Equal(JobState.Queued, retryDomain.State);
        Assert.Null(retryDomain.AssignedWorkerId);
        Assert.Equal(FailureCategory.None, retryDomain.FailureCategory);
        Assert.Contains(originalEvents, evt => evt.Message.Contains(retry.Id, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(retryEvents, evt => evt.DataJson?.Contains(failed.Id, StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task RetryFailedJobAsNewRejectsNonFailedTerminalJobs()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);
        var completed = await harness.Scheduler.CompleteJobAsync(assignment.Job.Id, new JobCompletionRequest(assignment.Lease.Id, "worker-1", 0, @"D:\renders\done"), CancellationToken.None);
        Assert.NotNull(completed);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Scheduler.RetryFailedJobAsNewAsync(completed.Id, CancellationToken.None));

        Assert.Contains("Only terminal failed jobs", error.Message, StringComparison.OrdinalIgnoreCase);
        var original = await harness.Jobs.GetAsync(completed.Id, CancellationToken.None);
        Assert.NotNull(original);
        Assert.Equal(JobState.Succeeded, original.State);
    }

    [Fact]
    public async Task StartupRecoveryDoesNotKillValidActiveLease()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);

        var recovered = await harness.Scheduler.RecoverStartupAsync(CancellationToken.None);
        var job = await harness.Jobs.GetAsync(assignment.Job.Id, CancellationToken.None);

        Assert.Equal(0, recovered);
        Assert.NotNull(job);
        Assert.Equal(JobState.Reserved, job.State);
        Assert.Single(await harness.Leases.ListActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartupRecoveryCanRunTwiceSafely()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);
        await harness.Scheduler.StartJobAsync(assignment.Job.Id, new JobStartRequest(assignment.Lease.Id, "worker-1"), CancellationToken.None);
        await harness.Leases.UpsertAsync(assignment.Lease.ToDomain() with { IsActive = false, ReleasedAtUtc = DateTimeOffset.UtcNow, ReleaseReason = "simulated-crash" }, CancellationToken.None);

        var first = await harness.Scheduler.RecoverStartupAsync(CancellationToken.None);
        var second = await harness.Scheduler.RecoverStartupAsync(CancellationToken.None);
        var job = await harness.Jobs.GetAsync(assignment.Job.Id, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.NotNull(job);
        Assert.Equal(JobState.Queued, job.State);
        Assert.Single(await harness.Attempts.ListForJobAsync(assignment.Job.Id, CancellationToken.None));
    }    [Fact]
    public async Task NotificationSinkReceivesSucceededPayload()
    {
        var sink = new RecordingNotificationSink();
        var harness = await SchedulerHarness.CreateAsync(notificationSink: sink);
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        await harness.Scheduler.CompleteJobAsync(assignment.Job.Id, new JobCompletionRequest(assignment.Lease.Id, "worker-1", 0, @"D:\renders\done"), CancellationToken.None);

        var payload = Assert.Single(sink.Payloads);
        Assert.Equal(JobState.Succeeded, payload.FinalState);
        Assert.Equal(assignment.Job.Id, payload.JobId);
    }

    [Fact]
    public async Task NotificationSinkReceivesFailedPayload()
    {
        var sink = new RecordingNotificationSink();
        var harness = await SchedulerHarness.CreateAsync(notificationSink: sink);
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        await harness.Scheduler.FailJobAsync(assignment.Job.Id, new JobFailureRequest(assignment.Lease.Id, "worker-1", FailureCategory.ProjectPathMissing, "Missing project."), CancellationToken.None);

        var payload = Assert.Single(sink.Payloads);
        Assert.Equal(JobState.Failed, payload.FinalState);
        Assert.Equal(FailureCategory.ProjectPathMissing, payload.FailureCategory);
    }

    [Fact]
    public async Task NotificationSinkReceivesCancelledPayload()
    {
        var sink = new RecordingNotificationSink();
        var harness = await SchedulerHarness.CreateAsync(notificationSink: sink);
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        await harness.Scheduler.FailJobAsync(assignment.Job.Id, new JobFailureRequest(assignment.Lease.Id, "worker-1", FailureCategory.CancelledByUser, "Cancelled.", RetryEligible: false), CancellationToken.None);

        var payload = Assert.Single(sink.Payloads);
        Assert.Equal(JobState.Cancelled, payload.FinalState);
        Assert.Equal(FailureCategory.CancelledByUser, payload.FailureCategory);
    }

    [Fact]
    public async Task NotificationFailureDoesNotFailJobTransition()
    {
        var harness = await SchedulerHarness.CreateAsync(notificationSink: new ThrowingNotificationSink());
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        var completed = await harness.Scheduler.CompleteJobAsync(assignment.Job.Id, new JobCompletionRequest(assignment.Lease.Id, "worker-1", 0, @"D:\renders\done"), CancellationToken.None);

        Assert.NotNull(completed);
        Assert.Equal(JobState.Succeeded, completed.State);
    }

    [Fact]
    public async Task CompletedJobDoesNotReopenWhenCompletionIsPostedAgain()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        var request = new JobCompletionRequest(assignment.Lease.Id, "worker-1", 0, @"D:\renders\done");
        var first = await harness.Scheduler.CompleteJobAsync(assignment.Job.Id, request, CancellationToken.None);
        var second = await harness.Scheduler.CompleteJobAsync(assignment.Job.Id, request, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(JobState.Succeeded, first.State);
        Assert.Equal(JobState.Succeeded, second.State);
        Assert.Empty(await harness.Leases.ListActiveAsync(CancellationToken.None));
    }



    [Fact]
    public async Task NonRetryableFailureFailsJobWithoutLooping()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        var result = await harness.Scheduler.FailJobAsync(
            assignment.Job.Id,
            new JobFailureRequest(assignment.Lease.Id, "worker-1", FailureCategory.ProjectPathMissing, "Project path was not present on worker."),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(JobState.Failed, result.State);
        Assert.Equal(FailureCategory.ProjectPathMissing, result.FailureCategory);
        Assert.Empty(await harness.Leases.ListActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetryableFailureCanWaitBeforeBeingAssignedAgain()
    {
        var harness = await SchedulerHarness.CreateAsync(retryDelaySeconds: 60);
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        var failed = await harness.Scheduler.FailJobAsync(
            assignment.Job.Id,
            new JobFailureRequest(assignment.Lease.Id, "worker-1", FailureCategory.RenderProcessTimedOut, "Renderer timed out."),
            CancellationToken.None);

        Assert.NotNull(failed);
        Assert.Equal(JobState.RetryWait, failed.State);
        Assert.True(failed.QueuedAtUtc > DateTimeOffset.UtcNow);

        var immediate = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.False(immediate.Assigned);

        await harness.Jobs.UpsertAsync(failed.ToDomain() with { QueuedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, CancellationToken.None);
        var retry = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);

        Assert.True(retry.Assigned);
        Assert.NotNull(retry.Job);
        Assert.Equal(JobState.Reserved, retry.Job.State);
        Assert.Equal(2, (await harness.Attempts.ListForJobAsync(assignment.Job.Id, CancellationToken.None)).Count);
    }
    [Fact]
    public async Task ExpireLeasesPromotesDueRetryWaitJobs()
    {
        var harness = await SchedulerHarness.CreateAsync(retryDelaySeconds: 60);
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        var failed = await harness.Scheduler.FailJobAsync(
            assignment.Job.Id,
            new JobFailureRequest(assignment.Lease.Id, "worker-1", FailureCategory.RenderProcessTimedOut, "Renderer timed out."),
            CancellationToken.None);
        Assert.NotNull(failed);
        Assert.Equal(JobState.RetryWait, failed.State);

        await harness.Jobs.UpsertAsync(failed.ToDomain() with { QueuedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, CancellationToken.None);

        var expired = await harness.Scheduler.ExpireLeasesAsync(CancellationToken.None);
        var job = await harness.Jobs.GetAsync(assignment.Job.Id, CancellationToken.None);
        var events = await harness.Events.ListForJobAsync(assignment.Job.Id, CancellationToken.None);

        Assert.Equal(0, expired);
        Assert.NotNull(job);
        Assert.Equal(JobState.Queued, job.State);
        Assert.Contains(events, evt => evt.State == JobState.Queued && evt.Message.Contains("Retry delay elapsed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WorkerCannotCompleteAnotherWorkersLeasedJob()
    {
        var harness = await SchedulerHarness.CreateAsync();
        await harness.SeedSchedulableWorkerAsync();
        await harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Render Main"), CancellationToken.None);
        var assignment = await harness.Scheduler.RequestJobAsync("worker-1", CancellationToken.None);
        Assert.NotNull(assignment.Job);
        Assert.NotNull(assignment.Lease);

        var result = await harness.Scheduler.CompleteJobAsync(
            assignment.Job.Id,
            new JobCompletionRequest(assignment.Lease.Id, "worker-2", 0, @"D:\renders\done"),
            CancellationToken.None);

        var job = await harness.Jobs.GetAsync(assignment.Job.Id, CancellationToken.None);
        Assert.Null(result);
        Assert.NotNull(job);
        Assert.Equal(JobState.Reserved, job.State);
        Assert.Single(await harness.Leases.ListActiveAsync(CancellationToken.None));
    }
    [Fact]
    public async Task FrameChunkingIsExplicitlyGated()
    {
        var harness = await SchedulerHarness.CreateAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Scheduler.CreateJobAsync(new CreateRenderJobRequest("project-1", "profile-1", "Chunked", ChunkSizeFrames: 100), CancellationToken.None));

        Assert.Contains("disabled", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingNotificationSink : IJobNotificationSink
    {
        public List<JobNotificationPayloadDto> Payloads { get; } = [];

        public Task NotifyTerminalAsync(JobNotificationPayloadDto payload, CancellationToken cancellationToken)
        {
            Payloads.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotificationSink : IJobNotificationSink
    {
        public Task NotifyTerminalAsync(JobNotificationPayloadDto payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated notification failure");
    }
    private sealed class SchedulerHarness
    {
        private SchedulerHarness(SqliteRenderFarmRepository repository, IJobScheduler scheduler)
        {
            Repository = repository;
            Scheduler = scheduler;
            Workers = repository;
            Projects = repository;
            Profiles = repository;
            Jobs = repository;
            Attempts = repository;
            Leases = repository;
            Events = repository;
            Settings = repository;
        }

        public SqliteRenderFarmRepository Repository { get; }
        public IJobScheduler Scheduler { get; }
        public IWorkerRepository Workers { get; }
        public IProjectRepository Projects { get; }
        public IRenderProfileRepository Profiles { get; }
        public IJobRepository Jobs { get; }
        public IJobAttemptRepository Attempts { get; }
        public IJobLeaseRepository Leases { get; }
        public IJobEventRepository Events { get; }
        public ISettingsRepository Settings { get; }

        public static async Task<SchedulerHarness> CreateAsync(int maxAttempts = 3, int retryDelaySeconds = 0, IJobNotificationSink? notificationSink = null)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), "rf_scheduler_tests", Guid.NewGuid().ToString("N"), "renderfarm.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            var repository = new SqliteRenderFarmRepository(Options.Create(new RenderFarmDatabaseOptions { ConnectionString = $"Data Source={databasePath}" }));
            await repository.InitializeAsync(CancellationToken.None);
            var schedulerOptions = Options.Create(new JobSchedulerOptions { LeaseSeconds = 30, MaxAttempts = maxAttempts, RetryDelaySeconds = retryDelaySeconds });
            var retryPolicy = new ConfiguredRetryPolicy(schedulerOptions);
            var scheduler = new JobScheduler(repository, repository, repository, repository, repository, repository, repository, repository, repository, retryPolicy, schedulerOptions, notificationSink);
            return new SchedulerHarness(repository, scheduler);
        }

        public async Task SeedSchedulableWorkerAsync(WorkerStatus status = WorkerStatus.Idle)
        {
            var project = new ProjectProfile(
                "project-1",
                "Demo Project",
                "D:\\Projects\\Demo\\Demo.uproject",
                "5.7",
                ["5.7"],
                null,
                [new WorkerProjectPath("path-1", "project-1", "worker-1", "C:\\Program Files\\Epic Games\\UE_5.7", "D:\\Projects\\Demo\\Demo.uproject", null)]);
            var profile = new RenderProfile("profile-1", "project-1", "Main Queue", RenderProfileType.MrqQueue, "/Game/MainQueue", null, "png", false, new Dictionary<string, string>());
            var capabilities = new WorkerCapabilities(
                16,
                64,
                "GPU",
                12,
                500,
                [new UnrealEngineInstallation("5.7", "C:\\Program Files\\Epic Games\\UE_5.7", "C:\\Program Files\\Epic Games\\UE_5.7\\Engine\\Binaries\\Win64\\UnrealEditor-Cmd.exe", true)],
                [new ProjectPathStatus("D:\\Projects\\Demo\\Demo.uproject", true)],
                [new SharedOutputStatus("\\\\server\\renders", true, true, 100)]);
            var worker = new RenderFarm.Domain.Worker("worker-1", "Worker 1", "host", "127.0.0.1", null, status, null, null, "test", capabilities, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

            await Projects.UpsertAsync(project, CancellationToken.None);
            await Profiles.UpsertAsync(profile, CancellationToken.None);
            await Workers.UpsertAsync(worker, CancellationToken.None);
        }
    }
}


