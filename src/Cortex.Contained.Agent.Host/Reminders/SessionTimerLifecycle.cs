namespace Cortex.Contained.Agent.Host.Reminders;

/// <summary>Internal lifecycle of a timer. Unlike the agent-facing status, this includes cancellation.</summary>
internal enum SessionTimerState
{
    /// <summary>Waiting to fire. The only state from which anything can be claimed.</summary>
    Pending = 0,

    /// <summary>Fired: the intent has been handed to the agent.</summary>
    Fired = 1,

    /// <summary>Cancelled before it could fire. Never reported — a cancelled timer is dropped.</summary>
    Cancelled = 2,
}

/// <summary>
/// The one-shot claim deciding whether a timer fires or is cancelled.
/// <para>
/// Firing and cancelling race for the SAME transition out of <see cref="SessionTimerState.Pending"/>
/// and exactly one may win. Reading a status and then acting on it cannot provide that guarantee at
/// any ordering — the loser's read can always be stale by the time it acts — so the transition
/// itself is the atomic operation and the return value is the permission to act. A cancel that
/// loses must tell the agent the timer already fired; a fire that loses must stay completely
/// silent, enqueuing nothing and releasing nothing.
/// </para>
/// <para>
/// Kept separate from <see cref="SessionTimerService"/> so the interleavings can be tested directly
/// rather than through real timers, where the race is roughly a one-in-twenty-thousand event.
/// </para>
/// </summary>
internal sealed class SessionTimerLifecycle
{
    private readonly Lock gate = new();
    private SessionTimerState state = SessionTimerState.Pending;
    private DateTimeOffset? firedAtUtc;

    /// <summary>One consistent read of state and fire time, so the two can never disagree.</summary>
    public (SessionTimerState State, DateTimeOffset? FiredAtUtc) Snapshot()
    {
        lock (this.gate)
        {
            return (this.state, this.firedAtUtc);
        }
    }

    /// <summary>
    /// Claims the timer for firing. Only the winner may enqueue the intent and release the
    /// conversation's cap slot.
    /// </summary>
    public bool TryClaimFire(DateTimeOffset now)
    {
        lock (this.gate)
        {
            if (this.state != SessionTimerState.Pending)
            {
                return false;
            }

            this.firedAtUtc = now;
            this.state = SessionTimerState.Fired;
            return true;
        }
    }

    /// <summary>
    /// Claims the timer for cancellation. Only the winner may report success and release the
    /// conversation's cap slot.
    /// </summary>
    public bool TryClaimCancel()
    {
        lock (this.gate)
        {
            if (this.state != SessionTimerState.Pending)
            {
                return false;
            }

            this.state = SessionTimerState.Cancelled;
            return true;
        }
    }
}
