using System.Text.Json;
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

        group.MapPost("/validate", async (CreateRenderJobRequest request, IRenderJobValidator validator, CancellationToken ct) =>
        {
            if (EndpointValidation.ValidateCreateJob(request) is { } problem)
            {
                return problem;
            }

            return Results.Ok(await validator.ValidateFastAsync(request, ct));
        });
        group.MapPost("", async (CreateRenderJobRequest request, IJobScheduler scheduler, IJobRepository jobs, IRenderJobValidator validator, IActivityLog activity, CancellationToken ct) =>
        {
            if (EndpointValidation.ValidateCreateJob(request) is { } problem)
            {
                return problem;
            }

            var validation = await validator.ValidateFastAsync(request, ct);
            if (validation.Status == RenderValidationStatus.Blocked)
            {
                return Results.BadRequest(validation);
            }

            try
            {
                var job = await scheduler.CreateJobAsync(request, ct);
                var validatedJob = job with { ValidationJson = JsonSerializer.Serialize(validation, RenderFarmJson.SerializerOptions) };
                await jobs.UpsertAsync(validatedJob, ct);
                activity.Add("info", "Job", "Render queued", $"{validatedJob.Name} is waiting for a suitable worker.", jobId: validatedJob.Id, projectId: validatedJob.ProjectId, renderProfileId: validatedJob.RenderProfileId, actionRoute: "queue");
                return Results.Created($"/api/jobs/{validatedJob.Id}", validatedJob.ToDto());
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

        group.MapPost("/{jobId}/retry", async (string jobId, IJobRepository jobs, IJobScheduler scheduler, IActivityLog activity, CancellationToken ct) =>
        {
            var source = await jobs.GetAsync(jobId, ct);
            if (source is null)
            {
                return Results.NotFound();
            }

            if (source.State != JobState.Failed)
            {
                var message = source.State == JobState.RetryWait
                    ? "This job is already waiting for the configured retry delay. It will requeue automatically when QueuedAtUtc is due."
                    : source.State == JobState.Stale
                        ? "This job is stale and should be recovered by the controller lease recovery workflow before retrying."
                        : $"Only failed terminal jobs can be retried as a new job. Current state is {source.State}.";
                return Results.Conflict(new { error = message, state = source.State, queuedAtUtc = source.QueuedAtUtc });
            }

            try
            {
                var retry = await scheduler.RetryFailedJobAsNewAsync(jobId, ct);
                if (retry is null)
                {
                    return Results.NotFound();
                }

                activity.Add("info", "Job", "Retry queued as new job", $"{source.Name} was cloned into {retry.Id}.", jobId: retry.Id, projectId: retry.ProjectId, renderProfileId: retry.RenderProfileId, actionRoute: "queue");
                return Results.Created($"/api/jobs/{retry.Id}", retry);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message, state = source.State });
            }
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

    private static async Task<IResult?> ValidateQueueRequestAsync(
        CreateRenderJobRequest request,
        IProjectRepository projects,
        IRenderProfileRepository profiles,
        IWorkerRepository workers,
        ISettingsRepository settings,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        if (project is null)
        {
            errors[nameof(request.ProjectId)] = ["Project was not found."];
            return Results.ValidationProblem(errors);
        }

        var profile = await profiles.GetAsync(request.RenderProfileId, cancellationToken);
        if (profile is null || !string.Equals(profile.ProjectId, project.Id, StringComparison.OrdinalIgnoreCase))
        {
            errors[nameof(request.RenderProfileId)] = ["Render profile was not found for the selected project."];
            return Results.ValidationProblem(errors);
        }

        var defaults = await ControllerRenderDefaults.LoadAsync(settings, cancellationToken);
        var workerList = await workers.ListAsync(cancellationToken);
        var approvals = await WorkerApproval.ListAsync(settings, cancellationToken);
        var schedulingModes = await WorkerScheduling.ListAsync(settings, cancellationToken);

        var projectPath = FirstNonEmpty(GetSetting(profile, "projectPath"), GetSetting(profile, "uprojectPath"), project.UProjectPath, project.WorkerPaths.Select(path => path.ProjectPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)));
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            errors["ProjectPath"] = ["Project path is required before queueing. Set the project .uproject path or a profile projectPath override."];
        }
        else if (!projectPath.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) || !File.Exists(Environment.ExpandEnvironmentVariables(projectPath.Trim().Trim('"'))))
        {
            errors["ProjectPath"] = [$"Project .uproject file was not found: {projectPath}"];
        }

        var unrealPath = FirstNonEmpty(
            GetSetting(profile, "unrealExecutablePath"),
            GetSetting(profile, "unrealExe"),
            GetSetting(profile, "unrealCommand"),
            GetSetting(profile, "unrealSearchRoot"),
            defaults.UnrealExecutablePath,
            defaults.UnrealSearchRoot,
            project.WorkerPaths.Select(path => path.EnginePath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)),
            workerList.SelectMany(worker => worker.Capabilities.UnrealInstallations).FirstOrDefault(install => install.Exists)?.ExecutablePath);
        if (string.IsNullOrWhiteSpace(unrealPath))
        {
            errors["UnrealExecutable"] = ["Unreal executable or search root is required before queueing. Configure Controller Render Defaults or a render-profile override."];
        }

        var outputRoot = FirstNonEmpty(
            request.OutputDirectory,
            GetSetting(profile, "outputDirectory"),
            GetSetting(profile, "defaultOutputDirectory"),
            GetSetting(profile, "defaultOutputRoot"),
            GetSetting(profile, "outputRoot"),
            defaults.SharedOutputRoot,
            workerList.SelectMany(worker => worker.Capabilities.SharedOutputRoots).FirstOrDefault(root => root.Exists && root.Writable)?.Path);
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            errors["OutputRoot"] = ["Shared output root is required before queueing. Configure Controller Render Defaults or select an output folder."];
        }
        else if (!CanWriteOutputDirectory(outputRoot, out var outputError))
        {
            errors["OutputRoot"] = [outputError];
        }

        AddRenderProfileValidation(errors, profile);


        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static void AddRenderProfileValidation(IDictionary<string, string[]> errors, RenderProfile profile)
    {
        if (profile.Type == RenderProfileType.CommandTemplate && string.IsNullOrWhiteSpace(profile.CommandTemplate))
        {
            errors["RenderProfile.CommandTemplate"] = ["Command template profiles require a command template."];
        }

        if (profile.Type is not (RenderProfileType.MrqQueue or RenderProfileType.MrgGraph))
        {
            return;
        }

        var mapPath = FirstNonEmpty(GetSetting(profile, "map"), GetSetting(profile, "mapName"), GetSetting(profile, "level"), GetSetting(profile, "levelName"));
        var sequencePath = FirstNonEmpty(GetSetting(profile, "sequence"), GetSetting(profile, "levelSequence"));
        var configPath = FirstNonEmpty(profile.AssetPath, GetSetting(profile, "moviePipelineConfig"), GetSetting(profile, "mrqConfig"), GetSetting(profile, "queue"));
        var launchMode = ResolveMovieRenderLaunchMode(profile, sequencePath);

        AddPathValidation(errors, "RenderProfile.Map", mapPath, UnrealAssetPathKind.WorldPackagePath, "Map/world argument is required and must look like Minimal_Default1 or /Game/Maps/MainMap.");

        if (launchMode == MovieRenderLaunchMode.SingleSequence)
        {
            AddPathValidation(errors, "RenderProfile.Sequence", sequencePath, UnrealAssetPathKind.LevelSequenceObjectPath, "Single Sequence MRQ mode requires a Level Sequence object path such as /Game/Cinematics/Seq01.Seq01.");
        }
        else if (!string.IsNullOrWhiteSpace(sequencePath) && HasExplicitMovieRenderLaunchMode(profile))
        {
            errors["RenderProfile.Sequence"] = ["Saved MRQ Queue mode should not set a separate Level Sequence. Remove the sequence field, or switch the profile to Single Sequence + Config mode."];
        }

        var configKind = launchMode == MovieRenderLaunchMode.SavedQueue
            ? UnrealAssetPathKind.MoviePipelineQueueObjectPath
            : UnrealAssetPathKind.MoviePipelineConfigObjectPath;
        var configMessage = launchMode == MovieRenderLaunchMode.SavedQueue
            ? "Saved MRQ Queue mode requires a queue asset path such as /Game/Cinematics/myRenderQueue."
            : "Single Sequence MRQ mode requires a config preset path such as /Game/RenderConfig.RenderConfig.";
        AddPathValidation(errors, "RenderProfile.Config", configPath, configKind, configMessage);
    }

    private static void AddPathValidation(IDictionary<string, string[]> errors, string key, string? value, UnrealAssetPathKind kind, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = [missingMessage];
            return;
        }

        var result = UnrealPathNormalizer.TryNormalizeUnrealReference(value, kind);
        if (!result.Success)
        {
            errors[key] = [result.Error ?? missingMessage];
        }
    }


    private enum MovieRenderLaunchMode
    {
        SavedQueue,
        SingleSequence
    }

    private static MovieRenderLaunchMode ResolveMovieRenderLaunchMode(RenderProfile profile, string? levelSequence)
    {
        var raw = GetMovieRenderLaunchModeValue(profile);
        if (IsSingleSequenceMode(raw))
        {
            return MovieRenderLaunchMode.SingleSequence;
        }

        if (IsQueueMode(raw))
        {
            return MovieRenderLaunchMode.SavedQueue;
        }

        return string.IsNullOrWhiteSpace(levelSequence)
            ? MovieRenderLaunchMode.SavedQueue
            : MovieRenderLaunchMode.SingleSequence;
    }

    private static bool HasExplicitMovieRenderLaunchMode(RenderProfile profile) =>
        !string.IsNullOrWhiteSpace(GetMovieRenderLaunchModeValue(profile));

    private static string GetMovieRenderLaunchModeValue(RenderProfile profile) =>
        FirstNonEmpty(
            GetSetting(profile, "mrqMode"),
            GetSetting(profile, "renderMode"),
            GetSetting(profile, "movieRenderMode"),
            GetSetting(profile, "moviePipelineMode"),
            GetSetting(profile, "launchMode"));

    private static bool IsSingleSequenceMode(string? value)
    {
        var key = NormalizeModeKey(value);
        return key is "single" or "singlesequence" or "sequence" or "levelsequence" or "config" or "configpreset" or "sequenceconfig" or "singlelevelsequence";
    }

    private static bool IsQueueMode(string? value)
    {
        var key = NormalizeModeKey(value);
        return key is "queue" or "savedqueue" or "queuepreset" or "mrqqueue" or "moviepipelinequeue";
    }

    private static string NormalizeModeKey(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool CanWriteOutputDirectory(string path, out string error)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            Directory.CreateDirectory(expanded);
            var probe = Path.Combine(expanded, $".renderfarm_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "renderfarm output probe");
            File.Delete(probe);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            error = $"Output directory is not writable: {ex.Message}";
            return false;
        }
    }

    private static string? GetSetting(RenderProfile profile, string key) =>
        profile.Settings.TryGetValue(key, out var exact)
            ? exact
            : profile.Settings.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    private static string BuildChunkOutputHint(string? outputDirectory, RenderChunkRange chunk)
    {
        var chunkName = $"chunk_{chunk.ChunkIndex + 1:0000}_frames_{chunk.FrameStart}_{chunk.FrameEnd}";
        return string.IsNullOrWhiteSpace(outputDirectory)
            ? chunkName
            : $"{outputDirectory.TrimEnd('\\', '/')}/{chunkName}";
    }
}






