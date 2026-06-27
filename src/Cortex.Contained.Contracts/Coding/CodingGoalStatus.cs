namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Snapshot of a Coda autonomous-goal run, as reported by Coda's <c>session/prompt</c> result.
/// Present only when the session had an active goal that produced a non-<c>None</c> outcome.
/// </summary>
public sealed record CodingGoalStatus
{
    /// <summary>Coda's goal outcome: <c>Met</c>, <c>Unmet</c>, or <c>None</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The judge's last "what still remains" text, or null when the goal was met.</summary>
    public string? Remaining { get; init; }

    /// <summary>How many times Coda was nudged to continue toward the goal.</summary>
    public int Continuations { get; init; }

    /// <summary>Wall-clock seconds elapsed during the goal run.</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>True if the run hit its autonomy budget and escalated for guidance.</summary>
    public bool Escalated { get; init; }

    /// <summary>True if the single bounded budget extension was granted.</summary>
    public bool ExtensionUsed { get; init; }
}
