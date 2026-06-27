using Cortex.Contained.ScenarioEvals.Model;

namespace Cortex.Contained.ScenarioEvals.Abstractions;

/// <summary>
/// Generates in-character messages for a synthetic persona using a separate LLM.
/// </summary>
public interface IActorService
{
    /// <summary>
    /// Generate a single user message as the persona would say it.
    /// </summary>
    /// <param name="persona">The persona definition (name, background, personality).</param>
    /// <param name="facts">Known facts about the persona, by category.</param>
    /// <param name="segment">The current segment (topic, type, hints).</param>
    /// <param name="conversationHistory">Prior exchanges in this segment for context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated message and token usage.</returns>
    Task<(string Message, TokenUsageInfo Tokens)> GenerateMessageAsync(
        PersonaDefinition persona,
        Dictionary<string, string[]> facts,
        SegmentDefinition segment,
        IReadOnlyList<Exchange> conversationHistory,
        CancellationToken ct);
}
