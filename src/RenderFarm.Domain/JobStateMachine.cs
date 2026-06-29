namespace RenderFarm.Domain;

/// <summary>
/// Central definition of legal controller-owned job state transitions.
/// </summary>
public static class JobStateMachine
{
    private static readonly IReadOnlyDictionary<JobState, IReadOnlySet<JobState>> LegalTransitions = new Dictionary<JobState, IReadOnlySet<JobState>>
    {
        [JobState.Created] = StateSet(JobState.Queued, JobState.Cancelled),
        [JobState.Queued] = StateSet(JobState.Reserved, JobState.CancelRequested, JobState.Cancelled),
        [JobState.Reserved] = StateSet(JobState.Running, JobState.Queued, JobState.RetryWait, JobState.Stale, JobState.Failed, JobState.CancelRequested, JobState.Cancelling, JobState.Cancelled),
        [JobState.Running] = StateSet(JobState.ValidatingWorker, JobState.PreparingUnrealQueue, JobState.LaunchingUnreal, JobState.Rendering, JobState.VerifyingOutputs, JobState.Queued, JobState.RetryWait, JobState.Stale, JobState.Succeeded, JobState.Failed, JobState.CancelRequested, JobState.Cancelling, JobState.Cancelled),
        [JobState.ValidatingWorker] = StateSet(JobState.PreparingUnrealQueue, JobState.Running, JobState.Queued, JobState.RetryWait, JobState.Failed, JobState.CancelRequested, JobState.Cancelling, JobState.Cancelled),
        [JobState.PreparingUnrealQueue] = StateSet(JobState.LaunchingUnreal, JobState.Running, JobState.Queued, JobState.RetryWait, JobState.Failed, JobState.CancelRequested, JobState.Cancelling, JobState.Cancelled),
        [JobState.LaunchingUnreal] = StateSet(JobState.Rendering, JobState.Running, JobState.Queued, JobState.RetryWait, JobState.Failed, JobState.CancelRequested, JobState.Cancelling, JobState.Cancelled),
        [JobState.Rendering] = StateSet(JobState.VerifyingOutputs, JobState.Running, JobState.Queued, JobState.RetryWait, JobState.Stale, JobState.Succeeded, JobState.Failed, JobState.CancelRequested, JobState.Cancelling, JobState.Cancelled),
        [JobState.VerifyingOutputs] = StateSet(JobState.Succeeded, JobState.Failed, JobState.CancelRequested, JobState.Cancelling, JobState.Cancelled),
        [JobState.CancelRequested] = StateSet(JobState.Cancelling, JobState.Cancelled, JobState.Failed),
        [JobState.Cancelling] = StateSet(JobState.Cancelled, JobState.Failed),
        [JobState.Stale] = StateSet(JobState.Queued, JobState.Failed),
        [JobState.RetryWait] = StateSet(JobState.Queued, JobState.Failed, JobState.CancelRequested, JobState.Cancelled),
        [JobState.Succeeded] = StateSet(),
        [JobState.Failed] = StateSet(),
        [JobState.Cancelled] = StateSet()
    };

    /// <summary>
    /// Returns true when a state is terminal and should never be rescheduled.
    /// </summary>
    public static bool IsTerminal(JobState state) => state is JobState.Succeeded or JobState.Failed or JobState.Cancelled;

    /// <summary>
    /// Returns true when moving from one state to another is legal. Staying in the same state is idempotent.
    /// </summary>
    public static bool CanTransition(JobState from, JobState to) =>
        from == to || (!IsTerminal(from) && LegalTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to));

    /// <summary>
    /// Throws when the requested transition would corrupt the job lifecycle.
    /// </summary>
    public static void EnsureCanTransition(JobState from, JobState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Illegal job state transition from {from} to {to}.");
        }
    }

    private static IReadOnlySet<JobState> StateSet(params JobState[] states) => states.ToHashSet();
}