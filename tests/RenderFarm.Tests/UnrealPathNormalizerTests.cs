using RenderFarm.Shared;
using Xunit;

namespace RenderFarm.Tests;

public sealed class UnrealPathNormalizerTests
{
    [Fact]
    public void MissingLeadingSlashIsAddedForConfigObjectPath()
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference("Game/RenderConfig.RenderConfig", UnrealAssetPathKind.MoviePipelineConfigObjectPath);

        Assert.True(result.Success, result.Error);
        Assert.Equal("/Game/RenderConfig.RenderConfig", result.NormalizedPath);
        Assert.Contains("leading slash", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapObjectPathBecomesWorldPackagePath()
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference("/Game/C4DFORUNREALV2/V2.V2", UnrealAssetPathKind.WorldPackagePath);

        Assert.True(result.Success, result.Error);
        Assert.Equal("/Game/C4DFORUNREALV2/V2", result.NormalizedPath);
    }

    [Fact]
    public void LevelSequencePathBecomesObjectPath()
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference("Game/C4DFORUNREALV2/V2.V2", UnrealAssetPathKind.LevelSequenceObjectPath);

        Assert.True(result.Success, result.Error);
        Assert.Equal("/Game/C4DFORUNREALV2/V2.V2", result.NormalizedPath);
    }

    [Fact]
    public void CopiedUnrealReferenceWrapperIsStripped()
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference("LevelSequence'/Game/Cinematics/Seq01.Seq01'", UnrealAssetPathKind.LevelSequenceObjectPath);

        Assert.True(result.Success, result.Error);
        Assert.Equal("/Game/Cinematics/Seq01.Seq01", result.NormalizedPath);
    }

    [Fact]
    public void UassetFilesystemPathUnderContentBecomesGamePath()
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference(@"D:\Projects\Show\Content\Render\Presets\HighQuality.uasset", UnrealAssetPathKind.MoviePipelineConfigObjectPath);

        Assert.True(result.Success, result.Error);
        Assert.Equal("/Game/Render/Presets/HighQuality.HighQuality", result.NormalizedPath);
    }

    [Fact]
    public void ContentMountReferencesAreRejected()
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference("/Game/Content/RenderConfig.RenderConfig", UnrealAssetPathKind.MoviePipelineConfigObjectPath);

        Assert.False(result.Success);
        Assert.Contains("Content", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimpleMapNameIsPreserved()
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference("V2", UnrealAssetPathKind.WorldPackagePath);

        Assert.True(result.Success, result.Error);
        Assert.Equal("V2", result.NormalizedPath);
    }

    [Fact]
    public void QueuePackagePathDoesNotAppendObjectName()
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference("/Game/RenderConfig", UnrealAssetPathKind.MoviePipelineQueueObjectPath);

        Assert.True(result.Success, result.Error);
        Assert.Equal("/Game/RenderConfig", result.NormalizedPath);
    }

    [Fact]
    public void ConfigPresetPathAppendsObjectName()
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference("/Game/RenderConfig", UnrealAssetPathKind.MoviePipelineConfigObjectPath);

        Assert.True(result.Success, result.Error);
        Assert.Equal("/Game/RenderConfig.RenderConfig", result.NormalizedPath);
    }

    [Fact]
    public void UassetQueuePathConvertsToPackagePathWithoutObjectName()
    {
        var result = UnrealPathNormalizer.TryNormalizeUnrealReference(@"D:\Project\Content\RenderConfig.uasset", UnrealAssetPathKind.MoviePipelineQueueObjectPath);

        Assert.True(result.Success, result.Error);
        Assert.Equal("/Game/RenderConfig", result.NormalizedPath);
    }
}

