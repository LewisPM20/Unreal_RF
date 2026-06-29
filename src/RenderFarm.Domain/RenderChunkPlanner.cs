namespace RenderFarm.Domain;

/// <summary>
/// One deterministic child render range planned from a parent frame range.
/// </summary>
public sealed record RenderChunkRange(int ChunkIndex, int FrameStart, int FrameEnd, int TotalChunks);

/// <summary>
/// Deterministic chunk planner. Execution remains gated until Unreal MRQ/MRG frame-range rendering is proven.
/// </summary>
public static class RenderChunkPlanner
{
    public static IReadOnlyList<RenderChunkRange> Plan(int frameStart, int frameEndInclusive, int chunkSizeFrames)
    {
        if (frameEndInclusive < frameStart)
        {
            throw new ArgumentOutOfRangeException(nameof(frameEndInclusive), "Frame end must be greater than or equal to frame start.");
        }

        if (chunkSizeFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSizeFrames), "Chunk size must be greater than zero.");
        }

        var totalFrames = frameEndInclusive - frameStart + 1;
        var totalChunks = (int)Math.Ceiling(totalFrames / (double)chunkSizeFrames);
        var chunks = new List<RenderChunkRange>(totalChunks);
        for (var index = 0; index < totalChunks; index++)
        {
            var start = frameStart + index * chunkSizeFrames;
            var end = Math.Min(frameEndInclusive, start + chunkSizeFrames - 1);
            chunks.Add(new RenderChunkRange(index, start, end, totalChunks));
        }

        return chunks;
    }
}