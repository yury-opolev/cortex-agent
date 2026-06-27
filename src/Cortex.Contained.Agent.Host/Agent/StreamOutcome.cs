namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Outcome of streaming one LLM turn, telling the tool loop how to proceed.
/// </summary>
internal enum StreamOutcome
{
    /// <summary>The turn streamed normally; inspect the result's text/tool calls.</summary>
    Completed,

    /// <summary>A context-overflow was handled by emergency compaction; the caller should retry the round (<c>continue</c>).</summary>
    RetryAfterCompaction,

    /// <summary>A non-recoverable LLM error occurred and was already delivered/persisted; the caller should stop the turn (<c>return</c>).</summary>
    Errored,
}
