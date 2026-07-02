using System.Text.RegularExpressions;

namespace RenderFarm.Shared;

/// <summary>
/// Converts common Unreal command-line render log signatures into operator-facing diagnostics.
/// </summary>
public static class RenderLogClassifier
{
    private const int MaxExcerptLength = 6000;

    /// <summary>
    /// Classifies stdout, stderr, or saved Unreal log text without hiding the original log excerpt.
    /// </summary>
    public static RenderLogClassificationDto Classify(string? logText)
    {
        var text = logText ?? string.Empty;
        var diagnostics = new List<RenderLogDiagnosticDto>();
        var loadErrors = ExtractLoadErrors(text).ToArray();
        var configValue = ExtractMoviePipelineConfig(text);

        if (Contains(text, "Failed to find Pipeline Configuration asset to render"))
        {
            diagnostics.Add(new RenderLogDiagnosticDto(
                "mrq-config-not-found",
                "error",
                "MRQ config/queue preset was not found. Use a saved asset path like /Game/RenderConfig.RenderConfig and make sure the asset exists.",
                "Failed to find Pipeline Configuration asset to render",
                "Open Unreal, save the MRQ config or queue preset, then paste the object path without /Content."));
        }

        if (Contains(text, "Please note that the /Content/ part of the on-disk structure should be omitted"))
        {
            diagnostics.Add(new RenderLogDiagnosticDto(
                "content-path-in-asset-reference",
                "error",
                "Remove /Content from Unreal asset paths. Content/Render/MyPreset.uasset becomes /Game/Render/MyPreset.MyPreset.",
                "/Content/ part of the on-disk structure should be omitted",
                "Use Unreal mount paths such as /Game/Folder/Asset.Asset instead of filesystem Content paths."));
        }

        if (Contains(text, "RLPlugin is Incompatible"))
        {
            diagnostics.Add(new RenderLogDiagnosticDto(
                "rlplugin-incompatible",
                "warning",
                "Plugin built for an older Unreal version. Disable, rebuild, or update it before command-line rendering if it is required.",
                "RLPlugin is Incompatible",
                "Update the plugin for this engine version or disable it for render workers."));
        }

        if (Contains(text, "DatasmithContent/Materials/C4DMaster"))
        {
            diagnostics.Add(new RenderLogDiagnosticDto(
                "datasmith-c4d-material-missing",
                "warning",
                "Datasmith/C4D material dependency is missing. Enable/install Datasmith Content or repair/reimport affected materials.",
                "DatasmithContent/Materials/C4DMaster",
                "Enable Datasmith Content for the project or replace affected imported C4D materials."));
        }

        if (!string.IsNullOrWhiteSpace(configValue))
        {
            diagnostics.Add(new RenderLogDiagnosticDto(
                "movie-pipeline-config-value",
                "info",
                $"Unreal received MoviePipelineConfig={configValue}.",
                $"MoviePipelineConfig={configValue}",
                "Compare this value with the saved asset path shown in the render profile."));
        }

        if (loadErrors.Length > 0)
        {
            diagnostics.Add(new RenderLogDiagnosticDto(
                "load-errors",
                "warning",
                "Unreal reported load errors. Review the grouped load errors before retrying the render.",
                string.Join(Environment.NewLine, loadErrors.Take(6)),
                "Open the project in Unreal and resolve missing assets, classes, plugins, or redirectors."));
        }

        return new RenderLogClassificationDto(
            diagnostics,
            loadErrors,
            configValue,
            string.IsNullOrWhiteSpace(text) ? null : Truncate(text.Trim(), MaxExcerptLength));
    }

    private static bool Contains(string text, string value) =>
        text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string? ExtractMoviePipelineConfig(string text)
    {
        var match = Regex.Match(text, @"MoviePipelineConfig\s*=\s*(?<value>[^\s""']+|""[^""\r\n]+""|'[^'\r\n]+')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value.Trim().Trim('"', '\'') : null;
    }

    private static IEnumerable<string> ExtractLoadErrors(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        using var reader = new StringReader(text);
        var collecting = false;
        var count = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Contains("LoadErrors:", StringComparison.OrdinalIgnoreCase))
            {
                collecting = true;
                yield return line.Trim();
                count++;
                continue;
            }

            if (!collecting)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) || count >= 40)
            {
                yield break;
            }

            if (line.StartsWith("Log", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Error", StringComparison.OrdinalIgnoreCase) || char.IsWhiteSpace(line[0]))
            {
                yield return line.Trim();
                count++;
                continue;
            }

            yield break;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + Environment.NewLine + "... truncated ...";
}

