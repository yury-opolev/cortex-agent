namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Accumulates per-turn latency stages using a monotonic <see cref="TimeProvider"/>
/// clock, then produces a <see cref="TurnLatencySnapshot"/>. One instance per turn.
/// Not thread-safe — a turn is driven by a single session loop.
/// <para>
/// Construction marks turn start (t0, the processing/dequeue point) for the
/// end-to-end measurement. TTFT is captured on the FIRST LLM round only; LLM and
/// tool time accumulate across rounds. Queue-wait (the wall-clock receive→dequeue
/// gap) is computed by the caller and passed to <see cref="Build"/>.
/// </para>
/// </summary>
public sealed class TurnLatencyTracker
{
    private readonly TimeProvider timeProvider;
    private readonly long startTimestamp;

    private long currentRoundStartTimestamp;
    private long ttftMs = -1; // unset
    private long llmMs;
    private long toolMs;
    private int rounds;

    public TurnLatencyTracker(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        this.startTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>Mark the start of an LLM request (one per round).</summary>
    public void MarkLlmRequestStart()
    {
        this.currentRoundStartTimestamp = this.timeProvider.GetTimestamp();
        this.rounds++;
    }

    /// <summary>Mark the first content/tool-call chunk of the current request. Records TTFT once (first round).</summary>
    public void MarkFirstToken()
    {
        if (this.ttftMs < 0)
        {
            this.ttftMs = ElapsedMs(this.currentRoundStartTimestamp);
        }
    }

    /// <summary>Mark the end of the current LLM request; accumulates LLM time.</summary>
    public void MarkLlmRequestEnd()
    {
        this.llmMs += ElapsedMs(this.currentRoundStartTimestamp);
    }

    /// <summary>Add a tool's measured execution time (milliseconds).</summary>
    public void AddToolMs(long ms)
    {
        if (ms > 0)
        {
            this.toolMs += ms;
        }
    }

    /// <summary>
    /// Produce the turn's latency breakdown. <paramref name="queueWaitMs"/> is the
    /// receive→dequeue wait (wall clock), supplied by the caller; everything else is
    /// measured monotonically by this tracker.
    /// </summary>
    public TurnLatencySnapshot Build(long queueWaitMs)
    {
        return new TurnLatencySnapshot(
            QueueWaitMs: queueWaitMs < 0 ? 0 : queueWaitMs,
            TtftMs: this.ttftMs < 0 ? 0 : this.ttftMs,
            LlmMs: this.llmMs,
            ToolMs: this.toolMs,
            E2eMs: ElapsedMs(this.startTimestamp),
            Rounds: this.rounds);
    }

    private long ElapsedMs(long fromTimestamp) =>
        (long)this.timeProvider.GetElapsedTime(fromTimestamp).TotalMilliseconds;
}
