using RenderFarm.Shared;
using Xunit;

namespace RenderFarm.Tests;

public sealed class RenderLogClassifierTests
{
    [Fact]
    public void PipelineConfigMissingProducesHumanFix()
    {
        var result = RenderLogClassifier.Classify("Error: Failed to find Pipeline Configuration asset to render MoviePipelineConfig=/Game/Bad.Bad");

        Assert.Contains(result.Diagnostics, item => item.Code == "mrq-config-not-found" && item.Message.Contains("MRQ config/queue preset", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/Game/Bad.Bad", result.MoviePipelineConfigValue);
        Assert.NotNull(result.RawExcerpt);
    }

    [Fact]
    public void ContentPathWarningProducesPathFix()
    {
        var result = RenderLogClassifier.Classify("Please note that the /Content/ part of the on-disk structure should be omitted");

        Assert.Contains(result.Diagnostics, item => item.Code == "content-path-in-asset-reference" && item.Message.Contains("Remove /Content", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PluginAndDatasmithWarningsAreClassified()
    {
        var result = RenderLogClassifier.Classify("RLPlugin is Incompatible\nDatasmithContent/Materials/C4DMaster missing");

        Assert.Contains(result.Diagnostics, item => item.Code == "rlplugin-incompatible");
        Assert.Contains(result.Diagnostics, item => item.Code == "datasmith-c4d-material-missing");
    }

    [Fact]
    public void LoadErrorsAreGrouped()
    {
        var result = RenderLogClassifier.Classify("LoadErrors:\n  Missing asset /Game/Foo\nLogMovieRenderPipeline: Done");

        Assert.NotEmpty(result.LoadErrors);
        Assert.Contains(result.Diagnostics, item => item.Code == "load-errors");
    }
}
