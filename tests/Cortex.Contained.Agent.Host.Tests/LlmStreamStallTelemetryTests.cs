using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Llm.Providers;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Telemetry for an inactivity-watchdog breach.
/// <para>
/// Root cause context (2026-08-15): the only trace the incident left was one line —
/// "Stream transport fault: LLM stream produced no data for 120s." — with no task id, no model,
/// no idle phase, no indication of how big the context was. It was indistinguishable in the log
/// from a genuine network fault, which is why diagnosis needed a source dive. A breach must
/// report enough to be diagnosed from the log alone.
/// </para>
/// </summary>
public class LlmStreamStallTelemetryTests
{
    private static readonly LlmStreamTimeouts Fast =
        new() { FirstChunk = TimeSpan.FromMilliseconds(400), BetweenChunks = TimeSpan.FromMilliseconds(200) };

    private static LlmStreamContext Context() => new()
    {
        ConversationId = "subagent-sa-a61a32e",
        RequestId = "req-7",
        Model = "claude-opus-5",
        Provider = "anthropic",
        PromptChars = 480_000,
    };

    private static async IAsyncEnumerable<LlmStreamChunk> Script(
        params (int DelayMs, LlmStreamChunk Chunk)[] steps)
    {
        foreach (var (delayMs, chunk) in steps)
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            yield return chunk;
        }
    }

    private static async Task DrainIgnoringTimeoutAsync(IAsyncEnumerable<LlmStreamChunk> source)
    {
        try
        {
            await foreach (var _ in source.ConfigureAwait(false))
            {
                // drain
            }
        }
        catch (TimeoutException)
        {
            // the breach under test
        }
    }

    private sealed class RecordingObserver : ILlmStreamStallObserver
    {
        public List<LlmStreamStallReport> Reports { get; } = [];

        public void OnStall(LlmStreamStallReport report) => this.Reports.Add(report);
    }

    // ── A breach is reported ───────────────────────────────────────────

    [Fact]
    public async Task Breach_BeforeAnyContent_IsReportedAsTheFirstChunkPhase()
    {
        var observer = new RecordingObserver();

        await DrainIgnoringTimeoutAsync(LlmStreamIdleGuard.Apply(
            Script((5000, new LlmStreamChunk { ContentDelta = "never" })),
            Fast, CancellationToken.None, Context(), observer));

        var report = Assert.Single(observer.Reports);
        Assert.Equal(LlmStreamStallPhase.FirstChunk, report.Phase);
        Assert.False(report.ContentEmitted);
    }

    [Fact]
    public async Task Breach_AfterContent_IsReportedAsTheBetweenChunksPhase()
    {
        // The production case. "ContentEmitted = true" is the single most diagnostic field:
        // it is exactly what makes the fault unretryable by DirectLlmClient.
        var observer = new RecordingObserver();

        await DrainIgnoringTimeoutAsync(LlmStreamIdleGuard.Apply(
            Script(
                (5, new LlmStreamChunk { ContentDelta = "Let me start by " }),
                (5000, new LlmStreamChunk { ContentDelta = "never" })),
            Fast, CancellationToken.None, Context(), observer));

        var report = Assert.Single(observer.Reports);
        Assert.Equal(LlmStreamStallPhase.BetweenChunks, report.Phase);
        Assert.True(report.ContentEmitted);
    }

    // ── The fields needed to diagnose ──────────────────────────────────

    [Fact]
    public async Task Breach_CarriesTheIdentityOfTheStalledRequest()
    {
        var observer = new RecordingObserver();

        await DrainIgnoringTimeoutAsync(LlmStreamIdleGuard.Apply(
            Script((5000, new LlmStreamChunk { ContentDelta = "never" })),
            Fast, CancellationToken.None, Context(), observer));

        var report = Assert.Single(observer.Reports);
        Assert.Equal("subagent-sa-a61a32e", report.ConversationId);
        Assert.Equal("req-7", report.RequestId);
        Assert.Equal("claude-opus-5", report.Model);
        Assert.Equal("anthropic", report.Provider);
    }

    [Fact]
    public async Task Breach_CarriesTheContextSize()
    {
        // The whole hypothesis was context-size driven; without this the correlation could not
        // be seen from the log.
        var observer = new RecordingObserver();

        await DrainIgnoringTimeoutAsync(LlmStreamIdleGuard.Apply(
            Script((5000, new LlmStreamChunk { ContentDelta = "never" })),
            Fast, CancellationToken.None, Context(), observer));

        Assert.Equal(480_000, Assert.Single(observer.Reports).PromptChars);
    }

    [Fact]
    public async Task Breach_CarriesTheIdleBudgetAndTheActualElapsedIdleTime()
    {
        var observer = new RecordingObserver();

        await DrainIgnoringTimeoutAsync(LlmStreamIdleGuard.Apply(
            Script((5000, new LlmStreamChunk { ContentDelta = "never" })),
            Fast, CancellationToken.None, Context(), observer));

        var report = Assert.Single(observer.Reports);
        Assert.Equal(Fast.FirstChunk, report.Budget);
        Assert.True(
            report.Elapsed >= Fast.FirstChunk,
            $"elapsed idle time ({report.Elapsed}) should be at least the budget ({Fast.FirstChunk}).");
        Assert.True(report.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Breach_CarriesHowMuchArrivedBeforeTheStall()
    {
        var observer = new RecordingObserver();

        await DrainIgnoringTimeoutAsync(LlmStreamIdleGuard.Apply(
            Script(
                (5, new LlmStreamChunk { ContentDelta = "abc" }),
                (5, new LlmStreamChunk { IsKeepAlive = true }),
                (5, new LlmStreamChunk { ContentDelta = "de" }),
                (5000, new LlmStreamChunk { ContentDelta = "never" })),
            Fast, CancellationToken.None, Context(), observer));

        // Content-bearing chunks and heartbeats are counted separately: "how much real output
        // arrived" and "was the provider signalling liveness" are different questions.
        var report = Assert.Single(observer.Reports);
        Assert.Equal(2, report.ChunksReceived);
        Assert.Equal(1, report.KeepAlivesReceived);
        Assert.Equal(5, report.ContentCharsReceived);
    }

    // ── It must not fire when nothing is wrong ─────────────────────────

    [Fact]
    public async Task HealthyStream_ReportsNothing()
    {
        var observer = new RecordingObserver();

        await DrainIgnoringTimeoutAsync(LlmStreamIdleGuard.Apply(
            Script(
                (5, new LlmStreamChunk { ContentDelta = "a" }),
                (5, new LlmStreamChunk { ContentDelta = "b" })),
            Fast, CancellationToken.None, Context(), observer));

        Assert.Empty(observer.Reports);
    }

    [Fact]
    public async Task NoObserver_StillWorks()
    {
        // Telemetry is optional plumbing — its absence must never change stream behaviour.
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in LlmStreamIdleGuard.Apply(
                Script((5000, new LlmStreamChunk { ContentDelta = "never" })),
                Fast, CancellationToken.None).ConfigureAwait(false))
            {
                // drain
            }
        });
    }

    [Fact]
    public async Task ThrowingObserver_DoesNotMaskTheBreach()
    {
        // A broken telemetry sink must not turn a diagnosable stall into a mystery exception.
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in LlmStreamIdleGuard.Apply(
                Script((5000, new LlmStreamChunk { ContentDelta = "never" })),
                Fast, CancellationToken.None, Context(), new ThrowingObserver()).ConfigureAwait(false))
            {
                // drain
            }
        });
    }

    private sealed class ThrowingObserver : ILlmStreamStallObserver
    {
        public void OnStall(LlmStreamStallReport report)
            => throw new InvalidOperationException("telemetry sink is broken");
    }

    // ── Metrics ────────────────────────────────────────────────────────

    [Fact]
    public void AgentMetrics_CountsStallsSplitByPhase()
    {
        var metrics = new AgentMetrics();

        metrics.RecordStreamStall(LlmStreamStallPhase.FirstChunk);
        metrics.RecordStreamStall(LlmStreamStallPhase.BetweenChunks);
        metrics.RecordStreamStall(LlmStreamStallPhase.BetweenChunks);

        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.StreamFirstChunkStalls);
        Assert.Equal(2, snapshot.StreamIdleStalls);
    }

    [Fact]
    public void AgentMetrics_NoStalls_ReportsZero()
    {
        var snapshot = new AgentMetrics().Snapshot();

        Assert.Equal(0, snapshot.StreamFirstChunkStalls);
        Assert.Equal(0, snapshot.StreamIdleStalls);
    }
}
