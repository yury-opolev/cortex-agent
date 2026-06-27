using System.ComponentModel.DataAnnotations;

namespace Cortex.Contained.Contracts.Config;

/// <summary>
/// Tuning knobs for conversation-history summarization (the
/// <c>CompactConversationAsync</c> path inside the agent runtime).
/// Distinct from <c>MemoryCompactionOptions</c>, which controls the
/// <em>memory store</em> dedup pass.
/// </summary>
public sealed class ConversationCompactionConfig
{
    /// <summary>
    /// Recent user turns to preserve verbatim at the end of the conversation
    /// when summarization runs. The tail (these N user turns plus any
    /// assistant/tool messages between and after them) is kept intact only
    /// when its combined token count fits inside
    /// <see cref="PreserveBudgetRatio"/> of the model's context window —
    /// otherwise the older tool-round preservation rule is used as a fallback.
    /// Set to 0 to disable user-turn-based preservation entirely. Default: 4.
    /// </summary>
    [Range(0, 100)]
    public int PreserveRecentTurns { get; set; } = 4;

    /// <summary>
    /// Fraction of the model's context window the preserved tail is allowed
    /// to occupy. If the tail is bigger than this, it is summarized along
    /// with the older messages instead of being preserved. Default: 0.25 (25%).
    /// </summary>
    [Range(0.0, 1.0)]
    public double PreserveBudgetRatio { get; set; } = 0.25;
}
