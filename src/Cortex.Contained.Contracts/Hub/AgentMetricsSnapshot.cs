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
public sealed record AgentMetricsSnapshot(
    long TotalMessagesProcessed,
    int ActiveConversations,
    int InboundQueueDepth,
    int InboundQueuePeak,
    int ExtractionQueueDepth,
    int ExtractionQueuePeak,
    int TokenRefreshSuccesses,
    int TokenRefreshFailures);
