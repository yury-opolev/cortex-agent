namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Bridge → Agent. The session's goal configuration after a <see cref="CodingSetGoalRequest"/>.
/// Fields are null when the goal was cleared or a budget was left at its default.
/// </summary>
public sealed record CodingSetGoalResponse
{
    public required string SessionId { get; init; }

    /// <summary>The active goal text after the mutation, or null if the goal was cleared.</summary>
    public string? Goal { get; init; }

    /// <summary>The active wall-clock budget (suffix form), or null if unset/default.</summary>
    public string? MaxDuration { get; init; }

    /// <summary>The active continuation budget, or null if unset/default.</summary>
    public int? MaxContinuations { get; init; }
}
