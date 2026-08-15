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

    public void Dispose()
    {
        this.service.Dispose();
        GC.SuppressFinalize(this);
    }

    private static bool TryDrain(AgentMessageChannel queue, out AgentMessage? message)
    {
        // The timer callback enqueues from a thread-pool thread, so poll briefly.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (queue.TryRead(out message))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        message = null;
        return false;
    }

    // ── Firing as an intent, into the originating conversation ───────────────

    [Fact]
    public void Fired_timer_enqueues_the_intent_on_the_conversation_that_created_it()
    {
        this.service.Schedule("discord-voice-1", "discord-voice", delaySeconds: 1, intent: "call the next round");

        Assert.True(TryDrain(this.queue, out var message));

        // The originating conversation, NOT an isolated one — this is what lets the model act on
        // the live situation instead of a frozen line of text.
        Assert.Equal("discord-voice-1", message!.ConversationId);
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
    public void List_is_empty_when_nothing_is_scheduled()
    {
        Assert.Empty(this.service.List("conv"));
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public void Cancelled_timer_disappears_from_the_listing_and_never_fires()
    {
        var id = this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "should not fire");

        Assert.True(this.service.Cancel(id));
        Assert.Empty(this.service.List("conv"));

        Thread.Sleep(1200);
        Assert.False(this.queue.TryRead(out _));
    }

    [Fact]
    public void Cancelling_an_unknown_timer_reports_failure_rather_than_pretending()
    {
        Assert.False(this.service.Cancel("nope"));
        Assert.False(this.service.Cancel(""));
    }

    [Fact]
    public void Cancelling_frees_a_slot_against_the_per_conversation_cap()
    {
        var ids = new List<string>();
        for (var i = 0; i < SessionTimerService.PerConversationCap; i++)
        {
            ids.Add(this.service.Schedule("conv", "ch", delaySeconds: 600, intent: $"intent {i}"));
        }

        Assert.Throws<InvalidOperationException>(
            () => this.service.Schedule("conv", "ch", delaySeconds: 600, intent: "over cap"));

        Assert.True(this.service.Cancel(ids[0]));
        this.service.Schedule("conv", "ch", delaySeconds: 600, intent: "fits now");
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
