using System.Text.Json;
using RenderFarm.Domain;
using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

internal static class ControllerRenderDefaults
{
    public const string SettingsKey = "render.defaults";

    public static async Task<RenderDefaultsDto> LoadAsync(ISettingsRepository settings, CancellationToken cancellationToken)
    {
        var setting = await settings.GetAsync(SettingsKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(setting?.ValueJson))
        {
            return Empty;
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize<RenderDefaultsDto>(setting.ValueJson, RenderFarmJson.SerializerOptions) ?? Empty);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static async Task<RenderDefaultsDto> SaveAsync(ISettingsRepository settings, RenderDefaultsDto request, CancellationToken cancellationToken)
    {
        var normalized = Normalize(request);
        await settings.UpsertAsync(new FarmSetting(SettingsKey, JsonSerializer.Serialize(normalized, RenderFarmJson.SerializerOptions), DateTimeOffset.UtcNow), cancellationToken);
        return normalized;
    }

    private static RenderDefaultsDto Empty => new(null, null, null, "{JobId}");

    private static RenderDefaultsDto Normalize(RenderDefaultsDto value) => new(
        NullIfWhiteSpace(value.UnrealExecutablePath),
        NullIfWhiteSpace(value.UnrealSearchRoot),
        NullIfWhiteSpace(value.SharedOutputRoot),
        string.IsNullOrWhiteSpace(value.OutputSubfolderPattern) ? "{JobId}" : value.OutputSubfolderPattern.Trim());

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
