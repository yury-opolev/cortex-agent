using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Events;

namespace Cortex.Contained.Agent.Host.Tests.Events;

public sealed class RunProvenanceTests
{
    private static AgentMessage Message(AgentMessageSource source, string conversationId = "discord-dm", string? trigger = null, string? subagentTaskId = null) => new()
    {
        ConversationId = conversationId,
        ChannelId = "discord-dm",
        Text = "x",
        Source = source,
        TriggerLabel = trigger,
        SubagentTaskId = subagentTaskId,
    };

    [Fact]
    public void ForMessage_ScheduledTask_TakesTaskIdFromConversation()
    {
        var run = RunProvenance.ForMessage(Message(AgentMessageSource.ScheduledTask, "scheduled-1bffa504", "scheduled task 1bffa504 (\"brief\")"));

        Assert.Equal(AuthorKinds.ScheduledTask, run.AuthorKind);
        Assert.Equal("1bffa504", run.AuthorId);
        Assert.Equal("discord-dm", run.ChannelId);
        Assert.Equal("scheduled task 1bffa504 (\"brief\")", run.Label);
        Assert.StartsWith(EventIds.RunPrefix, run.RunId, StringComparison.Ordinal);
    }

    [Fact]
    public void ForMessage_TimerAndUserAndSubagentCompletion_MapAuthorKinds()
    {
        Assert.Equal(AuthorKinds.Timer, RunProvenance.ForMessage(Message(AgentMessageSource.SessionTimer)).AuthorKind);
        Assert.Equal(AuthorKinds.Main, RunProvenance.ForMessage(Message(AgentMessageSource.User)).AuthorKind);

        var completion = RunProvenance.ForMessage(Message(AgentMessageSource.SubagentCompletion, subagentTaskId: "t1"));
        Assert.Equal(AuthorKinds.Main, completion.AuthorKind);
        Assert.Equal("completion of subagent t1", completion.Label);
    }

    [Fact]
    public void ForSubagent_HasNoChannel()
    {
        var run = RunProvenance.ForSubagent("t9");

        Assert.Equal(AuthorKinds.Subagent, run.AuthorKind);
        Assert.Equal("t9", run.AuthorId);
        Assert.Null(run.ChannelId);
    }

    [Fact]
    public async Task RunScope_Begin_IsVisibleDownstreamAndRestoredOnDispose()
    {
        var outer = RunProvenance.ForSubagent("outer");
        var inner = RunProvenance.ForSubagent("inner");

        using (RunScope.Begin(outer))
        {
            using (RunScope.Begin(inner))
            {
                Assert.Same(inner, await Task.Run(() => RunScope.Current));
            }

            Assert.Same(outer, RunScope.Current);
        }

        Assert.Null(RunScope.Current);
    }

    [Fact]
    public void EventIds_NewEventId_IsRecognisedAndUnique()
    {
        var first = EventIds.NewEventId();
        var second = EventIds.NewEventId();

        Assert.True(EventIds.IsEventId(first));
        Assert.NotEqual(first, second);
        Assert.False(EventIds.IsEventId("ev_XYZ"));
        Assert.False(EventIds.IsEventId(EventIds.NewRunId()));
    }
}
