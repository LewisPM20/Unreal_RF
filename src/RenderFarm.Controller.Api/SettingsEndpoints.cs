using RenderFarm.Domain;
using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");

        group.MapGet("", async (ISettingsRepository settings, CancellationToken ct) =>
            Results.Ok((await settings.ListAsync(ct)).Select(x => new SettingDto(x.Key, x.ValueJson, x.UpdatedAtUtc))));

        group.MapGet("/{key}", async (string key, ISettingsRepository settings, CancellationToken ct) =>
            await settings.GetAsync(key, ct) is { } setting ? Results.Ok(new SettingDto(setting.Key, setting.ValueJson, setting.UpdatedAtUtc)) : Results.NotFound());

        group.MapPut("/{key}", async (string key, SettingDto dto, ISettingsRepository settings, CancellationToken ct) =>
        {
            var setting = new FarmSetting(key, dto.ValueJson, DateTimeOffset.UtcNow);
            await settings.UpsertAsync(setting, ct);
            return Results.Ok(new SettingDto(setting.Key, setting.ValueJson, setting.UpdatedAtUtc));
        });

        var queue = app.MapGroup("/api/queue").WithTags("Settings");
        queue.MapGet("/settings", async (ISettingsRepository settings, CancellationToken ct) =>
        {
            var setting = await settings.GetAsync("queue.settings", ct);
            return Results.Ok(new { enabled = true, source = "csharp", value = setting?.ValueJson });
        });

        queue.MapPost("/settings", async (QueueSettingsRequest request, ISettingsRepository settings, CancellationToken ct) =>
        {
            var json = request.Enabled ? """{"enabled":true}""" : """{"enabled":false}""";
            var setting = new FarmSetting("queue.settings", json, DateTimeOffset.UtcNow);
            await settings.UpsertAsync(setting, ct);
            return Results.Ok(new { enabled = request.Enabled, source = "csharp", value = setting.ValueJson });
        });

        queue.MapPost("/tick", async (IJobScheduler scheduler, CancellationToken ct) =>
            Results.Ok(new { expired = await scheduler.ExpireLeasesAsync(ct) }));

        return app;
    }
}
