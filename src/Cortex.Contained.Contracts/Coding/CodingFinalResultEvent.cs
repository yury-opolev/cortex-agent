namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Push event from Bridge to Agent Host when a session completes its current task.
/// </summary>
public sealed record CodingFinalResultEvent
{
    public required string SessionId { get; init; }

    public required string TaskId { get; init; }

    public required string FinalText { get; init; }

    public IReadOnlyList<CodingToolCall> ToolCalls { get; init; } = [];
}
