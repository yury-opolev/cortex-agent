namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Extension point for filtering tools per conversation. The registry asks
/// each gate "which tools should be hidden for this conversation?" and unions
/// the results. Used for state-conditional filtering of the voice-enrollment
/// tool family, but the abstraction is intentionally generic so other gates
/// (e.g. role-based) could be added later.
/// </summary>
public interface IConversationToolGate
{
    /// <summary>
    /// Returns the set of tool names that should be omitted from the
    /// definition list given to the LLM for <paramref name="conversationId"/>.
    /// Implementations should be synchronous and fast — the registry calls
    /// this on the hot path of every tool-list computation.
    /// </summary>
    IReadOnlySet<string> GetHiddenTools(string? conversationId);
}
