using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

public class SubagentTaskStateTests
{
    [Theory]
    [InlineData(SubagentTaskState.Queued, "queued")]
    [InlineData(SubagentTaskState.Running, "running")]
    [InlineData(SubagentTaskState.Revising, "revising")]
    [InlineData(SubagentTaskState.Completed, "completed")]
    [InlineData(SubagentTaskState.Failed, "failed")]
    [InlineData(SubagentTaskState.Cancelled, "cancelled")]
    public void ToStorageValue_And_Parse_RoundTrip(SubagentTaskState state, string expected)
    {
        Assert.Equal(expected, state.ToStorageValue());
        Assert.Equal(state, SubagentTaskStateExtensions.Parse(expected));
    }
}
