using RenderFarm.Domain;
using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs").WithTags("Jobs");

        group.MapGet("", async (IJobRepository jobs, CancellationToken ct) =>
            Results.Ok((await jobs.ListAsync(ct)).Select(x => x.ToDto())));

        group.MapDelete("", async (SqliteRenderFarmRepository repository, CancellationToken ct) =>
        {
            var cleared = await repository.ClearJobsAsync(ct);
            return Results.Ok(new { cleared, message = "Cleared jobs, attempts, leases, and events. Projects, render profiles, workers, and settings were kept." });
        });

        group.MapGet("/groups", async (IJobRepository jobs, CancellationToken ct) =>
        {
            var result = await jobs.ListAsync(ct);
            return Results.Ok(result.GroupBy(x => x.State).Select(grouping => new { state = grouping.Key, count = grouping.Count(), jobs = grouping.Select(x => x.ToDto()) }));
        });

        group.MapGet("/{jobId}", async (string jobId, IJobRepository jobs, CancellationToken ct) =>
            await jobs.GetAsync(jobId, ct) is { } job ? Results.Ok(job.ToDto()) : Results.NotFound());

        group.MapPost("/chunk-preview", async (ChunkPreviewRequest request, IProjectRepository projects, IRenderProfileRepository profiles, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProjectId) || string.IsNullOrWhiteSpace(request.RenderProfileId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.ProjectId)] = ["ProjectId is required."],
                    [nameof(request.RenderProfileId)] = ["RenderProfileId is required."]
                });
            }

            if (await projects.GetAsync(request.ProjectId, ct) is null)
            {
                return Results.NotFound(new { error = "Project was not found." });
            }

            if (await profiles.GetAsync(request.RenderProfileId, ct) is null)
            {
                return Results.NotFound(new { error = "Render profile was not found." });
            }

            try
            {
                var chunks = RenderChunkPlanner.Plan(request.FrameStart, request.FrameEnd, request.ChunkSizeFrames)
                    .Select(chunk => new ChunkPreviewItemDto(
                        chunk.ChunkIndex,
                        chunk.FrameStart,
                        chunk.FrameEnd,
                        chunk.TotalChunks,
                        BuildChunkOutputHint(request.OutputDirectory, chunk)))
                    .ToArray();
                return Results.Ok(new ChunkPreviewResponseDto(request.ProjectId, request.RenderProfileId, request.OutputDirectory, chunks));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("", async (CreateRenderJobRequest request, IJobScheduler scheduler, IActivityLog activity, CancellationToken ct) =>
        {
            if (EndpointValidation.ValidateCreateJob(request) is { } problem)
            {
                return problem;
            }

            try
            {
                var job = await scheduler.CreateJobAsync(request, ct);
                activity.Add("info", "Job", "Render queued", $"{job.Name} is waiting for a suitable worker.", jobId: job.Id, projectId: job.ProjectId, renderProfileId: job.RenderProfileId, actionRoute: "queue");
                return Results.Created($"/api/jobs/{job.Id}", job.ToDto());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPut("/{jobId}", async (string jobId, RenderJobDto dto, IJobRepository jobs, CancellationToken ct) =>
        {
            var existing = await jobs.GetAsync(jobId, ct);
            if (existing is not null && !JobStateMachine.CanTransition(existing.State, dto.State))
            {
                return Results.Conflict(new { error = $"Illegal job state transition from {existing.State} to {dto.State}." });
            }

            var job = dto.ToDomain() with { Id = jobId, UpdatedAtUtc = DateTimeOffset.UtcNow };
            await jobs.UpsertAsync(job, ct);
            return Results.Ok(job.ToDto());
        });

        group.MapPost("/{jobId}/cancel", async (string jobId, IJobRepository jobs, IJobEventRepository events, IActivityLog activity, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(jobId, ct);
            if (job is null)
            {
                return Results.NotFound();
            }

            if (JobStateMachine.IsTerminal(job.State))
            {
                return Results.Ok(job.ToDto());
            }

            if (!JobStateMachine.CanTransition(job.State, JobState.CancelRequested))
            {
                return Results.Conflict(new { error = $"Job in state {job.State} cannot be cancelled." });
            }

            var updated = job with { CancellationRequested = true, State = JobState.CancelRequested, UpdatedAtUtc = DateTimeOffset.UtcNow };
            await jobs.UpsertAsync(updated, ct);
            await events.AppendAsync(new JobEvent(Guid.NewGuid().ToString("N"), jobId, null, null, JobState.CancelRequested, FailureCategory.CancelledByUser, "Cancellation requested from dashboard/API", DateTimeOffset.UtcNow, null), ct);
            activity.Add("warning", "Job", "Cancellation requested", $"{updated.Name} is being cancelled.", jobId: updated.Id, projectId: updated.ProjectId, renderProfileId: updated.RenderProfileId, actionRoute: "queue");
            return Results.Ok(updated.ToDto());
        });

        group.MapPost("/{jobId}/retry", async (string jobId, IJobRepository jobs, IJobEventRepository events, IActivityLog activity, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(jobId, ct);
            if (job is null)
            {
                return Results.NotFound();
            }

            if (!JobStateMachine.CanTransition(job.State, JobState.Queued))
            {
                return Results.Conflict(new { error = $"Job in state {job.State} cannot be requeued directly." });
            }

            var updated = job with { State = JobState.Queued, AssignedWorkerId = null, CancellationRequested = false, FailureCategory = FailureCategory.None, Error = null, QueuedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow, FinishedAtUtc = null };
            await jobs.UpsertAsync(updated, ct);
            await events.AppendAsync(new JobEvent(Guid.NewGuid().ToString("N"), jobId, null, null, JobState.Queued, FailureCategory.None, "Job manually requeued", DateTimeOffset.UtcNow, null), ct);
            activity.Add("info", "Job", "Render requeued", $"{updated.Name} was returned to the queue.", jobId: updated.Id, projectId: updated.ProjectId, renderProfileId: updated.RenderProfileId, actionRoute: "queue");
            return Results.Ok(updated.ToDto());
        });

        group.MapPost("/{jobId}/renew-lease", async (string jobId, JobLeaseRenewalRequest request, IJobScheduler scheduler, CancellationToken ct) =>
            await scheduler.RenewLeaseAsync(jobId, request, ct) is { } lease ? Results.Ok(lease) : Results.Conflict(new { error = "Lease is not active, does not match this worker, or has expired." }));

        group.MapPost("/{jobId}/start", async (string jobId, JobStartRequest request, IJobScheduler scheduler, IActivityLog activity, CancellationToken ct) =>
        {
            var job = await scheduler.StartJobAsync(jobId, request, ct);
            if (job is null)
            {
                return Results.Conflict(new { error = "Job could not be started with the supplied lease." });
            }

            activity.Add("info", "Job", "Render started", $"{job.Name} started on worker {request.WorkerId}.", workerId: request.WorkerId, jobId: job.Id, projectId: job.ProjectId, renderProfileId: job.RenderProfileId, actionRoute: "queue");
            return Results.Ok(job);
        });

        group.MapPost("/{jobId}/complete", async (string jobId, JobCompletionRequest request, IJobScheduler scheduler, IActivityLog activity, CancellationToken ct) =>
        {
            var job = await scheduler.CompleteJobAsync(jobId, request, ct);
            if (job is null)
            {
                return Results.Conflict(new { error = "Job could not be completed with the supplied lease." });
            }

            activity.Add("success", "Job", "Render completed", request.OutputDirectory is null ? $"{job.Name} completed successfully." : $"{job.Name} completed: {request.OutputDirectory}", workerId: request.WorkerId, jobId: job.Id, projectId: job.ProjectId, renderProfileId: job.RenderProfileId, actionRoute: "queue");
            return Results.Ok(job);
        });

        group.MapPost("/{jobId}/fail", async (string jobId, JobFailureRequest request, IJobScheduler scheduler, IActivityLog activity, CancellationToken ct) =>
        {
            var job = await scheduler.FailJobAsync(jobId, request, ct);
            if (job is null)
            {
                return Results.Conflict(new { error = "Job could not be failed with the supplied lease." });
            }

            var severity = job.State == JobState.Queued ? "warning" : "error";
            activity.Add(severity, "Job", job.State == JobState.Queued ? "Render failed; retry scheduled" : "Render failed", $"{job.Name}: {request.FailureCategory} - {request.Error}", workerId: request.WorkerId, jobId: job.Id, projectId: job.ProjectId, renderProfileId: job.RenderProfileId, actionRoute: "queue");
            return Results.Ok(job);
        });

        group.MapPost("/expire-leases", async (IJobScheduler scheduler, CancellationToken ct) =>
            Results.Ok(new { expired = await scheduler.ExpireLeasesAsync(ct) }));

        group.MapGet("/{jobId}/attempts", async (string jobId, IJobAttemptRepository attempts, CancellationToken ct) =>
            Results.Ok((await attempts.ListForJobAsync(jobId, ct)).Select(x => x.ToDto())));

        group.MapPost("/{jobId}/attempts", async (string jobId, JobAttemptDto dto, IJobAttemptRepository attempts, CancellationToken ct) =>
        {
            var attempt = dto.ToDomain() with { JobId = jobId };
            await attempts.UpsertAsync(attempt, ct);
            return Results.Created($"/api/jobs/{jobId}/attempts/{attempt.Id}", attempt.ToDto());
        });

        group.MapGet("/{jobId}/events", async (string jobId, IJobEventRepository events, CancellationToken ct) =>
            Results.Ok((await events.ListForJobAsync(jobId, ct)).Select(x => x.ToDto())));

        group.MapPost("/{jobId}/events", async (string jobId, JobEventDto dto, IJobEventRepository events, CancellationToken ct) =>
        {
            var evt = dto.ToDomain() with { JobId = jobId, CreatedAtUtc = dto.CreatedAtUtc == default ? DateTimeOffset.UtcNow : dto.CreatedAtUtc };
            await events.AppendAsync(evt, ct);
            return Results.Created($"/api/jobs/{jobId}/events/{evt.Id}", evt.ToDto());
        });

        app.MapGet("/api/events", async (int? limit, IJobRepository jobs, IJobEventRepository events, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var all = new List<JobEventDto>();
            foreach (var job in await jobs.ListAsync(ct))
            {
                all.AddRange((await events.ListForJobAsync(job.Id, ct)).Select(x => x.ToDto()));
            }
            return Results.Ok(all.OrderByDescending(x => x.CreatedAtUtc).Take(take));
        }).WithTags("Jobs");

        return app;
    }

    private static string BuildChunkOutputHint(string? outputDirectory, RenderChunkRange chunk)
    {
        var chunkName = $"chunk_{chunk.ChunkIndex + 1:0000}_frames_{chunk.FrameStart}_{chunk.FrameEnd}";
        return string.IsNullOrWhiteSpace(outputDirectory)
            ? chunkName
            : $"{outputDirectory.TrimEnd('\\', '/')}/{chunkName}";
    }
}



