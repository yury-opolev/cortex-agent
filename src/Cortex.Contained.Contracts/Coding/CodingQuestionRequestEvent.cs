namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Push event when the coding agent asks the user a question (coda <c>request/question</c>).
/// The session blocks until <see cref="CodingRespondRequest"/> arrives.
/// </summary>
public sealed record CodingQuestionRequestEvent
{
    public required string SessionId { get; init; }

    public required string RequestId { get; init; }

    public required string Question { get; init; }

    public IReadOnlyList<string> Options { get; init; } = [];

    public bool MultiSelect { get; init; }
}
