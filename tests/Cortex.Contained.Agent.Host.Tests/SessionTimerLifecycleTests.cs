using Cortex.Contained.Agent.Host.Reminders;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Covers the one-shot claim that decides whether a timer fires or is cancelled.
/// <para>
/// Firing and cancelling race for the SAME transition out of <see cref="SessionTimerState.Pending"/>,
/// and exactly one may win. If both could act, the agent would be told "cancelled" and then receive
/// the intent anyway — a positive assurance it can act on, immediately contradicted. Checking a
/// status and then acting cannot give that guarantee, however the check is ordered, so the
/// transition itself has to be the atomic operation.
/// </para>
/// </summary>
public sealed class SessionTimerLifecycleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-15T10:00:00Z", null);

    [Fact]
    public void A_fresh_lifecycle_is_pending_and_has_not_fired()
    {
        var lifecycle = new SessionTimerLifecycle();

        var (state, firedAt) = lifecycle.Snapshot();

        Assert.Equal(SessionTimerState.Pending, state);
        Assert.Null(firedAt);
    }

    [Fact]
    public void Claiming_the_fire_records_when_it_fired()
    {
        var lifecycle = new SessionTimerLifecycle();

        Assert.True(lifecycle.TryClaimFire(Now));

        var (state, firedAt) = lifecycle.Snapshot();
        Assert.Equal(SessionTimerState.Fired, state);
        Assert.Equal(Now, firedAt);
    }

    [Fact]
    public void Cancelling_after_it_has_fired_is_refused()
    {
        // The whole point of the state machine: the agent already has the intent.
        var lifecycle = new SessionTimerLifecycle();
        Assert.True(lifecycle.TryClaimFire(Now));

        Assert.False(lifecycle.TryClaimCancel());
        Assert.Equal(SessionTimerState.Fired, lifecycle.Snapshot().State);
    }

    [Fact]
    public void Firing_after_it_has_been_cancelled_is_refused()
    {
        // The timer callback can already be queued on the thread pool when the cancel lands, so the
        // callback MUST re-check rather than assume it is still wanted.
        var lifecycle = new SessionTimerLifecycle();
        Assert.True(lifecycle.TryClaimCancel());

        Assert.False(lifecycle.TryClaimFire(Now));

        var (state, firedAt) = lifecycle.Snapshot();
        Assert.Equal(SessionTimerState.Cancelled, state);
        Assert.Null(firedAt);
    }

    [Fact]
    public void Only_the_first_of_several_fire_claims_wins()
    {
        // Guards against a duplicate callback enqueuing the intent twice.
        var lifecycle = new SessionTimerLifecycle();

        Assert.True(lifecycle.TryClaimFire(Now));
        Assert.False(lifecycle.TryClaimFire(Now.AddSeconds(1)));
        Assert.Equal(Now, lifecycle.Snapshot().FiredAtUtc);
    }

    [Fact]
    public void Only_the_first_of_several_cancel_claims_wins()
    {
        // Guards against the cap slot being freed twice by a double cancel.
        var lifecycle = new SessionTimerLifecycle();

        Assert.True(lifecycle.TryClaimCancel());
        Assert.False(lifecycle.TryClaimCancel());
    }

    [Fact]
    public void Firing_and_cancelling_at_the_same_instant_produce_exactly_one_winner()
    {
        // This is the race that matters, and it is the reason the claim exists. Run it many times:
        // a single pass proves nothing about an interleaving that only sometimes occurs.
        for (var attempt = 0; attempt < 2000; attempt++)
        {
            var lifecycle = new SessionTimerLifecycle();
            using var ready = new ManualResetEventSlim(false);
            bool firedWon = false;
            bool cancelWon = false;

            var firing = new Thread(() =>
            {
                ready.Wait();
                firedWon = lifecycle.TryClaimFire(Now);
            });
            var cancelling = new Thread(() =>
            {
                ready.Wait();
                cancelWon = lifecycle.TryClaimCancel();
            });

            firing.Start();
            cancelling.Start();
            ready.Set();
            firing.Join();
            cancelling.Join();

            Assert.True(
                firedWon ^ cancelWon,
                $"attempt {attempt}: fired={firedWon} cancelled={cancelWon} — exactly one must win.");

            var state = lifecycle.Snapshot().State;
            Assert.Equal(firedWon ? SessionTimerState.Fired : SessionTimerState.Cancelled, state);
        }
    }
}
