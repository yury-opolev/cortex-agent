namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Per-turn latency breakdown (all values in milliseconds) produced by
/// <see cref="TurnLatencyTracker"/>. <see cref="TtftMs"/> is the first round's
/// time-to-first-token (what gates the first spoken/typed word); <see cref="LlmMs"/>
/// and <see cref="ToolMs"/> are summed across rounds; <see cref="E2eMs"/> is from
/// turn start to completion; <see cref="QueueWaitMs"/> is the receive→dequeue wait.
/// </summary>
public readonly record struct TurnLatencySnapshot(
    long QueueWaitMs,
    long TtftMs,
    long LlmMs,
    long ToolMs,
    long E2eMs,
    int Rounds);
