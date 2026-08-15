namespace Cortex.Contained.Agent.Host.Llm.Providers;

/// <summary>
/// Which inactivity budget a stalled provider stream breached.
/// </summary>
internal enum LlmStreamStallPhase
{
    /// <summary>
    /// The stream went silent before producing any content. Time-to-first-token grows with
    /// prompt size, so this is usually a genuinely slow start rather than a fault — and it is
    /// still recoverable: nothing has been yielded, so the facade can retry and fail over.
    /// </summary>
    FirstChunk,

    /// <summary>
    /// The stream went silent after content had already been delivered. This is the damaging
    /// case: the facade's retry and failover are pre-content only, so only the agent loop can
    /// recover it.
    /// </summary>
    BetweenChunks,

    /// <summary>
    /// The stream ran past its absolute wall-clock ceiling. Distinct from the idle phases
    /// because the stream was NOT silent: it was alive and going nowhere, which the idle
    /// budgets alone can no longer detect now that heartbeats re-arm them.
    /// </summary>
    MaxDuration,
}
