namespace Cortex.Contained.Agent.Host.Agent.Autonomy;

/// <summary>
/// Supervisor decision after a subagent finishes one bounded loop.
/// <para>
/// The two cases carry different data, so modelling them as separate records prevents callers from
/// accidentally creating a terminal verdict without an outcome or a continuation without the
/// judge's next instruction.
/// </para>
/// </summary>
internal abstract record GoalVerdict
{
    private GoalVerdict()
    {
    }

    public static GoalVerdict Continue(string remaining) => new ContinueVerdict(remaining);

    public static GoalVerdict Stop(GoalOutcome outcome, string report) => new StopVerdict(outcome, report);

    internal sealed record ContinueVerdict : GoalVerdict
    {
        public ContinueVerdict(string remaining)
        {
            this.Remaining = remaining ?? throw new ArgumentNullException(nameof(remaining));
        }

        public string Remaining { get; }
    }

    internal sealed record StopVerdict : GoalVerdict
    {
        public StopVerdict(GoalOutcome outcome, string report)
        {
            if (outcome == GoalOutcome.None)
            {
                throw new ArgumentException("A terminal verdict must have a terminal outcome.", nameof(outcome));
            }

            this.Outcome = outcome;
            this.Report = report ?? throw new ArgumentNullException(nameof(report));
        }

        public GoalOutcome Outcome { get; }

        public string Report { get; }
    }
}
