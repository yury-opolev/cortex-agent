namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Agent → Bridge. Set, update, or clear a session's autonomous goal and budget in-place
/// (persist-until-cleared). A null/empty <see cref="Goal"/> clears the active goal. The new
/// configuration takes effect from the next message sent to the session.
/// </summary>
public sealed record CodingSetGoalRequest
{
    public required string SessionId { get; init; }

    /// <summary>The objective text, or null/empty to clear the current goal (disable autonomy).</summary>
    public string? Goal { get; init; }

    /// <summary>Optional wall-clock budget (suffix form, e.g. <c>30m</c>, <c>2h</c>, <c>1d</c>); null keeps the default.</summary>
    public string? MaxDuration { get; init; }

    /// <summary>Optional continuation (turn) budget; null keeps the default.</summary>
    public int? MaxContinuations { get; init; }
}
