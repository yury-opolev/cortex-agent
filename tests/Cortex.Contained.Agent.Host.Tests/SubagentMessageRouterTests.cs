using System.Reflection;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class SubagentMessageRouterTests
{
    [Fact]
    public void TryEnqueue_LiveSubagentConversation_InjectsRunnerAndSkipsMessageChannel()
    {
        var channel = new AgentMessageChannel();
        var registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);
        var runner = CreateRunner();
        Assert.True(registry.TryRegister("task-1", runner, out _));
        var router = new SubagentMessageRouter(channel, registry, NullLogger<SubagentMessageRouter>.Instance);

        Assert.True(router.TryEnqueue(CreateMessage("subagent-task-1", "hello")));

        Assert.False(channel.TryRead(out _));
        var injected = DrainInjectedMessages(runner);
        var message = Assert.Single(injected);
        Assert.Equal("hello", message.Text);
    }

    [Fact]
    public void TryEnqueue_NormalConversation_EnqueuesMessageChannel()
    {
        var channel = new AgentMessageChannel();
        var registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);
        var router = new SubagentMessageRouter(channel, registry, NullLogger<SubagentMessageRouter>.Instance);

        Assert.True(router.TryEnqueue(CreateMessage("webchat-default", "hello")));

        Assert.True(channel.TryRead(out var message));
        Assert.Equal("webchat-default", message!.ConversationId);
        Assert.Equal("hello", message.Text);
    }

    [Fact]
    public void TryEnqueue_SubagentConversationWithoutLiveRunner_EnqueuesMessageChannel()
    {
        var channel = new AgentMessageChannel();
        var registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);
        var router = new SubagentMessageRouter(channel, registry, NullLogger<SubagentMessageRouter>.Instance);

        Assert.True(router.TryEnqueue(CreateMessage("subagent-missing", "hello")));

        Assert.True(channel.TryRead(out var message));
        Assert.Equal("subagent-missing", message!.ConversationId);
        Assert.Equal("hello", message.Text);
    }

    private static AgentMessage CreateMessage(string conversationId, string text) => new()
    {
        ConversationId = conversationId,
        ChannelId = conversationId,
        Text = text,
        Source = AgentMessageSource.User,
    };

    private static SubagentRunner CreateRunner() => new(
        Substitute.For<ILlmClient>(),
        new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance),
        10,
        NullLogger<SubagentRunner>.Instance);

    private static IReadOnlyList<AgentMessage> DrainInjectedMessages(SubagentRunner runner)
    {
        var field = typeof(SubagentRunner).GetField("pendingSession", BindingFlags.Instance | BindingFlags.NonPublic);
        var session = Assert.IsType<AgentSession>(field!.GetValue(runner));
        return session.DrainPendingMessages();
    }
}
