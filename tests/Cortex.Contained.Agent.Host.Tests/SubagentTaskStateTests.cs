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

    [Fact]
    public void TaskState_ParseUnknown_Throws()
    {
        Assert.Throws<InvalidDataException>(() => SubagentTaskStateExtensions.Parse("garbage"));
    }

    [Theory]
    [InlineData(SubagentRunMode.New, "new")]
    [InlineData(SubagentRunMode.Resume, "resume")]
    public void RunMode_ToStorageValueAndParse_RoundTrips(SubagentRunMode runMode, string expected)
    {
        Assert.Equal(expected, runMode.ToStorageValue());
        Assert.Equal(runMode, SubagentRunModeExtensions.Parse(expected));
    }

    [Theory]
    [InlineData(SubagentNotificationState.None, "none")]
    [InlineData(SubagentNotificationState.Pending, "pending")]
    [InlineData(SubagentNotificationState.Enqueued, "enqueued")]
    [InlineData(SubagentNotificationState.Delivered, "delivered")]
    public void NotificationState_ToStorageValueAndParse_RoundTrips(SubagentNotificationState state, string expected)
    {
        Assert.Equal(expected, state.ToStorageValue());
        Assert.Equal(state, SubagentNotificationStateExtensions.Parse(expected));
    }

    [Fact]
    public void RunMode_ParseUnknown_Throws()
    {
        Assert.Throws<InvalidDataException>(() => SubagentRunModeExtensions.Parse("garbage"));
    }

    [Fact]
    public void NotificationState_ParseUnknown_Throws()
    {
        Assert.Throws<InvalidDataException>(() => SubagentNotificationStateExtensions.Parse("garbage"));
    }
}
