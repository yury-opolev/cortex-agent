namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Bridge → Agent. The agent's spoken answer was cut off by a user barge-in.
/// <see cref="PlayedText"/> is exactly what the user heard, already ending with
/// a "…" marker. The agent records this as the assistant turn instead of the
/// full generated answer, then processes the user's interrupting utterance.
/// </summary>
public sealed record TurnInterruptedNotification
{
    public required string ConversationId { get; init; }
    public required string PlayedText { get; init; }
}
