using System.Text.RegularExpressions;

namespace RenderFarm.Shared;

/// <summary>
/// The expected Unreal asset-reference shape for a command-line argument.
/// </summary>
public enum UnrealAssetPathKind
{
    WorldPackagePath,
    LevelSequenceObjectPath,
    MoviePipelineConfigObjectPath,
    MoviePipelineQueueObjectPath
}

/// <summary>
/// Result from normalising a user-supplied Unreal package or object reference.
/// </summary>
public sealed record UnrealPathNormalizationResult(bool Success, string? NormalizedPath, string? Warning, string? Error);

/// <summary>
/// Normalises Unreal package/object references before command-line launch.
/// </summary>
public static class UnrealPathNormalizer
{
    private static readonly Regex UnrealReferenceWrapper = new(@"^[A-Za-z0-9_]+\s*'([^']+)'$", RegexOptions.Compiled);

    public static string NormalizeUnrealPackagePath(string raw)
    {
        var result = TryNormalizeUnrealReference(raw, UnrealAssetPathKind.WorldPackagePath);
        return result is { Success: true, NormalizedPath: { } path }
            ? path
            : throw new InvalidOperationException(result.Error ?? "Invalid Unreal map path.");
    }

    public static string NormalizeUnrealObjectPath(string raw)
    {
        var result = TryNormalizeUnrealReference(raw, UnrealAssetPathKind.MoviePipelineConfigObjectPath);
        return result is { Success: true, NormalizedPath: { } path }
            ? path
            : throw new InvalidOperationException(result.Error ?? "Invalid Unreal object path.");
    }

    public static UnrealPathNormalizationResult TryNormalizeUnrealReference(string? raw, UnrealAssetPathKind expectedKind)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Fail("Path is required.");
        }

        var original = raw.Trim();
        var value = original.Trim().Trim('"').Trim();
        var wrapperMatch = UnrealReferenceWrapper.Match(value);
        if (wrapperMatch.Success)
        {
            value = wrapperMatch.Groups[1].Value;
        }

        if (TryConvertUassetPath(value, out var converted))
        {
            value = converted;
        }

        value = value.Replace('\\', '/').Trim();
        var warning = BuildWarning(original, value, wrapperMatch.Success);

        if (value.Contains("/Content/", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Use Unreal mount paths such as /Game/RenderConfig.RenderConfig; do not include /Content/.");
        }

        if (value.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            value = "/" + value;
            warning = AppendWarning(warning, "Added the missing leading slash.");
        }

        if (expectedKind == UnrealAssetPathKind.WorldPackagePath && IsSimpleMapUrl(value))
        {
            return new(true, value, string.Equals(original, value, StringComparison.Ordinal) ? warning : AppendWarning(warning, $"Normalised to {value}."), null);
        }

        if (!IsValidMountPath(value))
        {
            return Fail("Asset paths must start with /Game/ or another Unreal mount point such as /PluginName/, or be a simple map name such as Minimal_Default1 when used in the map slot.");
        }

        var normalized = expectedKind switch
        {
            UnrealAssetPathKind.WorldPackagePath => NormalizeWorldPath(value, ref warning),
            UnrealAssetPathKind.MoviePipelineQueueObjectPath => NormalizeQueuePath(value, ref warning),
            _ => NormalizeObjectPath(value, ref warning)
        };

        if (normalized.Contains("/Content/", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Use Unreal mount paths such as /Game/RenderConfig.RenderConfig; do not include /Content/.");
        }

        return new(true, normalized, string.Equals(original, normalized, StringComparison.Ordinal) ? warning : AppendWarning(warning, $"Normalised to {normalized}."), null);
    }

    private static string NormalizeWorldPath(string value, ref string? warning)
    {
        var lastSlash = value.LastIndexOf('/');
        var dot = value.IndexOf(".", Math.Max(0, lastSlash), StringComparison.Ordinal);
        if (dot > lastSlash)
        {
            value = value[..dot];
            warning = AppendWarning(warning, "Converted the map object path to a world package path.");
        }

        return value;
    }

    private static string NormalizeObjectPath(string value, ref string? warning)
    {
        var lastSlash = value.LastIndexOf('/');
        var leaf = lastSlash >= 0 ? value[(lastSlash + 1)..] : value;
        if (!leaf.Contains('.', StringComparison.Ordinal))
        {
            value = value + "." + leaf;
            warning = AppendWarning(warning, "Appended the object name to the package path.");
        }

        return value;
    }

    private static string NormalizeQueuePath(string value, ref string? warning)
    {
        // Epic's command-line MRQ queue examples use queue package paths such as
        // /Game/Cinematics/myRenderQueue, so do not force .AssetName here.
        return value;
    }

    private static bool TryConvertUassetPath(string value, out string converted)
    {
        converted = string.Empty;
        var normal = value.Replace('\\', '/').Trim().Trim('"');
        if (!normal.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var marker = "/Content/";
        var index = normal.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var relative = normal[(index + marker.Length)..];
        converted = "/Game/" + relative[..^".uasset".Length].Trim('/');
        return true;
    }

    private static bool IsSimpleMapUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Contains('/') || value.Contains('\\') || value.Contains('.') || value.Contains(' ') || value.Contains('\''))
        {
            return false;
        }

        return value.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-');
    }

    private static bool IsValidMountPath(string value)
    {
        if (!value.StartsWith("/", StringComparison.Ordinal) || value.Length < 3)
        {
            return false;
        }

        var secondSlash = value.IndexOf('/', 1);
        if (secondSlash <= 1)
        {
            return false;
        }

        var mount = value[1..secondSlash];
        return mount.All(ch => char.IsLetterOrDigit(ch) || ch == '_') && value.Length > secondSlash + 1;
    }

    private static string? BuildWarning(string original, string value, bool strippedWrapper)
    {
        string? warning = strippedWrapper ? "Removed the copied Unreal reference wrapper." : null;
        if (TryConvertUassetPath(original, out _))
        {
            warning = AppendWarning(warning, "Converted a .uasset filesystem path to an Unreal /Game path.");
        }

        return warning;
    }

    private static UnrealPathNormalizationResult Fail(string error) => new(false, null, null, error);

    private static string? AppendWarning(string? existing, string warning) =>
        string.IsNullOrWhiteSpace(existing) ? warning : existing + " " + warning;
}