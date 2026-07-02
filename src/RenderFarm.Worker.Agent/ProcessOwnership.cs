namespace RenderFarm.Worker.Agent;

public sealed record OwnedProcessMetadata(int ProcessId, string? ExecutablePath, string? InstanceRoot, bool RenderFarmOwnedMarker);

public enum ProcessOwnershipDecision
{
    NotOwned,
    OwnedByThisInstance,
    Uncertain
}

public static class ProcessOwnershipDecider
{
    public static ProcessOwnershipDecision Decide(OwnedProcessMetadata metadata, string currentInstanceRoot)
    {
        if (!metadata.RenderFarmOwnedMarker)
        {
            return ProcessOwnershipDecision.NotOwned;
        }

        if (string.IsNullOrWhiteSpace(metadata.InstanceRoot) || string.IsNullOrWhiteSpace(currentInstanceRoot)) {
            return ProcessOwnershipDecision.Uncertain;
        }

        var current = Normalize(currentInstanceRoot);
        var candidate = Normalize(metadata.InstanceRoot);
        return string.Equals(current, candidate, StringComparison.OrdinalIgnoreCase)
            ? ProcessOwnershipDecision.OwnedByThisInstance
            : ProcessOwnershipDecision.Uncertain;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
