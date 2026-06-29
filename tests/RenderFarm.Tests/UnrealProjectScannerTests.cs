using Microsoft.Extensions.Logging.Abstractions;
using RenderFarm.Controller.Api;
using Xunit;

namespace RenderFarm.Tests;

public sealed class UnrealProjectScannerTests
{
    [Fact]
    public async Task ScannerDiscoversFilesystemAssetHintsAndEngineVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_scan_tests", Guid.NewGuid().ToString("N"));
        var content = Path.Combine(root, "Content", "Cinematics");
        Directory.CreateDirectory(content);
        var projectPath = Path.Combine(root, "Demo.uproject");
        await File.WriteAllTextAsync(projectPath, """
        {
          "EngineAssociation": "5.7",
          "Plugins": [
            { "Name": "MovieRenderPipeline", "Enabled": true },
            { "Name": "OtherPlugin", "Enabled": true }
          ]
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(root, "Content", "Main.umap"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(content, "Seq_Main.uasset"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(content, "MRQ_Final.uasset"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(content, "MRG_Graph.uasset"), string.Empty);

        var scanner = new UnrealProjectScanner(NullLogger<UnrealProjectScanner>.Instance);
        var result = await scanner.ScanAsync(projectPath, useUnrealBridge: false, timeoutSeconds: 10, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("5.7", result.EngineVersion);
        Assert.Contains("/Game/Main", result.Maps);
        Assert.Contains("/Game/Cinematics/Seq_Main", result.LevelSequences);
        Assert.Contains("/Game/Cinematics/MRQ_Final", result.MovieRenderQueueConfigs);
        Assert.Contains("/Game/Cinematics/MRG_Graph", result.MovieRenderGraphs);
        Assert.Contains("MovieRenderPipeline", result.RelevantPlugins);
    }
}