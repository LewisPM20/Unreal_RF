using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public static class RenderProfileEndpoints
{
    public static IEndpointRouteBuilder MapRenderProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/render-profiles").WithTags("RenderProfiles");

        group.MapGet("", async (IRenderProfileRepository profiles, CancellationToken ct) =>
            Results.Ok((await profiles.ListAsync(ct)).Select(x => x.ToDto())));

        app.MapGet("/api/projects/{projectId}/render-profiles", async (string projectId, IRenderProfileRepository profiles, CancellationToken ct) =>
            Results.Ok((await profiles.ListForProjectAsync(projectId, ct)).Select(x => x.ToDto()))).WithTags("RenderProfiles");

        group.MapGet("/{profileId}", async (string profileId, IRenderProfileRepository profiles, CancellationToken ct) =>
            await profiles.GetAsync(profileId, ct) is { } profile ? Results.Ok(profile.ToDto()) : Results.NotFound());

        group.MapPost("/{profileId}/duplicate", async (string profileId, DuplicateRenderProfileRequest request, IRenderProfileRepository profiles, IActivityLog activity, CancellationToken ct) =>
        {
            var source = await profiles.GetAsync(profileId, ct);
            if (source is null)
            {
                return Results.NotFound();
            }

            var copy = source with
            {
                Id = string.IsNullOrWhiteSpace(request.NewId) ? Guid.NewGuid().ToString("N") : request.NewId.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? source.DisplayName + " Copy" : request.DisplayName.Trim()
            };
            await profiles.UpsertAsync(copy, ct);
            activity.Add("success", "Render Setup", "Render setup duplicated", $"Render setup {copy.DisplayName} was created from {source.DisplayName}.", projectId: copy.ProjectId, renderProfileId: copy.Id, actionRoute: "projects");
            return Results.Created($"/api/render-profiles/{copy.Id}", copy.ToDto());
        });

        group.MapPost("", async (RenderProfileDto dto, IRenderProfileRepository profiles, IActivityLog activity, CancellationToken ct) =>
        {
            if (EndpointValidation.ValidateRenderProfile(dto) is { } problem)
            {
                return problem;
            }

            await profiles.UpsertAsync(dto.ToDomain(), ct);
            activity.Add("success", "Render Setup", "Render setup saved", $"Render setup {dto.DisplayName} is available for queueing.", projectId: dto.ProjectId, renderProfileId: dto.Id, actionRoute: "projects");
            return Results.Created($"/api/render-profiles/{dto.Id}", dto);
        });

        group.MapPut("/{profileId}", async (string profileId, RenderProfileDto dto, IRenderProfileRepository profiles, IActivityLog activity, CancellationToken ct) =>
        {
            var profile = dto with { Id = profileId };
            if (EndpointValidation.ValidateRenderProfile(profile) is { } problem)
            {
                return problem;
            }

            await profiles.UpsertAsync(profile.ToDomain(), ct);
            activity.Add("success", "Render Setup", "Render setup updated", $"Render setup {profile.DisplayName} was updated.", projectId: profile.ProjectId, renderProfileId: profile.Id, actionRoute: "projects");
            return Results.Ok(profile);
        });

        group.MapDelete("/{profileId}", async (string profileId, bool? force, IRenderProfileRepository profiles, IJobRepository jobs, CancellationToken ct) =>
        {
            if ((force ?? false) is false && (await jobs.ListAsync(ct)).Any(job => string.Equals(job.RenderProfileId, profileId, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Conflict(new { error = "Render profile has existing jobs. Pass ?force=true only after deciding historical jobs may keep dangling references." });
            }

            await profiles.DeleteAsync(profileId, ct);
            return Results.Ok(new { deleted = profileId, forced = force ?? false });
        });

        return app;
    }
}

public sealed record DuplicateRenderProfileRequest(string? NewId = null, string? DisplayName = null);

