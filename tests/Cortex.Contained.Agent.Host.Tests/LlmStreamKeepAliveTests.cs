using Cortex.Contained.Agent.Host.Llm.Providers;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Keep-alive (heartbeat) chunks and the raised idle budget.
/// <para>
/// Root cause this closes (2026-08-15): a thinking model was indistinguishable from a dead
/// socket. <c>AnthropicApiClient</c> yielded nothing for <c>message_start</c>, <c>ping</c> or
/// <c>thinking_delta</c>, so once a short opening text block had flipped
/// <see cref="LlmStreamIdleGuard"/> onto its tight between-chunks budget, a long silent thinking
/// phase on a large context tripped the watchdog and killed a healthy request.
/// </para>
/// <para>
/// The fix is an explicit, provider-agnostic signal — <see cref="LlmStreamChunk.IsKeepAlive"/> —
/// rather than the guard reaching into any provider's SSE vocabulary. A keep-alive proves
/// liveness and re-arms the current budget, but it is NOT content: it must not flip the guard
/// onto the between-chunks budget, and it must never escape to consumers (a keep-alive reaching
/// <c>DirectLlmClient</c> would set <c>emittedAny</c> and silently disable pre-content failover).
/// </para>
/// </summary>
public class LlmStreamKeepAliveTests
{
    private static readonly LlmStreamTimeouts Fast =
        new() { FirstChunk = TimeSpan.FromMilliseconds(600), BetweenChunks = TimeSpan.FromMilliseconds(200) };

    private static async Task<List<LlmStreamChunk>> DrainAsync(IAsyncEnumerable<LlmStreamChunk> source)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in source.ConfigureAwait(false))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static async IAsyncEnumerable<LlmStreamChunk> Script(
        params (int DelayMs, LlmStreamChunk Chunk)[] steps)
    {
        foreach (var (delayMs, chunk) in steps)
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            yield return chunk;
        }
    }

    private static LlmStreamChunk KeepAlive => new() { IsKeepAlive = true };

    private static LlmStreamChunk Text(string text) => new() { ContentDelta = text };

    // ── The bug: thinking must not look like death ─────────────────────

    [Fact]
    public async Task KeepAlives_DuringALongThinkingPause_KeepTheStreamAlive()
    {
        // Production shape: an opening text block, then a silence far longer than the
        // between-chunks budget, punctuated by heartbeats. Without keep-alives this is the
        // exact 120s watchdog kill that lost two 25-minute research runs.
        var source = Script(
            (10, Text("Let me start by ")),
            (150, KeepAlive),
            (150, KeepAlive),
            (150, KeepAlive),
            (150, KeepAlive),
            (150, Text("...the answer.")));

        var chunks = await DrainAsync(LlmStreamIdleGuard.Apply(source, Fast, CancellationToken.None));

        Assert.Equal(["Let me start by ", "...the answer."], chunks.Select(c => c.ContentDelta));
    }

    [Fact]
    public async Task KeepAlives_AreNotYieldedToConsumers()
    {
        // A keep-alive escaping the guard would set DirectLlmClient's `emittedAny`, silently
        // disabling pre-content failover — the guard consumes them.
        var source = Script((5, KeepAlive), (5, KeepAlive), (5, Text("hi")));

        var chunks = await DrainAsync(LlmStreamIdleGuard.Apply(source, Fast, CancellationToken.None));

        var chunk = Assert.Single(chunks);
        Assert.Equal("hi", chunk.ContentDelta);
        Assert.False(chunk.IsKeepAlive);
    }

    [Fact]
    public async Task KeepAlive_DoesNotArmTheTighterBetweenChunksBudget()
    {
        // A heartbeat before any content must leave the guard on the GENEROUS first-chunk
        // budget: time-to-first-token is still running. 400ms > BetweenChunks (200ms) but
        // < FirstChunk (600ms), so this only passes if keep-alives do not count as content.
        var source = Script((5, KeepAlive), (400, Text("first")));

        var chunks = await DrainAsync(LlmStreamIdleGuard.Apply(source, Fast, CancellationToken.None));

        var chunk = Assert.Single(chunks);
        Assert.Equal("first", chunk.ContentDelta);
    }

    [Fact]
    public async Task KeepAlives_DoNotDisableTheWatchdogAltogether()
    {
        // Liveness must still be bounded: heartbeats that stop coming are a real hang.
        var source = Script((5, KeepAlive), (5, Text("a")), (5000, Text("never")));

        await Assert.ThrowsAsync<TimeoutException>(
            () => DrainAsync(LlmStreamIdleGuard.Apply(source, Fast, CancellationToken.None)));
    }

    [Fact]
    public async Task KeepAlivesThatStopArriving_StillTimeOut()
    {
        var source = Script(
            (100, KeepAlive), (100, KeepAlive), (100, KeepAlive),
            (100, KeepAlive), (100, KeepAlive), (100, KeepAlive),
            (100, KeepAlive), (100, KeepAlive), (5000, Text("never")));

        await Assert.ThrowsAsync<TimeoutException>(
            () => DrainAsync(LlmStreamIdleGuard.Apply(source, Fast, CancellationToken.None)));
    }

    // ── Configuration ──────────────────────────────────────────────────

    [Fact]
    public void IdleBudget_DefaultsTo300Seconds()
    {
        // Raised from 120s: the file's own notes record 135-183s of legitimate quiet on large
        // prompts, and the 120s figure had no measured basis.
        Assert.Equal(TimeSpan.FromSeconds(300), LlmStreamTimeouts.Default.BetweenChunks);
        Assert.Equal(300, new AgentConfig().LlmStreamIdleTimeoutSeconds);
    }

    [Fact]
    public void IdleBudget_RemainsTunable()
    {
        var timeouts = LlmStreamTimeouts.FromSeconds(600, 90);

        Assert.Equal(TimeSpan.FromSeconds(600), timeouts.FirstChunk);
        Assert.Equal(TimeSpan.FromSeconds(90), timeouts.BetweenChunks);
    }

    [Fact]
    public void IdleBudget_ZeroStillDisablesTheGuard()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, LlmStreamTimeouts.FromSeconds(300, 0).BetweenChunks);
    }

    [Fact]
    public void FirstChunkBudget_IsNeverTighterThanTheIdleBudget()
    {
        Assert.True(LlmStreamTimeouts.Default.FirstChunk >= LlmStreamTimeouts.Default.BetweenChunks);
    }

    // ── The chunk contract ─────────────────────────────────────────────

    [Fact]
    public void KeepAliveChunk_CarriesNoContent()
    {
        var chunk = new LlmStreamChunk { IsKeepAlive = true };

        Assert.True(chunk.IsKeepAlive);
        Assert.Null(chunk.ContentDelta);
        Assert.Null(chunk.ToolCallDeltas);
        Assert.Null(chunk.ErrorMessage);
        Assert.False(chunk.IsComplete);
    }

    [Fact]
    public void OrdinaryChunk_IsNotAKeepAlive()
    {
        Assert.False(new LlmStreamChunk { ContentDelta = "x" }.IsKeepAlive);
    }
}
