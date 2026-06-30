using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").WithTags("Projects");

        group.MapGet("", async (IProjectRepository projects, CancellationToken ct) =>
            Results.Ok((await projects.ListAsync(ct)).Select(x => x.ToDto())));

        group.MapGet("/{projectId}", async (string projectId, IProjectRepository projects, CancellationToken ct) =>
            await projects.GetAsync(projectId, ct) is { } project ? Results.Ok(project.ToDto()) : Results.NotFound());

        group.MapGet("/{projectId}/readiness", async (string projectId, string? renderProfileId, IProjectRepository projects, IRenderProfileRepository profiles, IWorkerRepository workers, CancellationToken ct) =>
        {
            var project = await projects.GetAsync(projectId, ct);
            if (project is null)
            {
                return Results.NotFound();
            }

            var profile = string.IsNullOrWhiteSpace(renderProfileId) ? null : await profiles.GetAsync(renderProfileId, ct);
            if (!string.IsNullOrWhiteSpace(renderProfileId) && profile is null)
            {
                return Results.NotFound(new { error = "Render profile was not found." });
            }

            var matrix = new ReadinessMatrixDto(project.Id, profile?.Id, (await workers.ListAsync(ct)).Select(worker => WorkerReadinessEvaluator.Evaluate(worker, project, profile)).ToArray());
            return Results.Ok(matrix);
        });

        group.MapGet("/{projectId}/validate/worker/{workerId}", async (string projectId, string workerId, string? renderProfileId, IProjectRepository projects, IRenderProfileRepository profiles, IWorkerRepository workers, CancellationToken ct) =>
        {
            var project = await projects.GetAsync(projectId, ct);
            var worker = await workers.GetAsync(workerId, ct);
            if (project is null || worker is null)
            {
                return Results.NotFound();
            }

            var profile = string.IsNullOrWhiteSpace(renderProfileId) ? null : await profiles.GetAsync(renderProfileId, ct);
            return Results.Ok(WorkerReadinessEvaluator.Evaluate(worker, project, profile));
        });

        group.MapPost("/{projectId}/scan", async (string projectId, UnrealProjectScanRequest request, IProjectRepository projects, IUnrealProjectScanner scanner, CancellationToken ct) =>
        {
            var project = await projects.GetAsync(projectId, ct);
            if (project is null)
            {
                return Results.NotFound();
            }

            var projectPath = request.ProjectPath ?? project.UProjectPath;
            var result = await scanner.ScanAsync(projectPath ?? string.Empty, request.UseUnrealBridge, request.TimeoutSeconds, ct);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        group.MapPost("", async (ProjectProfileDto dto, IProjectRepository projects, IActivityLog activity, CancellationToken ct) =>
        {
            if (EndpointValidation.ValidateProject(dto) is { } problem)
            {
                return problem;
            }

            await projects.UpsertAsync(dto.ToDomain(), ct);
            activity.Add("success", "Project", "Project saved", $"Project {dto.DisplayName} is available for render setup.", projectId: dto.Id, actionRoute: "projects");
            return Results.Created($"/api/projects/{dto.Id}", dto);
        });

        group.MapPut("/{projectId}", async (string projectId, ProjectProfileDto dto, IProjectRepository projects, IActivityLog activity, CancellationToken ct) =>
        {
            var project = dto with { Id = projectId };
            if (EndpointValidation.ValidateProject(project) is { } problem)
            {
                return problem;
            }

            await projects.UpsertAsync(project.ToDomain(), ct);
            activity.Add("success", "Project", "Project updated", $"Project {project.DisplayName} was updated.", projectId: project.Id, actionRoute: "projects");
            return Results.Ok(project);
        });

        group.MapDelete("/{projectId}", async (string projectId, bool? force, IProjectRepository projects, IRenderProfileRepository profiles, IJobRepository jobs, CancellationToken ct) =>
        {
            if ((force ?? false) is false && (await jobs.ListAsync(ct)).Any(job => string.Equals(job.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Conflict(new { error = "Project has existing jobs. Pass ?force=true only after deciding those historical jobs may keep dangling references." });
            }

            foreach (var profile in await profiles.ListForProjectAsync(projectId, ct))
            {
                await profiles.DeleteAsync(profile.Id, ct);
            }

            await projects.DeleteAsync(projectId, ct);
            return Results.Ok(new { deleted = projectId, forced = force ?? false });
        });

        group.MapGet("/export", async (IProjectRepository projects, IRenderProfileRepository profiles, CancellationToken ct) =>
            Results.Ok(new ProjectProfileImportExportDto((await projects.ListAsync(ct)).Select(x => x.ToDto()).ToArray(), (await profiles.ListAsync(ct)).Select(x => x.ToDto()).ToArray())));

        group.MapPost("/import", async (ProjectProfileImportExportDto import, IProjectRepository projects, IRenderProfileRepository profiles, CancellationToken ct) =>
        {
            foreach (var project in import.Projects)
            {
                if (EndpointValidation.ValidateProject(project) is { } problem)
                {
                    return problem;
                }

                await projects.UpsertAsync(project.ToDomain(), ct);
            }

            foreach (var profile in import.RenderProfiles)
            {
                if (EndpointValidation.ValidateRenderProfile(profile) is { } problem)
                {
                    return problem;
                }

                await profiles.UpsertAsync(profile.ToDomain(), ct);
            }

            return Results.Ok(new { projects = import.Projects.Count, renderProfiles = import.RenderProfiles.Count });
        });

        return app;
    }
}

public sealed record ProjectProfileImportExportDto(IReadOnlyList<ProjectProfileDto> Projects, IReadOnlyList<RenderProfileDto> RenderProfiles);


