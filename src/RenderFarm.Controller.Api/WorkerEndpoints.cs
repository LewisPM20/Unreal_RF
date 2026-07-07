using RenderFarm.Domain;
using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public static class WorkerEndpoints
{
    public static IEndpointRouteBuilder MapWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workers").WithTags("Workers");

        group.MapGet("", async (IWorkerRepository workers, CancellationToken ct) =>
            Results.Ok((await workers.ListAsync(ct)).Select(x => x.ToDto())));

        group.MapGet("/registered", async (IWorkerRepository workers, CancellationToken ct) =>
            Results.Ok((await workers.ListAsync(ct)).Select(x => x.ToDto())));

        group.MapGet("/status", async (IWorkerRepository workers, ISettingsRepository settings, CancellationToken ct) =>
            Results.Ok(await BuildWorkerStatusAsync(workers, settings, ct)));

        group.MapPost("/rescan", async (IWorkerRepository workers, ISettingsRepository settings, CancellationToken ct) =>
            Results.Ok(await BuildWorkerStatusAsync(workers, settings, ct)));

        group.MapGet("/{workerId}", async (string workerId, IWorkerRepository workers, CancellationToken ct) =>
            await workers.GetAsync(workerId, ct) is { } worker ? Results.Ok(worker.ToDto()) : Results.NotFound());

        group.MapPost("/heartbeat", async (WorkerHeartbeatDto heartbeat, IWorkerRepository workers, ISettingsRepository settings, IActivityLog activity, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(heartbeat.WorkerId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(heartbeat.WorkerId)] = ["WorkerId is required."] });
            }

            var receivedAtUtc = DateTimeOffset.UtcNow;
            var existing = await workers.GetAsync(heartbeat.WorkerId, ct);
            var approval = await WorkerApproval.GetAsync(settings, heartbeat.WorkerId, ct);
            if (approval == WorkerApproval.Pending && existing is null)
            {
                await WorkerApproval.SetAsync(settings, heartbeat.WorkerId, WorkerApproval.Pending, ct);
            }

            var reported = heartbeat.ToWorker(receivedAtUtc);
            var compatibility = RenderFarmVersion.EvaluateWorkerAgent(reported.AgentVersion);
            var status = approval switch
            {
                WorkerApproval.Accepted when !compatibility.Compatible => WorkerStatus.IncompatibleVersion,
                WorkerApproval.Accepted => reported.Status,
                WorkerApproval.Rejected => WorkerStatus.Rejected,
                _ => WorkerStatus.Pending
            };
            var worker = reported with
            {
                Status = status,
                LastError = compatibility.Compatible ? reported.LastError : compatibility.Reason,
                RegisteredAtUtc = existing?.RegisteredAtUtc ?? receivedAtUtc
            };
            await workers.UpsertAsync(worker, ct);
            if (existing is null)
            {
                activity.Add(
                    approval == WorkerApproval.Accepted && compatibility.Compatible ? "success" : "warning",
                    "Worker",
                    approval == WorkerApproval.Accepted && compatibility.Compatible ? "Worker connected" : approval == WorkerApproval.Accepted ? "Worker incompatible" : "Worker awaiting approval",
                    compatibility.Compatible ? $"{worker.Name} reported from {worker.Hostname ?? worker.IpAddress ?? "unknown host"}." : compatibility.Reason,
                    workerId: worker.Id,
                    actionRoute: "workers");
            }
            else if (!compatibility.Compatible && existing.Status != WorkerStatus.IncompatibleVersion)
            {
                activity.Add("warning", "Worker", "Worker incompatible", compatibility.Reason, workerId: worker.Id, actionRoute: "workers");
            }

            var schedulingMode = await WorkerScheduling.GetAsync(settings, heartbeat.WorkerId, ct);
            return Results.Ok(new
            {
                accepted = approval == WorkerApproval.Accepted && compatibility.Compatible,
                approval,
                scheduling_mode = schedulingMode,
                worker_id = heartbeat.WorkerId,
                received_at_utc = receivedAtUtc,
                controller_version = RenderFarmVersion.ProductVersion,
                required_worker_version = RenderFarmVersion.MinimumWorkerProductVersion,
                protocol_version = RenderFarmVersion.ProtocolVersion,
                compatibility
            });
        });

        group.MapGet("/pending", async (IWorkerRepository workers, ISettingsRepository settings, CancellationToken ct) =>
        {
            var approvals = await WorkerApproval.ListAsync(settings, ct);
            var result = await workers.ListAsync(ct);
            return Results.Ok(result.Where(worker => WorkerApproval.GetEffective(approvals, worker) == WorkerApproval.Pending).Select(x => x.ToDto()));
        });

        group.MapPost("/{workerId}/approve", async (string workerId, IWorkerRepository workers, ISettingsRepository settings, IActivityLog activity, CancellationToken ct) =>
        {
            await WorkerApproval.SetAsync(settings, workerId, WorkerApproval.Accepted, ct);
            if (await workers.GetAsync(workerId, ct) is { } worker)
            {
                var compatibility = RenderFarmVersion.EvaluateWorkerAgent(worker.AgentVersion);
                var status = !compatibility.Compatible
                    ? WorkerStatus.IncompatibleVersion
                    : worker.Status is WorkerStatus.Pending or WorkerStatus.Rejected or WorkerStatus.Unknown ? WorkerStatus.Idle : worker.Status;
                await workers.UpsertAsync(worker with { Status = status, LastError = compatibility.Compatible ? worker.LastError : compatibility.Reason }, ct);
            }

            activity.Add("success", "Approval", "Worker approved", $"Worker {workerId} can now receive jobs.", workerId: workerId, actionRoute: "workers");
            return Results.Ok(new { worker_id = workerId, approval = WorkerApproval.Accepted });
        });

        group.MapPost("/{workerId}/reject", async (string workerId, IWorkerRepository workers, ISettingsRepository settings, IActivityLog activity, CancellationToken ct) =>
        {
            await WorkerApproval.SetAsync(settings, workerId, WorkerApproval.Rejected, ct);
            if (await workers.GetAsync(workerId, ct) is { } worker)
            {
                await workers.UpsertAsync(worker with { Status = WorkerStatus.Rejected }, ct);
            }

            activity.Add("warning", "Approval", "Worker rejected", $"Worker {workerId} was blocked from scheduling.", workerId: workerId, actionRoute: "workers");
            return Results.Ok(new { worker_id = workerId, approval = WorkerApproval.Rejected });
        });

        group.MapPost("/{workerId}/scheduling", async (string workerId, WorkerSchedulingModeRequest request, IWorkerRepository workers, ISettingsRepository settings, CancellationToken ct) =>
        {
            if (await workers.GetAsync(workerId, ct) is null)
            {
                return Results.NotFound();
            }

            await WorkerScheduling.SetAsync(settings, workerId, request.Mode, ct);
            return Results.Ok(new { worker_id = workerId, scheduling_mode = request.Mode });
        });

        group.MapGet("/{workerId}/readiness", async (string workerId, string projectId, string? renderProfileId, IWorkerRepository workers, IProjectRepository projects, IRenderProfileRepository profiles, ISettingsRepository settings, CancellationToken ct) =>
        {
            var worker = await workers.GetAsync(workerId, ct);
            var project = await projects.GetAsync(projectId, ct);
            if (worker is null || project is null)
            {
                return Results.NotFound();
            }

            var profile = string.IsNullOrWhiteSpace(renderProfileId) ? null : await profiles.GetAsync(renderProfileId, ct);
            var defaults = await ControllerRenderDefaults.LoadAsync(settings, ct);
            return Results.Ok(WorkerReadinessEvaluator.Evaluate(worker, project, profile, defaults: defaults));
        });

        group.MapGet("/{workerId}/validate-project-path", async (string workerId, string path, IWorkerRepository workers, CancellationToken ct) =>
        {
            var worker = await workers.GetAsync(workerId, ct);
            if (worker is null)
            {
                return Results.NotFound();
            }

            var reported = worker.Capabilities.ProjectPaths.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(new { workerId, path, ok = reported?.Exists ?? false, message = reported is null ? "Worker has not reported this path." : reported.Exists ? "Worker reports path exists." : "Worker reports path missing." });
        });

        group.MapGet("/{workerId}/validate-engine", async (string workerId, string version, IWorkerRepository workers, CancellationToken ct) =>
        {
            var worker = await workers.GetAsync(workerId, ct);
            if (worker is null)
            {
                return Results.NotFound();
            }

            var install = worker.Capabilities.UnrealInstallations.FirstOrDefault(x => x.Exists && string.Equals(x.Version, version, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(new { workerId, version, ok = install is not null, executablePath = install?.ExecutablePath, message = install is null ? "Worker has not reported this Unreal version." : "Worker reports compatible Unreal executable." });
        });

        group.MapGet("/{workerId}/validate-output-root", async (string workerId, string path, IWorkerRepository workers, CancellationToken ct) =>
        {
            var worker = await workers.GetAsync(workerId, ct);
            if (worker is null)
            {
                return Results.NotFound();
            }

            var output = worker.Capabilities.SharedOutputRoots.FirstOrDefault(x => path.StartsWith(x.Path, StringComparison.OrdinalIgnoreCase) || x.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(new { workerId, path, ok = output?.Writable == true, exists = output?.Exists, writable = output?.Writable, freeDiskGb = output?.FreeDiskGb, message = output?.Message ?? "Worker has not reported this output root." });
        });

        group.MapPost("/{workerId}/request-job", async (string workerId, IJobScheduler scheduler, CancellationToken ct) =>
        {
            var assignment = await scheduler.RequestJobAsync(workerId, ct);
            return assignment.Assigned ? Results.Ok(assignment) : Results.NoContent();
        });

        return app;
    }

    private static async Task<IEnumerable<object>> BuildWorkerStatusAsync(IWorkerRepository workers, ISettingsRepository settings, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var result = await workers.ListAsync(ct);
        var approvals = await WorkerApproval.ListAsync(settings, ct);
        var schedulingModes = await WorkerScheduling.ListAsync(settings, ct);
        return result.Select(worker =>
        {
            var dto = worker.ToDto();
            var effectiveStatus = WorkerStatusProjection.GetEffectiveWorkerStatus(worker, now);
            var approval = WorkerApproval.GetEffective(approvals, worker);
            return new
            {
                dto.Id,
                dto.Name,
                dto.Hostname,
                dto.IpAddress,
                dto.ServiceUrl,
                dto.Status,
                EffectiveStatus = effectiveStatus,
                Approval = approval,
                SchedulingMode = WorkerScheduling.GetEffective(schedulingModes, worker),
                dto.Stage,
                dto.CurrentJobId,
                dto.AgentVersion,
                dto.ProductVersion,
                dto.ProtocolVersion,
                dto.ApiContractVersion,
                dto.BuildId,
                dto.Compatibility,
                dto.Capabilities,
                dto.LastError,
                dto.RegisteredAtUtc,
                dto.LastHeartbeatUtc,
                SecondsSinceHeartbeat = Math.Max(0, (int)(now - dto.LastHeartbeatUtc).TotalSeconds)
            };
        });
    }
}






