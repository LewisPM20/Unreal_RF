using System.Globalization;
using RenderFarm.Domain;
using RenderFarm.Shared;

namespace RenderFarm.Worker.Agent;

/// <summary>
/// Validates render outputs using the mode selected by the profile or worker defaults.
/// </summary>
public static class RenderOutputValidator
{
    private static readonly string[] VideoExtensions = ["mov", "mp4", "avi", "mkv", "mxf"];
    private static readonly string[] ImageExtensions = ["png", "jpg", "jpeg", "exr", "bmp", "tif", "tiff"];
    private static readonly string[] AudioExtensions = ["wav", "aif", "aiff"];
    private static readonly string[] TempExtensions = ["tmp", "part", "partial", "lock"];

    public static OutputValidationSummaryDto Validate(UnrealRenderRequest request, string logText, OutputValidationMode? explicitMode = null)
    {
        var mode = explicitMode ?? DetermineMode(request.Profile);
        var expectedExtensions = ExpectedExtensions(mode, request.Profile).ToArray();
        var directory = request.OutputDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Failed(mode, directory, expectedExtensions, [], "No output directory was configured for this render.", "Set an output directory or configure Controller Render Defaults.");
        }

        if (!Directory.Exists(directory))
        {
            return Failed(mode, directory, expectedExtensions, [], $"Output folder was not created: {directory}", "Check the MRQ output path and worker write permissions.");
        }

        var files = Scan(directory).ToArray();
        var nonEmpty = files.Where(file => file.Length > 0).ToArray();
        if (files.Length > 0 && nonEmpty.Length == 0)
        {
            return Failed(mode, directory, expectedExtensions, files, "Output files were found, but every matching file is empty.", "Check whether Unreal failed while writing output or the share ran out of space.");
        }

        var matching = nonEmpty.Where(file => expectedExtensions.Contains(Extension(file), StringComparer.OrdinalIgnoreCase)).ToArray();
        return mode switch
        {
            OutputValidationMode.SingleVideo => ValidateSingleVideo(directory, mode, expectedExtensions, nonEmpty, matching),
            OutputValidationMode.ImageSequence => ValidateImageSequence(directory, mode, expectedExtensions, nonEmpty, matching, strict: false),
            OutputValidationMode.StrictFrameSequence => ValidateImageSequence(directory, mode, expectedExtensions, nonEmpty, matching, strict: true),
            _ => ValidateAny(directory, mode, expectedExtensions, nonEmpty)
        };
    }

    public static OutputValidationMode DetermineMode(RenderProfile profile)
    {
        var configured = GetSetting(profile, "outputValidationMode") ?? GetSetting(profile, "validationMode");
        if (Enum.TryParse<OutputValidationMode>(configured, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        var outputType = (GetSetting(profile, "fileFormat") ?? GetSetting(profile, "outputType") ?? profile.DefaultOutputType ?? string.Empty).Trim().TrimStart('.');
        if (VideoExtensions.Contains(outputType, StringComparer.OrdinalIgnoreCase)) return OutputValidationMode.SingleVideo;
        if (ImageExtensions.Contains(outputType, StringComparer.OrdinalIgnoreCase)) return OutputValidationMode.ImageSequence;
        return OutputValidationMode.AnyRenderOutput;
    }

    public static IReadOnlyList<string> SupportedRenderExtensions() => VideoExtensions.Concat(ImageExtensions).Concat(AudioExtensions).ToArray();

    private static OutputValidationSummaryDto ValidateSingleVideo(string directory, OutputValidationMode mode, IReadOnlyList<string> expected, IReadOnlyList<FileInfo> nonEmpty, IReadOnlyList<FileInfo> matching)
    {
        if (matching.Count > 0)
        {
            return Passed(mode, directory, expected, matching, $"Validated {matching.Count} non-empty video file(s).");
        }

        return WrongModeFailure(directory, mode, expected, nonEmpty, "a non-empty video file", "SingleVideo");
    }

    private static OutputValidationSummaryDto ValidateImageSequence(string directory, OutputValidationMode mode, IReadOnlyList<string> expected, IReadOnlyList<FileInfo> nonEmpty, IReadOnlyList<FileInfo> matching, bool strict)
    {
        if (matching.Count == 0)
        {
            return WrongModeFailure(directory, mode, expected, nonEmpty, "a non-empty image sequence", mode.ToString());
        }

        var missing = strict ? MissingFrameNumbers(matching).ToArray() : [];
        if (missing.Length > 0)
        {
            return new OutputValidationSummaryDto(
                OutputValidationStatus.Failed,
                mode,
                directory,
                expected.Select(x => "." + x).ToArray(),
                DetectedExtensions(nonEmpty),
                matching.Count,
                matching.Sum(file => file.Length),
                Samples(directory, matching),
                $"Image sequence is missing {missing.Length} frame number(s).",
                "Re-render the missing frames or switch to non-strict image sequence validation for internal smoke tests.",
                matching.Count,
                missing,
                BuildArtifactSummary(directory, matching));
        }

        return Passed(mode, directory, expected, matching, strict ? $"Validated strict image sequence with {matching.Count} non-empty frame(s)." : $"Validated image sequence with {matching.Count} non-empty file(s).", matching.Count);
    }

    private static OutputValidationSummaryDto ValidateAny(string directory, OutputValidationMode mode, IReadOnlyList<string> expected, IReadOnlyList<FileInfo> nonEmpty)
    {
        if (nonEmpty.Count > 0)
        {
            return Passed(mode, directory, expected, nonEmpty, $"Validated {nonEmpty.Count} non-empty plausible render output file(s).");
        }

        return Failed(mode, directory, expected, nonEmpty, "No non-empty render output files were found.", "Check the MRQ output folder, file type, and worker write permissions.");
    }

    private static OutputValidationSummaryDto WrongModeFailure(string directory, OutputValidationMode mode, IReadOnlyList<string> expected, IReadOnlyList<FileInfo> nonEmpty, string expectedPhrase, string modeName)
    {
        var produced = nonEmpty.Count == 0
            ? "nothing"
            : string.Join(", ", DetectedExtensions(nonEmpty).Select(x => "." + x));
        var message = $"Expected {expectedPhrase}, but the output folder produced {produced}.";
        var fix = nonEmpty.Any(file => VideoExtensions.Contains(Extension(file), StringComparer.OrdinalIgnoreCase)) && mode != OutputValidationMode.SingleVideo
            ? "This looks like a video render. Change validation mode to SingleVideo or update the MRQ output settings."
            : $"Set validation mode to match the MRQ output, or update the render profile to produce {modeName} output.";
        return Failed(mode, directory, expected, nonEmpty, message, fix);
    }

    private static OutputValidationSummaryDto Passed(OutputValidationMode mode, string directory, IReadOnlyList<string> expected, IReadOnlyList<FileInfo> files, string message, int? frameCount = null) =>
        new(
            OutputValidationStatus.Passed,
            mode,
            directory,
            expected.Select(x => "." + x).ToArray(),
            DetectedExtensions(files),
            files.Count,
            files.Sum(file => file.Length),
            Samples(directory, files),
            message,
            null,
            frameCount,
            Array.Empty<string>(),
            BuildArtifactSummary(directory, files));

    private static OutputValidationSummaryDto Failed(OutputValidationMode mode, string directory, IReadOnlyList<string> expected, IReadOnlyList<FileInfo> files, string message, string? fix) =>
        new(
            OutputValidationStatus.Failed,
            mode,
            directory,
            expected.Select(x => "." + x).ToArray(),
            DetectedExtensions(files),
            files.Count,
            files.Sum(file => file.Length),
            Samples(directory, files),
            message,
            fix,
            null,
            Array.Empty<string>(),
            files.Count > 0 ? BuildArtifactSummary(directory, files) : null);

    private static IEnumerable<FileInfo> Scan(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => !TempExtensions.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .Where(file => SupportedRenderExtensions().Contains(Extension(file), StringComparer.OrdinalIgnoreCase))
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<FileInfo>();
        }
    }

    private static IEnumerable<string> MissingFrameNumbers(IReadOnlyList<FileInfo> files)
    {
        var numbers = files.Select(file => LastNumber(file.Name)).Where(number => number is not null).Select(number => number!.Value).Distinct().Order().ToArray();
        if (numbers.Length < 2) yield break;
        for (var frame = numbers[0]; frame <= numbers[^1]; frame++)
        {
            if (!numbers.Contains(frame)) yield return frame.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static int? LastNumber(string value)
    {
        var digits = new string(value.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (string.IsNullOrWhiteSpace(digits))
        {
            var stem = Path.GetFileNameWithoutExtension(value);
            digits = new string(stem.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        }

        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static IReadOnlyList<string> ExpectedExtensions(OutputValidationMode mode, RenderProfile profile)
    {
        var configured = (GetSetting(profile, "expectedExtensions") ?? GetSetting(profile, "expectedOutputExtensions"))
            ?.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.TrimStart('.'))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (configured is { Length: > 0 }) return configured;
        return mode switch
        {
            OutputValidationMode.SingleVideo => VideoExtensions,
            OutputValidationMode.ImageSequence or OutputValidationMode.StrictFrameSequence => ImageExtensions,
            _ => SupportedRenderExtensions().ToArray()
        };
    }

    private static RenderArtifactSummaryDto BuildArtifactSummary(string outputDirectory, IReadOnlyList<FileInfo> files) =>
        new(outputDirectory, files.Count, files.Sum(file => file.Length), Samples(outputDirectory, files), DetectedExtensions(files));

    private static IReadOnlyList<string> Samples(string outputDirectory, IReadOnlyList<FileInfo> files) =>
        files.Take(8).Select(file => Path.GetRelativePath(outputDirectory, file.FullName)).ToArray();

    private static IReadOnlyList<string> DetectedExtensions(IReadOnlyList<FileInfo> files) =>
        files.Select(Extension).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    private static string Extension(FileInfo file) => Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant();

    private static string? GetSetting(RenderProfile profile, string key) =>
        profile.Settings.TryGetValue(key, out var exact)
            ? exact
            : profile.Settings.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
}

