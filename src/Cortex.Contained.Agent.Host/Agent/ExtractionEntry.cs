namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Lightweight record capturing a single message for the extraction buffer.
/// Completely separate from <see cref="Cortex.Contained.Contracts.Llm.LlmMessage"/>
/// to avoid leaking internal metadata to LLM APIs.
/// </summary>
public sealed record ExtractionEntry
{
    /// <summary>Message role: "user" or "assistant".</summary>
    public required string Role { get; init; }

    /// <summary>Text content of the message.</summary>
    public required string Content { get; init; }

    /// <summary>When the message was created.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
