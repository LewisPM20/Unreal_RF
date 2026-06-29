using RenderFarm.Domain;
using Xunit;

namespace RenderFarm.Tests;

public sealed class JobStateTests
{
    [Fact]
    public void JobStateContainsRenderingLifecycleStates()
    {
        Assert.Contains(JobState.Queued, Enum.GetValues<JobState>());
        Assert.Contains(JobState.Rendering, Enum.GetValues<JobState>());
        Assert.Contains(JobState.Succeeded, Enum.GetValues<JobState>());
        Assert.Contains(JobState.Failed, Enum.GetValues<JobState>());
    }

    [Theory]
    [InlineData(JobState.Queued, JobState.Reserved)]
    [InlineData(JobState.Reserved, JobState.Running)]
    [InlineData(JobState.Running, JobState.Succeeded)]
    [InlineData(JobState.Running, JobState.Failed)]
    [InlineData(JobState.Reserved, JobState.Stale)]
    [InlineData(JobState.Stale, JobState.Queued)]
    [InlineData(JobState.Queued, JobState.CancelRequested)]
    [InlineData(JobState.CancelRequested, JobState.Cancelled)]
    public void LegalTransitionsAreAllowed(JobState from, JobState to)
    {
        Assert.True(JobStateMachine.CanTransition(from, to));
        JobStateMachine.EnsureCanTransition(from, to);
    }

    [Theory]
    [InlineData(JobState.Succeeded, JobState.Queued)]
    [InlineData(JobState.Failed, JobState.Queued)]
    [InlineData(JobState.Cancelled, JobState.Running)]
    [InlineData(JobState.Queued, JobState.Succeeded)]
    [InlineData(JobState.Reserved, JobState.Succeeded)]
    public void IllegalTransitionsAreRejected(JobState from, JobState to)
    {
        Assert.False(JobStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => JobStateMachine.EnsureCanTransition(from, to));
    }

    [Theory]
    [InlineData(JobState.Succeeded)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    public void TerminalStatesAreTerminal(JobState state)
    {
        Assert.True(JobStateMachine.IsTerminal(state));
        Assert.False(JobStateMachine.CanTransition(state, JobState.Queued));
    }
}