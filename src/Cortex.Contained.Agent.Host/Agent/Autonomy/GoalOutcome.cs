namespace Cortex.Contained.Agent.Host.Agent.Autonomy;

/// <summary>
/// Terminal reason for an autonomous subagent goal.
/// </summary>
internal enum GoalOutcome
{
    None,
    Met,
    GenuinelyBlocked,
    Stalled,
    BudgetExhausted,
}
