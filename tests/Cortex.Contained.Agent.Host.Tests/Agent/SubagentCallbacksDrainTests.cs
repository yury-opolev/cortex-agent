using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public class SubagentCallbacksDrainTests
{
    [Fact]
    public void DrainInjectedMessages_WithPendingSession_AddsUserMessages()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a subagent." },
        };
        var session = new AgentSession("subagent-test");

        var callbacks = CreateCallbacks(messages, session);

        // Enqueue via session (simulating SubagentRunner.InjectMessage)
        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "subagent",
            ChannelId = "subagent",
            Text = "additional context",
            Source = AgentMessageSource.User,
        });

        callbacks.DrainInjectedMessages();

        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[1].Role);
        Assert.Equal("additional context", messages[1].Content);
    }

    [Fact]
    public void DrainInjectedMessages_NullSession_NoOp()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a subagent." },
        };

        var callbacks = CreateCallbacks(messages, pendingSession: null);
        callbacks.DrainInjectedMessages();

        // No messages added
        Assert.Single(messages);
    }

    [Fact]
    public void DrainInjectedMessages_EmptyQueue_NoOp()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a subagent." },
        };
        var session = new AgentSession("subagent-test");

        var callbacks = CreateCallbacks(messages, session);
        callbacks.DrainInjectedMessages();

        Assert.Single(messages);
    }

    [Fact]
    public void DrainInjectedMessages_MultipleMessages_AllAddedInOrder()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a subagent." },
        };
        var session = new AgentSession("subagent-test");

        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "subagent", ChannelId = "subagent",
            Text = "first injection", Source = AgentMessageSource.User,
        });
        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "subagent", ChannelId = "subagent",
            Text = "second injection", Source = AgentMessageSource.User,
        });

        var callbacks = CreateCallbacks(messages, session);
        callbacks.DrainInjectedMessages();

        Assert.Equal(3, messages.Count);
        Assert.Equal("first injection", messages[1].Content);
        Assert.Equal("second injection", messages[2].Content);
    }

    [Fact]
    public void DrainInjectedMessages_CalledTwice_SecondDrainEmpty()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a subagent." },
        };
        var session = new AgentSession("subagent-test");

        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "subagent", ChannelId = "subagent",
            Text = "only message", Source = AgentMessageSource.User,
        });

        var callbacks = CreateCallbacks(messages, session);

        callbacks.DrainInjectedMessages();
        Assert.Equal(2, messages.Count);

        // Second drain adds nothing
        callbacks.DrainInjectedMessages();
        Assert.Equal(2, messages.Count);
    }

    private static SubagentCallbacks CreateCallbacks(
        List<LlmMessage> messages,
        AgentSession? pendingSession)
    {
        var mockLlmClient = Substitute.For<ILlmClient>();

        return new SubagentCallbacks(
            messages,
            contextWindow: 128_000,
            maxOutputTokens: 8192,
            conversationId: "subagent-test",
            llmClient: mockLlmClient,
            logger: NullLogger.Instance,
            pendingSession: pendingSession);
    }
}
