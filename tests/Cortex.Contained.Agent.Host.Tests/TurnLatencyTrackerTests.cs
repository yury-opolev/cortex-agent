using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Pins the per-turn latency stage accumulator: TTFT is captured on the FIRST
/// LLM round only; LLM and tool time accumulate across rounds; e2e is measured
/// from turn start; queue-wait is passed through.
/// </summary>
public class TurnLatencyTrackerTests
{
    /// <summary>Controllable monotonic clock: 1 timestamp unit = 1 ms.</summary>
    private sealed class FakeClock : TimeProvider
    {
        public long Ts;
        public override long GetTimestamp() => this.Ts;
        public override long TimestampFrequency => 1000;
    }

    [Fact]
    public void MultiRound_AccumulatesAndCapturesTtftOnFirstRoundOnly()
    {
        var clock = new FakeClock { Ts = 0 };
        var tracker = new TurnLatencyTracker(clock); // t0 = 0

        // Round 1
        clock.Ts = 10; tracker.MarkLlmRequestStart();
        clock.Ts = 4710; tracker.MarkFirstToken();      // ttft = 4700
        clock.Ts = 6210; tracker.MarkLlmRequestEnd();   // llm += 6200
        tracker.AddToolMs(120);

        // Round 2
        clock.Ts = 6300; tracker.MarkLlmRequestStart();
        clock.Ts = 6400; tracker.MarkFirstToken();      // ttft already set -> ignored
        clock.Ts = 6500; tracker.MarkLlmRequestEnd();   // llm += 200 -> 6400

        clock.Ts = 6600;
        var s = tracker.Build(queueWaitMs: 40);

        Assert.Equal(40, s.QueueWaitMs);
        Assert.Equal(4700, s.TtftMs);
        Assert.Equal(6400, s.LlmMs);
        Assert.Equal(120, s.ToolMs);
        Assert.Equal(6600, s.E2eMs);
        Assert.Equal(2, s.Rounds);
    }

    [Fact]
    public void NoFirstToken_TtftZero()
    {
        var clock = new FakeClock { Ts = 0 };
        var tracker = new TurnLatencyTracker(clock);

        clock.Ts = 5; tracker.MarkLlmRequestStart();
        clock.Ts = 100; tracker.MarkLlmRequestEnd();
        clock.Ts = 110;
        var s = tracker.Build(queueWaitMs: 0);

        Assert.Equal(0, s.TtftMs);
        Assert.Equal(95, s.LlmMs);
        Assert.Equal(1, s.Rounds);
        Assert.Equal(110, s.E2eMs);
    }

    [Fact]
    public void NoTools_ToolMsZero()
    {
        var clock = new FakeClock { Ts = 0 };
        var tracker = new TurnLatencyTracker(clock);

        clock.Ts = 0; tracker.MarkLlmRequestStart();
        clock.Ts = 50; tracker.MarkFirstToken();
        clock.Ts = 200; tracker.MarkLlmRequestEnd();
        clock.Ts = 200;
        var s = tracker.Build(queueWaitMs: 7);

        Assert.Equal(0, s.ToolMs);
        Assert.Equal(50, s.TtftMs);
        Assert.Equal(7, s.QueueWaitMs);
    }
}
