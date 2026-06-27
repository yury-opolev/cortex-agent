using System.Collections.Concurrent;
using Cortex.Contained.Agent.Host.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SessionReminderServiceTests : IDisposable
{
    private const string VoiceConversationId = "discord-voice-tenant-1";
    private const string VoiceChannelId = "discord-voice";

    private readonly FakeVoiceCueDeliverer deliverer = new();
    private readonly SessionReminderService service;

    public SessionReminderServiceTests()
    {
        this.service = new SessionReminderService(
            this.deliverer,
            NullLogger<SessionReminderService>.Instance);
    }

    public void Dispose()
    {
        this.service.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Schedule_NonVoiceConversation_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            this.service.Schedule("webchat-default", "webchat-default", 10, text: "hello"));
    }

    [Fact]
    public void Schedule_DelayBelowMinimum_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            this.service.Schedule(
                VoiceConversationId,
                VoiceChannelId,
                SessionReminderService.MinDelaySeconds - 1,
                text: "hello"));
    }

    [Fact]
    public void Schedule_DelayAboveMaximum_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            this.service.Schedule(
                VoiceConversationId,
                VoiceChannelId,
                SessionReminderService.MaxDelaySeconds + 1,
                text: "hello"));
    }

    [Fact]
    public void Schedule_EmptyText_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            this.service.Schedule(VoiceConversationId, VoiceChannelId, 1, text: "   "));
    }

    [Fact]
    public async Task Schedule_ValidInput_FiresAfterDelay()
    {
        var id = this.service.Schedule(VoiceConversationId, VoiceChannelId, 1, text: "rest after set 3");

        Assert.False(string.IsNullOrWhiteSpace(id));

        // Wait up to 3s for the deliverer to receive the call.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline && this.deliverer.Calls.IsEmpty)
        {
            await Task.Delay(50);
        }

        Assert.Single(this.deliverer.Calls);
        var call = this.deliverer.Calls.Single();
        Assert.Equal(VoiceConversationId, call.ConversationId);
        Assert.Equal(VoiceChannelId, call.ChannelId);
        Assert.Equal("rest after set 3", call.Text);
    }

    [Fact]
    public async Task Cancel_BeforeFire_PreventsDelivery()
    {
        var id = this.service.Schedule(VoiceConversationId, VoiceChannelId, 2, text: "should not fire");

        var cancelled = this.service.Cancel(id);
        Assert.True(cancelled);

        // Wait past the original delay; assert nothing was delivered.
        await Task.Delay(3000);
        Assert.Empty(this.deliverer.Calls);
    }

    [Fact]
    public void Cancel_UnknownId_ReturnsFalse()
    {
        Assert.False(this.service.Cancel("nope-not-a-real-id"));
    }

    [Fact]
    public void Schedule_ExceedsPerConversationCap_Throws()
    {
        // Default cap is 10. Schedule 10, then the 11th throws.
        for (var i = 0; i < 10; i++)
        {
            this.service.Schedule(VoiceConversationId, VoiceChannelId, 60, text: "cue");
        }

        Assert.Throws<InvalidOperationException>(() =>
            this.service.Schedule(VoiceConversationId, VoiceChannelId, 60, text: "cue"));
    }

    [Fact]
    public void Schedule_DifferentConversations_IndependentCaps()
    {
        for (var i = 0; i < 10; i++)
        {
            this.service.Schedule(VoiceConversationId, VoiceChannelId, 60, text: "cue");
        }

        // Different conversation should still accept new reminders.
        var id = this.service.Schedule("discord-voice-tenant-2", VoiceChannelId, 60, text: "cue");
        Assert.False(string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public async Task Dispose_DisposesAllTimers()
    {
        this.service.Schedule(VoiceConversationId, VoiceChannelId, 1, text: "cue 1");
        this.service.Schedule(VoiceConversationId, VoiceChannelId, 1, text: "cue 2");

        this.service.Dispose();

        // Wait past the original delay; nothing should be delivered because timers are disposed.
        await Task.Delay(1500);
        Assert.Empty(this.deliverer.Calls);
    }

    // ── Fake deliverer ────────────────────────────────────────────────────

    private sealed class FakeVoiceCueDeliverer : IVoiceCueDeliverer
    {
        public ConcurrentBag<(string ConversationId, string ChannelId, string Text)> Calls { get; } = new();

        public Task SpeakAsync(string conversationId, string channelId, string text, CancellationToken cancellationToken)
        {
            this.Calls.Add((conversationId, channelId, text));
            return Task.CompletedTask;
        }
    }
}
