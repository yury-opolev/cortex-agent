using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public sealed class MessageSourceBehaviorTests
{
    [Fact]
    public void For_User_ReturnsExpectedPolicy()
    {
        var behavior = MessageSourceBehavior.For(AgentMessageSource.User);

        Assert.False(behavior.RunInEphemeralSession);
        Assert.False(behavior.IsInternalToHistory);
        Assert.False(behavior.UseProactiveDelivery);
        Assert.True(behavior.HandlesSlashCommands);
        Assert.True(behavior.SetsConversationTitleFromText);
        Assert.True(behavior.RunsMemoryExtraction);
        Assert.Null(behavior.PendingInjectionLabelPrefix);
        Assert.Equal(LlmMessageType.Normal, behavior.PendingInjectionMessageType);
    }

    [Fact]
    public void For_ScheduledTask_ReturnsExpectedPolicy()
    {
        var behavior = MessageSourceBehavior.For(AgentMessageSource.ScheduledTask);

        Assert.True(behavior.RunInEphemeralSession);
        Assert.True(behavior.IsInternalToHistory);
        Assert.True(behavior.UseProactiveDelivery);
        Assert.False(behavior.HandlesSlashCommands);
        Assert.False(behavior.SetsConversationTitleFromText);
        Assert.False(behavior.RunsMemoryExtraction);
        Assert.Equal("[Scheduled Task] ", behavior.PendingInjectionLabelPrefix);
        Assert.Equal(LlmMessageType.ScheduledTaskInstruction, behavior.PendingInjectionMessageType);
    }

    [Fact]
    public void For_SubagentCompletion_ReturnsExpectedPolicy()
    {
        var behavior = MessageSourceBehavior.For(AgentMessageSource.SubagentCompletion);

        Assert.False(behavior.RunInEphemeralSession);
        Assert.True(behavior.IsInternalToHistory);
        Assert.False(behavior.UseProactiveDelivery);
        Assert.False(behavior.HandlesSlashCommands);
        Assert.False(behavior.SetsConversationTitleFromText);
        Assert.False(behavior.RunsMemoryExtraction);
        Assert.Equal("[Background Task Completed] ", behavior.PendingInjectionLabelPrefix);
        Assert.Equal(LlmMessageType.ScheduledTaskInstruction, behavior.PendingInjectionMessageType);
    }

    [Fact]
    public void For_SessionTimer_ReturnsExpectedPolicy()
    {
        var behavior = MessageSourceBehavior.For(AgentMessageSource.SessionTimer);

        // A fired timer is answered by a focused composer run over a bounded tail, not by
        // appending an instruction to the live conversation as if the user had typed it.
        Assert.True(behavior.UsesFocusedComposer);
        Assert.True(behavior.RecordsOutcomeInConversation);

        // NOT ephemeral: that flag means "start from an empty session". A composer run starts from
        // a seeded one, which the composer builds itself.
        Assert.False(behavior.RunInEphemeralSession);

        Assert.True(behavior.IsInternalToHistory);
        Assert.True(behavior.UseProactiveDelivery);
        Assert.False(behavior.HandlesSlashCommands);
        Assert.False(behavior.SetsConversationTitleFromText);
        Assert.False(behavior.RunsMemoryExtraction);
    }

    [Fact]
    public void Only_the_session_timer_uses_the_focused_composer()
    {
        // The composer discards its session, so any source that opted in without also opting into
        // RecordsOutcomeInConversation would silently lose its reply.
        foreach (var source in Enum.GetValues<AgentMessageSource>())
        {
            var behavior = MessageSourceBehavior.For(source);
            if (source != AgentMessageSource.SessionTimer)
            {
                Assert.False(behavior.UsesFocusedComposer);
            }

            Assert.True(!behavior.UsesFocusedComposer || behavior.RecordsOutcomeInConversation);
        }
    }

    [Fact]
    public void For_CodingAgentInjection_ReturnsExpectedPolicy()
    {
        var behavior = MessageSourceBehavior.For(AgentMessageSource.CodingAgentInjection);

        // CodingAgentInjection is NOT internal — it appears in user history as Normal.
        Assert.False(behavior.RunInEphemeralSession);
        Assert.False(behavior.IsInternalToHistory);
        Assert.False(behavior.UseProactiveDelivery);
        Assert.False(behavior.HandlesSlashCommands);
        Assert.False(behavior.SetsConversationTitleFromText);
        Assert.False(behavior.RunsMemoryExtraction);
        Assert.Null(behavior.PendingInjectionLabelPrefix);
        Assert.Equal(LlmMessageType.ScheduledTaskInstruction, behavior.PendingInjectionMessageType);
    }
}
