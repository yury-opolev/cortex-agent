using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class HeuristicInterruptClassifierTests
{
    private static HeuristicInterruptClassifier New(
        Func<string, CancellationToken, Task<InterruptClass>>? llm = null)
        => new(llm);

    [Theory]
    [InlineData("mhm")]
    [InlineData("yeah")]
    [InlineData("uh huh")]
    [InlineData("haha")]
    [InlineData("ok sure")]
    public async Task BackchannelOnly_LowPEou_ShortWords_IsBackchannel(string partial)
    {
        var c = New();
        var r = await c.ClassifyAsync(partial, pEou: 0.01f, CancellationToken.None);
        Assert.Equal(InterruptClass.Backchannel, r);
    }

    [Theory]
    [InlineData("wait stop that's wrong")]
    [InlineData("no actually I meant the other thing")]
    [InlineData("can you instead look at the logs")]
    public async Task Contentful_IsReal(string partial)
    {
        var c = New();
        var r = await c.ClassifyAsync(partial, pEou: 0.4f, CancellationToken.None);
        Assert.Equal(InterruptClass.Real, r);
    }

    [Fact]
    public async Task Ambiguous_NoLlm_DefaultsReal()
    {
        // 2 words, not pure backchannel, mid pEou, no LLM tier -> safe default Real
        var c = New(llm: null);
        var r = await c.ClassifyAsync("hmm wait", pEou: 0.2f, CancellationToken.None);
        Assert.Equal(InterruptClass.Real, r);
    }

    [Fact]
    public async Task Ambiguous_WithLlm_UsesLlmVerdict()
    {
        var c = New(llm: (_, _) => Task.FromResult(InterruptClass.Backchannel));
        var r = await c.ClassifyAsync("hmm wait", pEou: 0.2f, CancellationToken.None);
        Assert.Equal(InterruptClass.Backchannel, r);
    }

    [Fact]
    public async Task LlmThrows_FallsBackToReal()
    {
        var c = New(llm: (_, _) => throw new InvalidOperationException("boom"));
        var r = await c.ClassifyAsync("hmm wait", pEou: 0.2f, CancellationToken.None);
        Assert.Equal(InterruptClass.Real, r);
    }

    [Fact]
    public async Task HeuristicOnly_NeverCallsLlm()
    {
        // HeuristicOnly mode wires a null LLM delegate by construction (see the
        // classifier construction in DiscordVoiceHandler — the LLM tier is never
        // wired on the Discord/Bridge path). With no delegate the Unsure band
        // must resolve to Real without any LLM call.
        var called = false;
        var c = new HeuristicInterruptClassifier(llm: null);
        var r = await c.ClassifyAsync("hmm wait", 0.2f, CancellationToken.None);
        Assert.False(called);
        Assert.Equal(InterruptClass.Real, r);
    }
}
