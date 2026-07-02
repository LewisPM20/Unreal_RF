using System.Text.Json;
using System.Text.Json.Serialization;
using RenderFarm.Domain;

namespace RenderFarm.Shared;

/// <summary>
/// Shared JSON configuration for all controller and worker HTTP contracts.
/// </summary>
public static class RenderFarmJson
{
    /// <summary>
    /// Contract JSON options used by non-ASP.NET callers such as worker agents.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>
    /// Adds the enum converters required by render-farm DTO contracts.
    /// </summary>
    public static void AddConverters(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter<JobState>());
        options.Converters.Add(new JsonStringEnumConverter<FailureCategory>());
        options.Converters.Add(new JsonStringEnumConverter<WorkerStatus>());
        options.Converters.Add(new JsonStringEnumConverter<WorkerSchedulingMode>());
        options.Converters.Add(new JsonStringEnumConverter<RenderProfileType>());
        options.Converters.Add(new JsonStringEnumConverter<RenderValidationSeverity>());
        options.Converters.Add(new JsonStringEnumConverter<RenderValidationStatus>());
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        AddConverters(options);
        return options;
    }
}

