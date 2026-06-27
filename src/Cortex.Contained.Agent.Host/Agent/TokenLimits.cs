namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Standard MaxTokens limits for LLM calls by output size category.
/// Use these instead of hardcoded numbers for consistency and maintainability.
/// </summary>
public static class TokenLimits
{
    /// <summary>
    /// Tiny output: yes/no, single word, short label.
    /// Examples: topic detection "same"/"different", dedup check.
    /// </summary>
    public const int Tiny = 64;

    /// <summary>
    /// Small output: a few sentences, short JSON array, search queries.
    /// Examples: topic label, search query generation, running summary.
    /// </summary>
    public const int Small = 512;

    /// <summary>
    /// Medium output: structured extraction, consolidation results.
    /// Examples: memory extraction, fact consolidation, compaction summary.
    /// </summary>
    public const int Medium = 4096;

    /// <summary>
    /// Full model output: use <see cref="IModelProvider.MaxOutputTokens"/>.
    /// For agent responses and subagent work — anything where the model needs
    /// full capacity. Fallback value when ModelProvider is not available.
    /// </summary>
    public const int FullFallback = 8192;

    /// <summary>
    /// Resolve the max output tokens from the model provider, with fallback.
    /// </summary>
    public static int ResolveMaxOutput(IModelProvider? modelProvider)
        => modelProvider?.MaxOutputTokens > 0 ? modelProvider.MaxOutputTokens : FullFallback;
}
