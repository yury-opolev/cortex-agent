namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Reply to a permission ask, question, or plan-approval request raised by a coding session.
/// </summary>
public sealed record CodingRespondRequest
{
    public required string RequestId { get; init; }

    /// <summary>
    /// For permission: <c>"allow_once"</c> | <c>"allow_always"</c> | <c>"deny"</c>.
    /// For a question: the chosen option or free-form answer text.
    /// For plan approval: <c>"approve"</c> | <c>"reject"</c>.
    /// </summary>
    public required string Response { get; init; }
}
