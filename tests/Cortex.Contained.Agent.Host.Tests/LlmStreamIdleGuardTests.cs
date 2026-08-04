using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Llm.Providers;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests the inactivity guard for provider streams. Verified behaviour of .NET: when a request
/// uses <see cref="HttpCompletionOption.ResponseHeadersRead"/>, <c>HttpClient.Timeout</c> does
/// NOT bound reads of the response body — so a provider that accepts the request and then goes
/// silent leaves the read pending forever. This guard bounds the gaps instead.
/// <para>
/// The budget for the FIRST chunk is deliberately separate from and larger than the
/// between-chunk budget: time-to-first-token grows with prompt size (coda observed 135-183s on
/// large grounding prompts), and coda's original guard wrongly bounded time-to-first-token with
/// the idle budget and killed healthy slow turns.
/// </para>
/// </summary>
public class LlmStreamIdleGuardTests
{
    private static readonly LlmStreamTimeouts Fast =
        new() { FirstChunk = TimeSpan.FromMilliseconds(400), BetweenChunks = TimeSpan.FromMilliseconds(200) };

    private static async IAsyncEnumerable<LlmStreamChunk> Delayed(
        params (int DelayMs, string? Text)[] steps)
    {
        foreach (var (delayMs, text) in steps)
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            yield return new LlmStreamChunk { ContentDelta = text };
        }
    }

    private static async Task<List<LlmStreamChunk>> DrainAsync(IAsyncEnumerable<LlmStreamChunk> source)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in source.ConfigureAwait(false))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    [Fact]
    public async Task Guard_StreamWithinBudget_PassesEveryChunkThrough()
    {
        var source = Delayed((10, "a"), (10, "b"), (10, "c"));

        var chunks = await DrainAsync(
            LlmStreamIdleGuard.Apply(source, Fast, CancellationToken.None));

        Assert.Equal(3, chunks.Count);
        Assert.Equal(["a", "b", "c"], chunks.Select(c => c.ContentDelta));
    }

    [Fact]
    public async Task Guard_StallsBeforeFirstChunk_ThrowsTimeout()
    {
        var source = Delayed((5000, "never"));

        await Assert.ThrowsAsync<TimeoutException>(
            () => DrainAsync(LlmStreamIdleGuard.Apply(source, Fast, CancellationToken.None)));
    }

    [Fact]
    public async Task Guard_StallsBetweenChunks_ThrowsTimeout()
    {
        var source = Delayed((10, "a"), (5000, "never"));

        await Assert.ThrowsAsync<TimeoutException>(
            () => DrainAsync(LlmStreamIdleGuard.Apply(source, Fast, CancellationToken.None)));
    }

    [Fact]
    public async Task Guard_SlowFirstChunkWithinItsLargerBudget_IsNotKilled()
    {
        // The whole point of a separate first-chunk budget: a 300ms time-to-first-token must
        // survive a 200ms BETWEEN-chunk budget, because generation has not started yet.
        var source = Delayed((300, "first"), (10, "second"));

        var chunks = await DrainAsync(
            LlmStreamIdleGuard.Apply(source, Fast, CancellationToken.None));

        Assert.Equal(["first", "second"], chunks.Select(c => c.ContentDelta));
    }

    [Fact]
    public async Task Guard_DisabledTimeouts_NeverTimeOut()
    {
        var disabled = new LlmStreamTimeouts
        {
            FirstChunk = Timeout.InfiniteTimeSpan,
            BetweenChunks = Timeout.InfiniteTimeSpan,
        };
        var source = Delayed((300, "a"));

        var chunks = await DrainAsync(
            LlmStreamIdleGuard.Apply(source, disabled, CancellationToken.None));

        Assert.Single(chunks);
    }

    [Fact]
    public async Task Guard_CallerCancellation_PropagatesCancellationNotTimeout()
    {
        using var cts = new CancellationTokenSource(100);
        var source = Delayed((5000, "never"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in LlmStreamIdleGuard
                .Apply(source, LlmStreamTimeouts.Default, cts.Token)
                .WithCancellation(cts.Token)
                .ConfigureAwait(false))
            {
                // drain
            }
        });
    }

    [Fact]
    public async Task Guard_ComposedWithFaultGuard_TurnsAStallIntoARetryableFault()
    {
        // The composition that matters in production: an inactivity timeout must surface as a
        // TRANSIENT terminal chunk so same-provider retry and failover both engage, rather than
        // an exception escaping the facade.
        var source = Delayed((5000, "never"));

        var chunks = await DrainAsync(LlmStreamFault.Guard(
            LlmStreamIdleGuard.Apply(source, Fast, CancellationToken.None),
            CancellationToken.None));

        var chunk = Assert.Single(chunks);
        Assert.True(chunk.IsComplete);
        Assert.StartsWith(LlmStreamFault.TransientPrefix, chunk.ErrorMessage!, StringComparison.Ordinal);
        Assert.True(DirectLlmClient.IsErrorTransientRetryable(chunk.ErrorMessage));
    }

    // ── Configuration ─────────────────────────────────────────────────

    [Fact]
    public void Default_AllowsASlowFirstTokenButBoundsMidStreamSilence()
    {
        Assert.True(LlmStreamTimeouts.Default.FirstChunk > LlmStreamTimeouts.Default.BetweenChunks);
        Assert.True(LlmStreamTimeouts.Default.FirstChunk >= TimeSpan.FromMinutes(4));
        Assert.True(LlmStreamTimeouts.Default.BetweenChunks >= TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -5)]
    public void FromSeconds_NonPositiveValues_DisableTheGuard(int first, int between)
    {
        var timeouts = LlmStreamTimeouts.FromSeconds(first, between);

        Assert.Equal(Timeout.InfiniteTimeSpan, timeouts.FirstChunk);
        Assert.Equal(Timeout.InfiniteTimeSpan, timeouts.BetweenChunks);
    }

    [Fact]
    public void FromSeconds_PositiveValues_AreHonoured()
    {
        var timeouts = LlmStreamTimeouts.FromSeconds(120, 45);

        Assert.Equal(TimeSpan.FromSeconds(120), timeouts.FirstChunk);
        Assert.Equal(TimeSpan.FromSeconds(45), timeouts.BetweenChunks);
    }
}
