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
