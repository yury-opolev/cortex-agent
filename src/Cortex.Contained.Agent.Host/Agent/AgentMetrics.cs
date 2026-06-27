using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Thread-safe, process-wide collector of the agent's operational metrics. Registered
/// as a singleton and updated from the few hot-path choke points: the inbound message
/// queue (<see cref="AgentMessageChannel"/>), the memory-extraction queue
/// (<c>MemoryExtractionService</c>), the message processing loop (<c>AgentRuntime</c>),
/// and the OAuth token-refresh sites (<c>DirectLlmClient</c>).
/// <para>
/// All mutators are lock-free (<see cref="Interlocked"/>) so they add negligible
/// overhead to the paths they instrument. <see cref="Snapshot"/> reads a coherent set
/// of the current values; because reads are not taken under a single lock the snapshot
/// is eventually-consistent rather than a strict transactional view, which is the
/// appropriate trade-off for observability counters.
/// </para>
/// </summary>
public sealed class AgentMetrics
{
    private long totalMessagesProcessed;
    private int inboundQueueDepth;
    private int inboundQueuePeak;
    private int extractionQueueDepth;
    private int extractionQueuePeak;
    private int tokenRefreshSuccesses;
    private int tokenRefreshFailures;

    private volatile Func<int>? activeConversationsProvider;

    /// <summary>
    /// Registers a callback that reports the number of currently-active conversations.
    /// Called once at startup by <c>AgentRuntime</c>; the value is read lazily on each
    /// <see cref="Snapshot"/> so it always reflects live session state.
    /// </summary>
    /// <param name="provider">Returns the active conversation count, or <see langword="null"/> to clear.</param>
    public void SetActiveConversationsProvider(Func<int>? provider)
    {
        this.activeConversationsProvider = provider;
    }

    /// <summary>Increments the count of fully-processed inbound messages.</summary>
    public void IncrementMessagesProcessed()
    {
        Interlocked.Increment(ref this.totalMessagesProcessed);
    }

    /// <summary>
    /// Records the current depth of the inbound message queue and advances the peak
    /// high-water mark if this depth exceeds it.
    /// </summary>
    /// <param name="depth">The current queue depth.</param>
    public void ObserveInboundQueueDepth(int depth)
    {
        Interlocked.Exchange(ref this.inboundQueueDepth, depth);
        UpdatePeak(ref this.inboundQueuePeak, depth);
    }

    /// <summary>
    /// Records the current depth of the memory-extraction queue and advances the peak
    /// high-water mark if this depth exceeds it.
    /// </summary>
    /// <param name="depth">The current queue depth.</param>
    public void ObserveExtractionQueueDepth(int depth)
    {
        Interlocked.Exchange(ref this.extractionQueueDepth, depth);
        UpdatePeak(ref this.extractionQueuePeak, depth);
    }

    /// <summary>Increments the count of successful OAuth token refresh/reload exchanges.</summary>
    public void IncrementTokenRefreshSuccess()
    {
        Interlocked.Increment(ref this.tokenRefreshSuccesses);
    }

    /// <summary>Increments the count of failed OAuth token refresh/reload exchanges.</summary>
    public void IncrementTokenRefreshFailure()
    {
        Interlocked.Increment(ref this.tokenRefreshFailures);
    }

    /// <summary>Captures the current values as an immutable snapshot.</summary>
    public AgentMetricsSnapshot Snapshot()
    {
        var activeConversations = 0;
        var provider = this.activeConversationsProvider;
        if (provider is not null)
        {
            try
            {
                activeConversations = provider();
            }
#pragma warning disable CA1031 // A faulty provider must never break the health snapshot.
            catch
            {
                activeConversations = 0;
            }
#pragma warning restore CA1031
        }

        return new AgentMetricsSnapshot(
            TotalMessagesProcessed: Interlocked.Read(ref this.totalMessagesProcessed),
            ActiveConversations: activeConversations,
            InboundQueueDepth: Volatile.Read(ref this.inboundQueueDepth),
            InboundQueuePeak: Volatile.Read(ref this.inboundQueuePeak),
            ExtractionQueueDepth: Volatile.Read(ref this.extractionQueueDepth),
            ExtractionQueuePeak: Volatile.Read(ref this.extractionQueuePeak),
            TokenRefreshSuccesses: Volatile.Read(ref this.tokenRefreshSuccesses),
            TokenRefreshFailures: Volatile.Read(ref this.tokenRefreshFailures));
    }

    /// <summary>
    /// Atomically raises <paramref name="peak"/> to <paramref name="candidate"/> if the
    /// candidate is larger, using a lock-free compare-and-swap retry loop.
    /// </summary>
    private static void UpdatePeak(ref int peak, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref peak);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref peak, candidate, current) == current)
            {
                return;
            }
        }
    }
}
