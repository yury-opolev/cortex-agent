namespace Cortex.Contained.Agent.Host.Agent.Autonomy;

/// <summary>
/// Durable consumed budget snapshot for rehydrating an autonomous run after restart.
/// </summary>
internal readonly record struct GoalBudgetConsumed
{
    public GoalBudgetConsumed(TimeSpan elapsed, int continuationsUsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Consumed elapsed time cannot be negative.");
        }

        if (continuationsUsed < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(continuationsUsed),
                "Consumed continuations cannot be negative.");
        }

        this.Elapsed = elapsed;
        this.ContinuationsUsed = continuationsUsed;
    }

    public TimeSpan Elapsed { get; }

    public int ContinuationsUsed { get; }
}
