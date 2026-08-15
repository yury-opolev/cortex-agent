using Cortex.Contained.Agent.Host.Llm.Providers;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// The absolute per-stream deadline.
/// <para>
/// Once provider heartbeats were allowed to re-arm the idle budgets, those budgets stopped
/// bounding the stream at all: a provider pinging forever, or a model stuck emitting thinking
/// deltas, would hold the request, the connection and the subagent slot open indefinitely. That
/// is a strictly worse failure than the premature kill the loosening was meant to fix, because
/// it is silent and never terminates. <see cref="LlmStreamTimeouts.MaxDuration"/> is the backstop.
/// </para>
/// </summary>
public class LlmStreamMaxDurationTests
{
    private static async IAsyncEnumerable<LlmStreamChunk> HeartbeatsForever(int everyMs)
    {
        while (true)
        {
            await Task.Delay(everyMs).ConfigureAwait(false);
            yield return new LlmStreamChunk { IsKeepAlive = true };
        }
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ContentThenHeartbeatsForever(int everyMs)
    {
        await Task.Delay(everyMs).ConfigureAwait(false);
        yield return new LlmStreamChunk { ContentDelta = "starting..." };

        while (true)
        {
            await Task.Delay(everyMs).ConfigureAwait(false);
            yield return new LlmStreamChunk { IsKeepAlive = true };
        }
    }

    private sealed class RecordingObserver : ILlmStreamStallObserver
    {
        public List<LlmStreamStallReport> Reports { get; } = [];

        public void OnStall(LlmStreamStallReport report) => this.Reports.Add(report);
    }

    private static async Task<TimeoutException> DrainExpectingTimeoutAsync(
        IAsyncEnumerable<LlmStreamChunk> source)
        => await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in source.ConfigureAwait(false))
            {
                // drain
            }
        });

    // ── The hazard the keep-alive fix introduced ───────────────────────

    [Fact]
    public async Task AnEndlesslyHeartbeatingStream_IsStillTerminated()
    {
        // Idle budgets are generous enough that ONLY the absolute deadline can end this.
        var timeouts = new LlmStreamTimeouts
        {
            FirstChunk = TimeSpan.FromSeconds(30),
            BetweenChunks = TimeSpan.FromSeconds(30),
            MaxDuration = TimeSpan.FromMilliseconds(600),
        };

        var ex = await DrainExpectingTimeoutAsync(
            LlmStreamIdleGuard.Apply(HeartbeatsForever(50), timeouts, CancellationToken.None));

        Assert.Contains("maximum duration", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStreamThatEmitsContentThenHeartbeatsForever_IsStillTerminated()
    {
        var timeouts = new LlmStreamTimeouts
        {
            FirstChunk = TimeSpan.FromSeconds(30),
            BetweenChunks = TimeSpan.FromSeconds(30),
            MaxDuration = TimeSpan.FromMilliseconds(600),
        };

        var ex = await DrainExpectingTimeoutAsync(
            LlmStreamIdleGuard.Apply(
                ContentThenHeartbeatsForever(50), timeouts, CancellationToken.None));

        Assert.Contains("maximum duration", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeadlineBreach_IsReportedUnderItsOwnPhase()
    {
        var timeouts = new LlmStreamTimeouts
        {
            FirstChunk = TimeSpan.FromSeconds(30),
            BetweenChunks = TimeSpan.FromSeconds(30),
            MaxDuration = TimeSpan.FromMilliseconds(600),
        };
        var observer = new RecordingObserver();

        await DrainExpectingTimeoutAsync(LlmStreamIdleGuard.Apply(
            HeartbeatsForever(50), timeouts, CancellationToken.None, null, observer));

        var report = Assert.Single(observer.Reports);
        Assert.Equal(LlmStreamStallPhase.MaxDuration, report.Phase);
        Assert.Equal(timeouts.MaxDuration, report.Budget);

        // The diagnostic that distinguishes it from a silent hang: it was NOT silent.
        Assert.True(
            report.KeepAlivesReceived > 0,
            "a deadline breach on a heartbeating stream should record the heartbeats");
    }

    [Fact]
    public async Task ADeadlineBreach_IsTransientSoTheUsualRecoveryPathsEngage()
    {
        var timeouts = new LlmStreamTimeouts
        {
            FirstChunk = TimeSpan.FromSeconds(30),
            BetweenChunks = TimeSpan.FromSeconds(30),
            MaxDuration = TimeSpan.FromMilliseconds(600),
        };

        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in LlmStreamFault.Guard(
            LlmStreamIdleGuard.Apply(HeartbeatsForever(50), timeouts, CancellationToken.None),
            CancellationToken.None).ConfigureAwait(false))
        {
            chunks.Add(chunk);
        }

        var chunk2 = Assert.Single(chunks);
        Assert.True(chunk2.IsComplete);
        Assert.StartsWith(LlmStreamFault.TransientPrefix, chunk2.ErrorMessage!, StringComparison.Ordinal);
    }

    // ── It must not fire on healthy streams ────────────────────────────

    [Fact]
    public async Task AStreamThatFinishesInsideTheDeadline_IsUntouched()
    {
        var timeouts = new LlmStreamTimeouts
        {
            FirstChunk = TimeSpan.FromSeconds(5),
            BetweenChunks = TimeSpan.FromSeconds(5),
            MaxDuration = TimeSpan.FromSeconds(5),
        };

        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in LlmStreamIdleGuard
            .Apply(Short(), timeouts, CancellationToken.None).ConfigureAwait(false))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
    }

    private static async IAsyncEnumerable<LlmStreamChunk> Short()
    {
        await Task.Delay(10).ConfigureAwait(false);
        yield return new LlmStreamChunk { ContentDelta = "a" };
        await Task.Delay(10).ConfigureAwait(false);
        yield return new LlmStreamChunk { ContentDelta = "b" };
    }

    // ── Configuration ──────────────────────────────────────────────────

    [Fact]
    public void MaxDuration_HasAGenerousDefault()
    {
        // A backstop, not a latency budget: it must never be the thing that ends a healthy
        // long-running turn.
        Assert.True(LlmStreamTimeouts.Default.MaxDuration >= TimeSpan.FromMinutes(15));
        Assert.True(LlmStreamTimeouts.Default.MaxDuration > LlmStreamTimeouts.Default.BetweenChunks);
    }

    [Fact]
    public void MaxDuration_IsTunable()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(900),
            LlmStreamTimeouts.FromSeconds(600, 300, 900).MaxDuration);
    }

    [Fact]
    public void MaxDuration_ZeroKeepsTheDefault_NegativeDisablesIt()
    {
        // 0 means "unset" here rather than "off", because an operator who never heard of this
        // knob must still get the backstop.
        Assert.Equal(
            LlmStreamTimeouts.Default.MaxDuration,
            LlmStreamTimeouts.FromSeconds(600, 300, 0).MaxDuration);
        Assert.Equal(
            Timeout.InfiniteTimeSpan,
            LlmStreamTimeouts.FromSeconds(600, 300, -1).MaxDuration);
    }

    [Fact]
    public void AgentConfig_DefaultsLeaveTheBackstopOn()
    {
        var config = new AgentConfig();

        Assert.Equal(0, config.LlmStreamMaxDurationSeconds);
        Assert.Equal(
            LlmStreamTimeouts.Default.MaxDuration,
            LlmStreamTimeouts.FromSeconds(
                config.LlmFirstTokenTimeoutSeconds,
                config.LlmStreamIdleTimeoutSeconds,
                config.LlmStreamMaxDurationSeconds).MaxDuration);
    }

    [Fact]
    public void FirstChunkBudget_IsStillTheMoreGenerousOfTheTwoIdleBudgets()
    {
        // The documented two-budget design: time-to-first-token grows with prompt size, so it
        // must never be bounded more tightly than mid-stream silence.
        Assert.True(LlmStreamTimeouts.Default.FirstChunk > LlmStreamTimeouts.Default.BetweenChunks);
        Assert.True(new AgentConfig().LlmFirstTokenTimeoutSeconds > new AgentConfig().LlmStreamIdleTimeoutSeconds);
    }
}
