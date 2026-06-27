using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// The pure decision the persist site uses: write the played-only text when
/// the turn was barge-in interrupted, otherwise the full generated response.
/// </summary>
public class ChooseAssistantContentTests
{
    [Fact]
    public void NotInterrupted_UsesFullResponse()
    {
        var s = new AgentSession("c1");
        var content = s.InterruptedPlayedText ?? "Full generated response.";
        Assert.Equal("Full generated response.", content);
    }

    [Fact]
    public void Interrupted_UsesPlayedText()
    {
        var s = new AgentSession("c1");
        s.MarkInterrupted("Sure. There was an engineer named Greg…");
        var content = s.InterruptedPlayedText ?? "…the entire 8-sentence story.";
        Assert.Equal("Sure. There was an engineer named Greg…", content);
    }

    [Fact]
    public void Interrupted_EmptyPlayedText_StillUsesPlayedText()
    {
        // Barge-in before any sentence finished → played text is the marker only.
        var s = new AgentSession("c1");
        s.MarkInterrupted("…");
        var content = s.InterruptedPlayedText ?? "full text";
        Assert.Equal("…", content);
    }
}
