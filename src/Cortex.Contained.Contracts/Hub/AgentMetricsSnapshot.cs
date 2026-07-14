namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// An immutable point-in-time view of the agent's operational metrics. Produced inside
/// Agent.Host and carried over the SignalR health/ping path to the Bridge, so operators
/// can observe queue pressure and token-refresh health without attaching a debugger or
/// scraping logs. Lives in Contracts so both processes share a single wire definition.
/// </summary>
/// <param name="TotalMessagesProcessed">Total inbound messages the runtime has fully processed since startup.</param>
/// <param name="ActiveConversations">Number of sessions currently generating a response at snapshot time.</param>
/// <param name="InboundQueueDepth">Current depth of the bounded inbound message queue.</param>
/// <param name="InboundQueuePeak">Highest inbound queue depth observed since startup.</param>
/// <param name="ExtractionQueueDepth">Current depth of the memory-extraction work queue.</param>
/// <param name="ExtractionQueuePeak">Highest extraction queue depth observed since startup.</param>
/// <param name="TokenRefreshSuccesses">Number of successful OAuth token refresh/reload exchanges.</param>
/// <param name="TokenRefreshFailures">Number of failed OAuth token refresh/reload exchanges.</param>
/// <param name="LatencySampleCount">Number of turns in the rolling latency window (0 when none yet).</param>
/// <param name="TtftAvgMs">Average LLM time-to-first-token over the window (ms).</param>
/// <param name="TtftP50Ms">Median TTFT over the window (ms).</param>
/// <param name="TtftP95Ms">95th-percentile TTFT over the window (ms).</param>
/// <param name="E2eAvgMs">Average end-to-end turn latency over the window (ms).</param>
/// <param name="E2eP50Ms">Median end-to-end turn latency over the window (ms).</param>
/// <param name="E2eP95Ms">95th-percentile end-to-end turn latency over the window (ms).</param>
/// <param name="Subagents">
/// Optional point-in-time aggregate of the subagent worker pool (queue depth, active count,
/// staleness, restarts — no prompt/message/result content). Null when the agent did not
/// populate it (e.g. an older agent build, or the subagent subsystem is unavailable).
/// </param>
public sealed record AgentMetricsSnapshot(
    long TotalMessagesProcessed,
    int ActiveConversations,
    int InboundQueueDepth,
    int InboundQueuePeak,
    int ExtractionQueueDepth,
    int ExtractionQueuePeak,
    int TokenRefreshSuccesses,
    int TokenRefreshFailures,
    int LatencySampleCount = 0,
    double TtftAvgMs = 0,
    long TtftP50Ms = 0,
    long TtftP95Ms = 0,
    double E2eAvgMs = 0,
    long E2eP50Ms = 0,
    long E2eP95Ms = 0,
    SubagentAggregateSnapshot? Subagents = null);
