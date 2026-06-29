using System.Text.Json;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public interface IUnrealProjectScanner
{
    Task<UnrealProjectScanResultDto> ScanAsync(string projectPath, bool useUnrealBridge, int timeoutSeconds, CancellationToken cancellationToken);
}

public sealed class UnrealProjectScanner(ILogger<UnrealProjectScanner> logger) : IUnrealProjectScanner
{
    public async Task<UnrealProjectScanResultDto> ScanAsync(string projectPath, bool useUnrealBridge, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return Failed(projectPath, "Project path is required.");
        }

        var fullProjectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullProjectPath) || !fullProjectPath.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase))
        {
            return Failed(fullProjectPath, "Project .uproject file was not found.");
        }

        try
        {
            var projectRoot = Path.GetDirectoryName(fullProjectPath)!;
            var engineVersion = await ReadEngineVersionAsync(fullProjectPath, cancellationToken);
            var plugins = await ReadRelevantPluginsAsync(fullProjectPath, cancellationToken);
            var contentRoot = Path.Combine(projectRoot, "Content");
            var assets = Directory.Exists(contentRoot)
                ? Directory.EnumerateFiles(contentRoot, "*.*", SearchOption.AllDirectories)
                    .Where(path => path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : [];

            var maps = assets.Where(path => path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)).Select(path => ToGamePath(contentRoot, path)).OrderBy(x => x).ToArray();
            var sequences = assets.Where(path => ContainsAny(Path.GetFileNameWithoutExtension(path), "sequence", "levelsequence", "seq_")).Select(path => ToGamePath(contentRoot, path)).OrderBy(x => x).ToArray();
            var mrq = assets.Where(path => ContainsAny(Path.GetFileNameWithoutExtension(path), "mrq", "renderqueue", "moviepipeline", "movie_render_queue")).Select(path => ToGamePath(contentRoot, path)).OrderBy(x => x).ToArray();
            var mrg = assets.Where(path => ContainsAny(Path.GetFileNameWithoutExtension(path), "mrg", "moviegraph", "movie_render_graph")).Select(path => ToGamePath(contentRoot, path)).OrderBy(x => x).ToArray();

            return new UnrealProjectScanResultDto(fullProjectPath, engineVersion, maps, sequences, mrq, mrg, plugins, UsedUnrealBridge: false, Ok: true, Error: useUnrealBridge ? "Unreal bridge invocation is defined under bridge/unreal_python but direct controller scanning used filesystem metadata in this phase." : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Project scan failed for {ProjectPath}", fullProjectPath);
            return Failed(fullProjectPath, ex.Message);
        }
    }

    private static UnrealProjectScanResultDto Failed(string projectPath, string error) =>
        new(projectPath, null, [], [], [], [], [], false, false, error);

    private static async Task<string?> ReadEngineVersionAsync(string uprojectPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(uprojectPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("EngineAssociation", out var engine) ? engine.GetString() : null;
    }

    private static async Task<IReadOnlyList<string>> ReadRelevantPluginsAsync(string uprojectPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(uprojectPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("Plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return plugins.EnumerateArray()
            .Where(plugin => plugin.TryGetProperty("Enabled", out var enabled) && enabled.GetBoolean())
            .Select(plugin => plugin.TryGetProperty("Name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name) && ContainsAny(name!, "MovieRender", "MoviePipeline", "Sequencer", "Python"))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ToGamePath(string contentRoot, string assetPath)
    {
        var relative = Path.GetRelativePath(contentRoot, assetPath);
        var withoutExtension = Path.ChangeExtension(relative, null) ?? relative;
        return "/Game/" + withoutExtension.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}