using RenderFarm.Domain;
using Xunit;

namespace RenderFarm.Tests;

public sealed class RenderChunkPlannerTests
{
    [Fact]
    public void PlanSplitsInclusiveFrameRangeDeterministically()
    {
        var chunks = RenderChunkPlanner.Plan(1, 1000, 200);

        Assert.Equal(5, chunks.Count);
        Assert.Equal(new RenderChunkRange(0, 1, 200, 5), chunks[0]);
        Assert.Equal(new RenderChunkRange(4, 801, 1000, 5), chunks[4]);
    }

    [Fact]
    public void PlanUsesShortFinalChunk()
    {
        var chunks = RenderChunkPlanner.Plan(10, 25, 7);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(new RenderChunkRange(0, 10, 16, 3), chunks[0]);
        Assert.Equal(new RenderChunkRange(1, 17, 23, 3), chunks[1]);
        Assert.Equal(new RenderChunkRange(2, 24, 25, 3), chunks[2]);
    }

    [Fact]
    public void PlanRejectsInvalidRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RenderChunkPlanner.Plan(20, 10, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => RenderChunkPlanner.Plan(1, 10, 0));
    }
}