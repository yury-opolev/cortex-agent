using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests;

public class TurnInterruptedRecordingTests
{
    [Fact]
    public void Notification_CarriesConversationAndPlayedText()
    {
        var n = new TurnInterruptedNotification
        {
            ConversationId = "discord-voice-default",
            PlayedText = "The first option is to use the …",
        };
        Assert.Equal("discord-voice-default", n.ConversationId);
        Assert.EndsWith("…", n.PlayedText);
    }

    [Fact]
    public void TruncateLastAssistant_ReplacesContentWithPlayedText()
    {
        // AgentSession is the unit under test for the history mutation.
        var session = new Cortex.Contained.Agent.Host.Agent.AgentSession("discord-voice-default");
        session.AddMessage(new Cortex.Contained.Contracts.Llm.LlmMessage { Role = "user", Content = "tell me the options" });
        session.AddMessage(new Cortex.Contained.Contracts.Llm.LlmMessage { Role = "assistant", Content = "The first option is to use the built-in tool and then configure it fully." });

        Cortex.Contained.Agent.Host.Agent.AgentRuntime.TruncateLastAssistantTurn(session, "The first option is to use the …");

        var h = session.GetHistory();
        Assert.Equal("assistant", h[^1].Role);
        Assert.Equal("The first option is to use the …", h[^1].Content);
    }

    [Fact]
    public void TruncateLastAssistant_NoAssistantTail_AppendsAssistantTurn()
    {
        var session = new Cortex.Contained.Agent.Host.Agent.AgentSession("discord-voice-default");
        session.AddMessage(new Cortex.Contained.Contracts.Llm.LlmMessage { Role = "user", Content = "hi" });

        Cortex.Contained.Agent.Host.Agent.AgentRuntime.TruncateLastAssistantTurn(session, "Hi there …");

        var h = session.GetHistory();
        Assert.Equal("assistant", h[^1].Role);
        Assert.Equal("Hi there …", h[^1].Content);
    }
}
