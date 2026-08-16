using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Reminders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Covers session timers as INTENTS fired back into the conversation that created them.
/// <para>
/// The previous design froze the exact words at schedule time and spoke them verbatim through a
/// voice deliverer, so a timer could not adapt to anything that had happened since and could only
/// ever speak. It also had no query surface at all: the id returned by the create call was the only
/// record a timer existed, so after a context compaction the agent could neither see nor cancel its
/// own timers.
/// </para>
/// <para>
/// Timers are driven through <see cref="TimeProvider"/>, so advancing the fake clock fires them
/// synchronously. Firing is therefore deterministic here: no sleeps, and no test that passes only
/// because a real timer happened to win a wall-clock race.
/// </para>
/// </summary>
public sealed class SessionTimerServiceTests : IDisposable
{
    private readonly AgentMessageChannel queue = new();
    private readonly FakeTimeProvider time = new(DateTimeOffset.Parse("2026-08-15T10:00:00Z", null));
    private readonly SessionTimerService service;

    public SessionTimerServiceTests()
    {
        this.service = new SessionTimerService(
            this.queue,
            NullLogger<SessionTimerService>.Instance,
            this.time);
    }

    public void Dispose() => this.service.Dispose();

    /// <summary>Advances the clock past a due timer and returns the intent it enqueued.</summary>
    private AgentMessage FireDue(int afterSeconds = 1)
    {
        this.time.Advance(TimeSpan.FromSeconds(afterSeconds));
        Assert.True(this.queue.TryRead(out var message), "expected the due timer to enqueue its intent");
        return message!;
    }

    // ── Firing as an intent, into the originating conversation ───────────────

    [Fact]
    public void Fired_timer_enqueues_the_intent_on_the_conversation_that_created_it()
    {
        this.service.Schedule("discord-voice-1", "discord-voice", delaySeconds: 1, intent: "call the next round");

        var message = this.FireDue();

        // The originating conversation, NOT an isolated one — this is what lets the model act on
        // the live situation instead of a frozen line of text.
        Assert.Equal("discord-voice-1", message.ConversationId);
        Assert.Equal("discord-voice", message.ChannelId);
        Assert.Equal(AgentMessageSource.SessionTimer, message.Source);
        Assert.Contains("call the next round", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fired_intent_tells_the_model_to_act_rather_than_repeat_the_text()
    {
        var text = SessionTimerService.BuildIntentText(
            new SessionTimerService.TimerEntryView("abc123", 90, "call the next round", "round 2"));

        Assert.Contains("abc123", text, StringComparison.Ordinal);
        Assert.Contains("round 2", text, StringComparison.Ordinal);
        Assert.Contains("call the next round", text, StringComparison.Ordinal);
        Assert.Contains("not a line to repeat verbatim", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timer_fires_when_it_is_due_and_not_before()
    {
        this.service.Schedule("conv", "ch", delaySeconds: 60, intent: "x");

        this.time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(this.queue.TryRead(out _), "fired early");

        this.time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(this.queue.TryRead(out _), "did not fire when due");
    }

    [Fact]
    public void A_timer_records_that_it_fired_when_it_was_due()
    {
        // Pins the reported schedule against the clock the service was given. Under the old real
        // Timer plus fake clock, these two came from different clocks entirely and the reported
        // countdown had no relationship to when the callback actually ran.
        var due = this.time.GetUtcNow().AddSeconds(1);
        this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "x");

        this.FireDue();

        var fired = Assert.Single(this.service.List("conv"));
        Assert.Equal(due, fired.FiresAtUtc);
        Assert.Equal(0, fired.SecondsSinceFired);
    }

    [Fact]
    public void Timers_work_outside_voice_conversations()
    {
        // Delivery is now the model's choice, so the old discord-voice-only restriction is gone.
        var id = this.service.Schedule("webchat-default", "webchat", delaySeconds: 30, intent: "check in");

        Assert.NotEmpty(id);
        Assert.Single(this.service.List("webchat-default"));
    }

    // ── Visibility ───────────────────────────────────────────────────────────

    [Fact]
    public void List_reports_pending_timers_so_they_can_be_found_without_the_create_result()
    {
        this.service.Schedule("conv", "ch", delaySeconds: 60, intent: "second", description: "later");
        var firstId = this.service.Schedule("conv", "ch", delaySeconds: 10, intent: "first", description: "sooner");

        var pending = this.service.List("conv");

        Assert.Equal(2, pending.Count);

        // Soonest first, so "what fires next" reads off the top.
        Assert.Equal(firstId, pending[0].Id);
        Assert.Equal("first", pending[0].Intent);
        Assert.Equal("sooner", pending[0].Description);
        Assert.Equal(10, pending[0].SecondsRemaining);
        Assert.Equal(60, pending[1].SecondsRemaining);
    }

    [Fact]
    public void List_is_scoped_to_one_conversation()
    {
        this.service.Schedule("conv-a", "ch", delaySeconds: 30, intent: "a");
        this.service.Schedule("conv-b", "ch", delaySeconds: 30, intent: "b");

        Assert.Equal("a", Assert.Single(this.service.List("conv-a")).Intent);
        Assert.Equal("b", Assert.Single(this.service.List("conv-b")).Intent);
    }

    [Fact]
    public void List_counts_down_as_time_passes()
    {
        this.service.Schedule("conv", "ch", delaySeconds: 60, intent: "x");

        this.time.Advance(TimeSpan.FromSeconds(45));

        Assert.Equal(15, Assert.Single(this.service.List("conv")).SecondsRemaining);
    }

    [Fact]
    public void List_puts_still_pending_timers_ahead_of_fired_ones()
    {
        // What is still going to happen matters more than what already has.
        this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "fired");
        this.service.Schedule("conv", "ch", delaySeconds: 600, intent: "pending");

        this.FireDue();

        var listed = this.service.List("conv");
        Assert.Equal(2, listed.Count);
        Assert.Equal("pending", listed[0].Intent);
        Assert.Equal(SessionTimerStatus.Pending, listed[0].Status);
        Assert.Equal("fired", listed[1].Intent);
        Assert.Equal(SessionTimerStatus.Fired, listed[1].Status);
    }

    [Fact]
    public void List_reports_how_long_ago_a_timer_fired()
    {
        // "Fired 2 seconds ago" and "fired 100 seconds ago" call for very different behaviour.
        this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "x");
        this.FireDue();

        this.time.Advance(TimeSpan.FromSeconds(30));

        var fired = Assert.Single(this.service.List("conv"));
        Assert.Equal(30, fired.SecondsSinceFired);
        Assert.Equal(0, fired.SecondsRemaining);
    }

    [Fact]
    public void List_is_empty_when_nothing_is_scheduled()
    {
        Assert.Empty(this.service.List("conv"));
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public void Cancelled_timer_disappears_from_the_listing_and_never_fires()
    {
        var id = this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "should not fire");

        Assert.Equal(SessionTimerCancelOutcome.Cancelled, this.service.Cancel("conv", id));
        Assert.Empty(this.service.List("conv"));

        this.time.Advance(TimeSpan.FromSeconds(600));
        Assert.False(this.queue.TryRead(out _), "a cancelled timer fired anyway");
    }

    [Fact]
    public void Cancelling_an_unknown_timer_reports_failure_rather_than_pretending()
    {
        Assert.Equal(SessionTimerCancelOutcome.NotFound, this.service.Cancel("conv", "nope"));
        Assert.Equal(SessionTimerCancelOutcome.NotFound, this.service.Cancel("conv", ""));
    }

    [Fact]
    public void A_timer_cannot_be_cancelled_from_another_conversation()
    {
        // The service is a process-wide singleton, so an id alone must not grant reach into a
        // conversation the agent cannot even list.
        var id = this.service.Schedule("conv-a", "ch", delaySeconds: 600, intent: "a");

        Assert.Equal(SessionTimerCancelOutcome.NotFound, this.service.Cancel("conv-b", id));
        Assert.Single(this.service.List("conv-a"));
    }

    [Fact]
    public void The_cap_is_per_conversation()
    {
        for (var i = 0; i < SessionTimerService.PerConversationCap; i++)
        {
            this.service.Schedule("conv-a", "ch", delaySeconds: 600, intent: $"a{i}");
        }

        // A busy conversation must not starve every other one.
        this.service.Schedule("conv-b", "ch", delaySeconds: 600, intent: "b");
        Assert.Equal(1, this.service.ActiveCount("conv-b"));
    }

    [Fact]
    public void A_fired_timer_cannot_be_cancelled_and_says_so()
    {
        // Once it has fired the agent already has the intent, so "cancel" must not report success.
        // Reporting NotFound would be equally wrong — it implies the timer never existed.
        var id = this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "already gone");
        this.FireDue();

        Assert.Equal(SessionTimerCancelOutcome.AlreadyFired, this.service.Cancel("conv", id));
        Assert.Equal(SessionTimerStatus.Fired, Assert.Single(this.service.List("conv")).Status);
    }

    [Fact]
    public void A_fired_timer_stays_visible_then_is_pruned()
    {
        this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "x");
        this.FireDue();

        // Visible while the agent might still ask about it...
        Assert.Equal(SessionTimerStatus.Fired, Assert.Single(this.service.List("conv")).Status);

        // ...then it stops cluttering the listing.
        this.time.Advance(SessionTimerService.FiredRetention + TimeSpan.FromSeconds(1));
        Assert.Empty(this.service.List("conv"));
    }

    [Fact]
    public void A_pruned_timer_reports_NotFound_rather_than_AlreadyFired()
    {
        // Retention has to be a property of the timer, not of whether the agent happened to call
        // list. Otherwise cancel and list disagree: list shows nothing, cancel says "already
        // fired" — and fired entries pile up forever in a conversation that never lists.
        var id = this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "x");
        this.FireDue();

        this.time.Advance(SessionTimerService.FiredRetention + TimeSpan.FromSeconds(1));

        Assert.Equal(SessionTimerCancelOutcome.NotFound, this.service.Cancel("conv", id));
    }

    // ── The per-conversation cap ─────────────────────────────────────────────

    [Fact]
    public void Scheduling_beyond_the_per_conversation_cap_is_rejected()
    {
        for (var i = 0; i < SessionTimerService.PerConversationCap; i++)
        {
            this.service.Schedule("conv", "ch", delaySeconds: 600, intent: $"intent {i}");
        }

        Assert.Equal(SessionTimerService.PerConversationCap, this.service.ActiveCount("conv"));
        Assert.Throws<InvalidOperationException>(
            () => this.service.Schedule("conv", "ch", delaySeconds: 600, intent: "over cap"));
    }

    [Fact]
    public void Cancelling_frees_exactly_one_slot()
    {
        var ids = new List<string>();
        for (var i = 0; i < SessionTimerService.PerConversationCap; i++)
        {
            ids.Add(this.service.Schedule("conv", "ch", delaySeconds: 600, intent: $"intent {i}"));
        }

        Assert.Equal(SessionTimerCancelOutcome.Cancelled, this.service.Cancel("conv", ids[0]));

        // Asserted directly rather than via "did the next Schedule throw?", which can only ever
        // catch over-counting. Releasing a slot twice makes Schedule succeed, so a throw-based
        // assertion reads a leaked slot as success.
        Assert.Equal(SessionTimerService.PerConversationCap - 1, this.service.ActiveCount("conv"));

        Assert.Equal(SessionTimerCancelOutcome.NotFound, this.service.Cancel("conv", ids[0]));
        Assert.Equal(SessionTimerService.PerConversationCap - 1, this.service.ActiveCount("conv"));
    }

    [Fact]
    public void A_fired_timer_frees_its_slot_against_the_cap_even_while_still_listed()
    {
        // Retention is a visibility aid, not a reservation — it must not consume capacity.
        for (var i = 0; i < SessionTimerService.PerConversationCap; i++)
        {
            this.service.Schedule("conv", "ch", delaySeconds: 1, intent: $"intent {i}");
        }

        this.time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, this.service.ActiveCount("conv"));

        var listed = this.service.List("conv");
        Assert.Equal(SessionTimerService.PerConversationCap, listed.Count);
        Assert.All(listed, t => Assert.Equal(SessionTimerStatus.Fired, t.Status));

        this.service.Schedule("conv", "ch", delaySeconds: 600, intent: "fits because the others fired");
        Assert.Equal(SessionTimerService.PerConversationCap + 1, this.service.List("conv").Count);
    }

    [Fact]
    public void A_timer_that_fires_frees_its_slot_exactly_once()
    {
        var id = this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "x");
        Assert.Equal(1, this.service.ActiveCount("conv"));

        this.FireDue();
        Assert.Equal(0, this.service.ActiveCount("conv"));

        // A cancel arriving after the fire must not release a second slot, which would silently
        // grant the conversation an extra timer for the rest of the process's life.
        Assert.Equal(SessionTimerCancelOutcome.AlreadyFired, this.service.Cancel("conv", id));
        Assert.Equal(0, this.service.ActiveCount("conv"));
    }

    [Fact]
    public void The_cap_counter_keeps_agreeing_with_what_is_actually_pending()
    {
        // The cap tests otherwise read the same counter the cap enforces, so they can only catch
        // drift within it, never divergence between the counter and reality.
        this.service.Schedule("conv", "ch", delaySeconds: 600, intent: "stays");
        var cancelled = this.service.Schedule("conv", "ch", delaySeconds: 600, intent: "cancelled");
        this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "fires");

        this.service.Cancel("conv", cancelled);
        this.FireDue();
        this.service.Schedule("conv", "ch", delaySeconds: 600, intent: "added after");

        var pending = this.service.List("conv").Count(t => t.Status == SessionTimerStatus.Pending);
        Assert.Equal(pending, this.service.ActiveCount("conv"));
        Assert.Equal(2, pending);
    }

    [Fact]
    public void Cancelling_races_the_fire_without_ever_doing_both()
    {
        // The race C1 lived in: a cancel issued exactly as the timer comes due — which is precisely
        // when an agent cancels. Whichever side wins, the other must do nothing at all: reporting
        // "cancelled" and then delivering the intent anyway gives the agent a positive assurance
        // that is immediately contradicted, and releases the cap slot twice.
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var channel = new AgentMessageChannel();
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-15T10:00:00Z", null));
            using var racing = new SessionTimerService(channel, NullLogger<SessionTimerService>.Instance, clock);

            var id = racing.Schedule("conv", "ch", delaySeconds: 1, intent: "contested");

            using var ready = new ManualResetEventSlim(false);
            var outcome = SessionTimerCancelOutcome.NotFound;

            var cancelling = new Thread(() =>
            {
                ready.Wait();
                outcome = racing.Cancel("conv", id);
            });
            var firing = new Thread(() =>
            {
                ready.Wait();
                clock.Advance(TimeSpan.FromSeconds(1));
            });

            cancelling.Start();
            firing.Start();
            ready.Set();
            cancelling.Join();
            firing.Join();

            var delivered = channel.TryRead(out _);
            var reportedCancelled = outcome == SessionTimerCancelOutcome.Cancelled;

            Assert.True(
                delivered ^ reportedCancelled,
                $"attempt {attempt}: delivered={delivered} reportedCancelled={reportedCancelled}, "
                + $"outcome={outcome} — exactly one side must take effect.");

            // Released once, by whichever side won.
            Assert.Equal(0, racing.ActiveCount("conv"));
        }
    }

    // ── Shutdown ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disposing_stops_timers_from_firing()
    {
        var channel = new AgentMessageChannel();
        var scoped = new SessionTimerService(channel, NullLogger<SessionTimerService>.Instance, this.time);
        scoped.Schedule("conv", "ch", delaySeconds: 1, intent: "should never arrive");

        await scoped.DisposeAsync();

        this.time.Advance(TimeSpan.FromSeconds(600));
        Assert.False(channel.TryRead(out _), "a timer fired after the service was disposed");
    }

    [Fact]
    public async Task Scheduling_after_disposal_is_refused()
    {
        await this.service.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(
            () => this.service.Schedule("conv", "ch", delaySeconds: 60, intent: "too late"));
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        await this.service.DisposeAsync();
        await this.service.DisposeAsync();
        this.service.Dispose();
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SessionTimerService.MinDelaySeconds - 1)]
    [InlineData(SessionTimerService.MaxDelaySeconds + 1)]
    public void Delay_outside_the_supported_range_is_rejected(int delaySeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => this.service.Schedule("conv", "ch", delaySeconds, intent: "x"));
    }

    [Fact]
    public void An_empty_intent_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => this.service.Schedule("conv", "ch", 30, intent: "   "));
    }
}
