namespace Cortex.Contained.Channels.Discord.Tests;

using Microsoft.Extensions.Logging.Abstractions;

public sealed class DiscordEnrollmentProgressNotifierTests
{
    private const string TenantId = "tenant-1";
    private const ulong TextChannelId = 9001_0000_0001UL;

    [Fact]
    public async Task ReportAsync_NoTrackedChannel_DoesNotPost()
    {
        var sender = new FakeChannelSender();
        var notifier = MakeNotifier(sender);

        // No TrackInteractionChannel call — channel not registered.
        await notifier.ReportAsync(TenantId, "Enrolling", 0, 3);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ReportAsync_EnrolledState_PostsAndUntracks()
    {
        var sender = new FakeChannelSender();
        var notifier = MakeNotifier(sender);
        notifier.TrackInteractionChannel(TenantId, TextChannelId);

        await notifier.ReportAsync(TenantId, "Enrolled", 3, 3);

        Assert.Single(sender.Sent);
        var (channelId, text) = sender.Sent[0];
        Assert.Equal(TextChannelId, channelId);
        Assert.Contains("enrolled", text, StringComparison.OrdinalIgnoreCase);

        // Subsequent post should be swallowed — tenant was untracked on terminal state.
        await notifier.ReportAsync(TenantId, "Enrolled", 3, 3);
        Assert.Single(sender.Sent); // Still just 1
    }

    [Fact]
    public async Task ReportAsync_EnrollingMidStream_PostsCapturedNofK()
    {
        var sender = new FakeChannelSender();
        var notifier = MakeNotifier(sender);
        notifier.TrackInteractionChannel(TenantId, TextChannelId);

        await notifier.ReportAsync(TenantId, "Enrolling", 2, 3);

        Assert.Single(sender.Sent);
        var (_, text) = sender.Sent[0];
        Assert.Contains("2/3", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untrack_RemovesTracking_NoFurtherPosts()
    {
        var sender = new FakeChannelSender();
        var notifier = MakeNotifier(sender);
        notifier.TrackInteractionChannel(TenantId, TextChannelId);

        notifier.Untrack(TenantId);
        await notifier.ReportAsync(TenantId, "Enrolling", 1, 3);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ReportAsync_UnknownState_PostsCancelledAndUntracks()
    {
        var sender = new FakeChannelSender();
        var notifier = MakeNotifier(sender);
        notifier.TrackInteractionChannel(TenantId, TextChannelId);

        await notifier.ReportAsync(TenantId, "Unknown", 0, 3);

        Assert.Single(sender.Sent);
        var (_, text) = sender.Sent[0];
        Assert.Contains("cancelled", text, StringComparison.OrdinalIgnoreCase);

        // Untracked on terminal state — subsequent call is swallowed.
        await notifier.ReportAsync(TenantId, "Enrolling", 1, 3);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task ReportAsync_ConfirmingState_PostsConfirmMessage()
    {
        var sender = new FakeChannelSender();
        var notifier = MakeNotifier(sender);
        notifier.TrackInteractionChannel(TenantId, TextChannelId);

        await notifier.ReportAsync(TenantId, "Confirming", 3, 3);

        Assert.Single(sender.Sent);
        Assert.Contains("confirm", sender.Sent[0].Text, StringComparison.OrdinalIgnoreCase);
        // Non-terminal — tenant is still tracked.
        await notifier.ReportAsync(TenantId, "Confirming", 3, 3);
        Assert.Equal(2, sender.Sent.Count);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static DiscordEnrollmentProgressNotifier MakeNotifier(IDiscordChannelSender sender)
        => new(sender, NullLogger<DiscordEnrollmentProgressNotifier>.Instance);

    private sealed class FakeChannelSender : IDiscordChannelSender
    {
        public List<(ulong ChannelId, string Text)> Sent { get; } = [];

        public ValueTask<bool> TrySendAsync(ulong channelId, string text)
        {
            this.Sent.Add((channelId, text));
            return ValueTask.FromResult(true);
        }
    }
}
