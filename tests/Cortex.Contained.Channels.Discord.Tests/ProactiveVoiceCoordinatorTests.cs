using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Channels.Discord.Tests;

public class ProactiveVoiceCoordinatorTests
{
    private const int DefaultQueueCap = 5;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Enqueue_WhileConnected_CallsSpeak_NoRing()
    {
        var callbacks = new FakeCallbacks();
        var time = new FakeTimeProvider();
        var coordinator = BuildCoordinator(callbacks, time);

        var outcome = await coordinator.EnqueueAsync("hello", userInVoice: true, CancellationToken.None);

        Assert.Equal(ProactiveDelivery.Spoken, outcome.Delivery);
        Assert.Null(outcome.Reason);
        Assert.Equal(["hello"], callbacks.Spoken);
        Assert.Empty(callbacks.InvitesCreated);
        Assert.Empty(callbacks.RingDmsSent);
        Assert.Equal(0, callbacks.JoinCalls);
    }

    [Fact]
    public async Task QueueCapExceeded_DropsOldest()
    {
        var callbacks = new FakeCallbacks();
        var time = new FakeTimeProvider();
        var coordinator = BuildCoordinator(callbacks, time, queueCap: 2);
        await coordinator.EnqueueAsync("m1", userInVoice: false, CancellationToken.None);
        await coordinator.EnqueueAsync("m2", userInVoice: false, CancellationToken.None);
        await coordinator.EnqueueAsync("m3", userInVoice: false, CancellationToken.None);

        await coordinator.OnUserJoinedAsync(CancellationToken.None);

        Assert.Equal(["m2", "m3"], callbacks.Spoken);
    }

    [Fact]
    public async Task UserJoin_CancelsRingTimer_NoFallbackFires()
    {
        var callbacks = new FakeCallbacks();
        var time = new FakeTimeProvider();
        var coordinator = BuildCoordinator(callbacks, time);
        await coordinator.EnqueueAsync("only", userInVoice: false, CancellationToken.None);

        await coordinator.OnUserJoinedAsync(CancellationToken.None);
        time.Advance(DefaultTtl + TimeSpan.FromSeconds(5));
        await coordinator.WaitForPendingFallbackAsync();

        Assert.Equal(["only"], callbacks.Spoken);
        Assert.Empty(callbacks.VoiceMessageDmsSent);
        Assert.Equal(0, callbacks.LeaveCalls);
    }

    [Fact]
    public async Task RingTtl_Elapses_SendsVoiceMessageDms_LeavesVoice_ClearsQueue()
    {
        var callbacks = new FakeCallbacks();
        var time = new FakeTimeProvider();
        var coordinator = BuildCoordinator(callbacks, time);
        await coordinator.EnqueueAsync("first", userInVoice: false, CancellationToken.None);
        await coordinator.EnqueueAsync("second", userInVoice: false, CancellationToken.None);

        time.Advance(DefaultTtl);
        await coordinator.WaitForPendingFallbackAsync();

        Assert.Equal(["first", "second"], callbacks.VoiceMessageDmsSent);
        Assert.Empty(callbacks.Spoken);
        Assert.Equal(1, callbacks.LeaveCalls);

        // Late join after TTL should be a no-op (queue already drained).
        await coordinator.OnUserJoinedAsync(CancellationToken.None);
        Assert.Empty(callbacks.Spoken);
    }

    [Fact]
    public async Task UserJoin_DuringRing_DrainsQueueViaSpeak_InOrder()
    {
        var callbacks = new FakeCallbacks();
        var time = new FakeTimeProvider();
        var coordinator = BuildCoordinator(callbacks, time);
        await coordinator.EnqueueAsync("first", userInVoice: false, CancellationToken.None);
        await coordinator.EnqueueAsync("second", userInVoice: false, CancellationToken.None);

        await coordinator.OnUserJoinedAsync(CancellationToken.None);

        Assert.Equal(["first", "second"], callbacks.Spoken);
        Assert.Empty(callbacks.VoiceMessageDmsSent);
        Assert.Equal(0, callbacks.LeaveCalls);
    }

    [Fact]
    public async Task SecondEnqueue_DuringActiveRing_DoesNotReRing()
    {
        var callbacks = new FakeCallbacks();
        var time = new FakeTimeProvider();
        var coordinator = BuildCoordinator(callbacks, time);

        var first = await coordinator.EnqueueAsync("first", userInVoice: false, CancellationToken.None);
        var second = await coordinator.EnqueueAsync("second", userInVoice: false, CancellationToken.None);

        Assert.Equal(ProactiveDelivery.Rang, first.Delivery);
        Assert.Equal(ProactiveDelivery.Queued, second.Delivery);
        Assert.Equal(1, callbacks.JoinCalls);
        Assert.Single(callbacks.InvitesCreated);
        Assert.Single(callbacks.RingDmsSent);
    }

    [Fact]
    public async Task Enqueue_WhileDisconnected_StartsRing()
    {
        var callbacks = new FakeCallbacks { InviteUrl = "https://discord.gg/ring1" };
        var time = new FakeTimeProvider();
        var coordinator = BuildCoordinator(callbacks, time);

        var outcome = await coordinator.EnqueueAsync("hi", userInVoice: false, CancellationToken.None);

        Assert.Equal(ProactiveDelivery.Rang, outcome.Delivery);
        Assert.Equal(1, callbacks.JoinCalls);
        Assert.Equal(["https://discord.gg/ring1"], callbacks.InvitesCreated);
        Assert.Equal(["https://discord.gg/ring1"], callbacks.RingDmsSent);
        Assert.Empty(callbacks.Spoken);
    }

    [Fact]
    public async Task UserInVoice_True_GoesToSpeakPath_NoRingDm()
    {
        // Reproduces the "user in voice but bot not yet connected" race window —
        // before this fix, the coordinator would ring even though the user was
        // sitting in the channel.
        var speakCalls = new List<string>();
        var ringDmCalls = 0;

        var coordinator = new ProactiveVoiceCoordinator(
            joinVoice: ct => Task.CompletedTask,
            createInvite: ct => Task.FromResult("https://invite.url"),
            sendRingDm: (url, ct) => { ringDmCalls++; return Task.CompletedTask; },
            sendVoiceMessageDm: (text, ct) => Task.CompletedTask,
            speak: (text, hint, ct) => { speakCalls.Add(text); return Task.CompletedTask; },
            leaveVoice: ct => Task.CompletedTask,
            ringTtl: TimeSpan.FromMinutes(1),
            queueCap: 5,
            logger: NullLogger.Instance,
            timeProvider: TimeProvider.System);

        await coordinator.EnqueueAsync("Hello user.", userInVoice: true, CancellationToken.None);

        Assert.Equal(["Hello user."], speakCalls);
        Assert.Equal(0, ringDmCalls);
    }

    [Fact]
    public async Task EnqueueAsync_SpeakThrows_ReturnsDropped()
    {
        var coordinator = new ProactiveVoiceCoordinator(
            joinVoice: _ => Task.CompletedTask,
            createInvite: _ => Task.FromResult("https://invite.url"),
            sendRingDm: (_, _) => Task.CompletedTask,
            sendVoiceMessageDm: (_, _) => Task.CompletedTask,
            speak: (_, _, _) => throw new VoiceNotConnectedException("not connected"),
            leaveVoice: _ => Task.CompletedTask,
            ringTtl: DefaultTtl,
            queueCap: DefaultQueueCap,
            logger: NullLogger<ProactiveVoiceCoordinator>.Instance,
            timeProvider: TimeProvider.System);

        var outcome = await coordinator.EnqueueAsync("hello", userInVoice: true, CancellationToken.None);

        Assert.Equal(ProactiveDelivery.Dropped, outcome.Delivery);
        Assert.Contains("not connected", outcome.Reason);
    }

    [Fact]
    public async Task EnqueueAsync_JoinVoiceThrows_ReturnsDroppedAndAbandonsRing()
    {
        var coordinator = new ProactiveVoiceCoordinator(
            joinVoice: _ => throw new InvalidOperationException("voice join failed"),
            createInvite: _ => Task.FromResult("https://invite.url"),
            sendRingDm: (_, _) => Task.CompletedTask,
            sendVoiceMessageDm: (_, _) => Task.CompletedTask,
            speak: (_, _, _) => Task.CompletedTask,
            leaveVoice: _ => Task.CompletedTask,
            ringTtl: DefaultTtl,
            queueCap: DefaultQueueCap,
            logger: NullLogger<ProactiveVoiceCoordinator>.Instance,
            timeProvider: TimeProvider.System);

        var outcome = await coordinator.EnqueueAsync("hi", userInVoice: false, CancellationToken.None);

        Assert.Equal(ProactiveDelivery.Dropped, outcome.Delivery);
        Assert.Contains("voice join failed", outcome.Reason);
    }

    [Fact]
    public async Task EnqueueAsync_CreateInviteThrows_ReturnsDropped()
    {
        var coordinator = new ProactiveVoiceCoordinator(
            joinVoice: _ => Task.CompletedTask,
            createInvite: _ => throw new InvalidOperationException("invite failed"),
            sendRingDm: (_, _) => Task.CompletedTask,
            sendVoiceMessageDm: (_, _) => Task.CompletedTask,
            speak: (_, _, _) => Task.CompletedTask,
            leaveVoice: _ => Task.CompletedTask,
            ringTtl: DefaultTtl,
            queueCap: DefaultQueueCap,
            logger: NullLogger<ProactiveVoiceCoordinator>.Instance,
            timeProvider: TimeProvider.System);

        var outcome = await coordinator.EnqueueAsync("hi", userInVoice: false, CancellationToken.None);

        Assert.Equal(ProactiveDelivery.Dropped, outcome.Delivery);
        Assert.Contains("invite failed", outcome.Reason);
    }

    [Fact]
    public async Task EnqueueAsync_SendRingDmThrows_ReturnsDropped()
    {
        var coordinator = new ProactiveVoiceCoordinator(
            joinVoice: _ => Task.CompletedTask,
            createInvite: _ => Task.FromResult("https://invite.url"),
            sendRingDm: (_, _) => throw new InvalidOperationException("dm blocked"),
            sendVoiceMessageDm: (_, _) => Task.CompletedTask,
            speak: (_, _, _) => Task.CompletedTask,
            leaveVoice: _ => Task.CompletedTask,
            ringTtl: DefaultTtl,
            queueCap: DefaultQueueCap,
            logger: NullLogger<ProactiveVoiceCoordinator>.Instance,
            timeProvider: TimeProvider.System);

        var outcome = await coordinator.EnqueueAsync("hi", userInVoice: false, CancellationToken.None);

        Assert.Equal(ProactiveDelivery.Dropped, outcome.Delivery);
        Assert.Contains("dm blocked", outcome.Reason);
    }

    [Fact]
    public async Task EnqueueAsync_RingFailedThenRetry_RingsAgain()
    {
        var dmSucceeds = false;
        var coordinator = new ProactiveVoiceCoordinator(
            joinVoice: _ => Task.CompletedTask,
            createInvite: _ => Task.FromResult("https://invite.url"),
            sendRingDm: (_, _) => dmSucceeds ? Task.CompletedTask : throw new InvalidOperationException("dm blocked"),
            sendVoiceMessageDm: (_, _) => Task.CompletedTask,
            speak: (_, _, _) => Task.CompletedTask,
            leaveVoice: _ => Task.CompletedTask,
            ringTtl: DefaultTtl,
            queueCap: DefaultQueueCap,
            logger: NullLogger<ProactiveVoiceCoordinator>.Instance,
            timeProvider: TimeProvider.System);

        var first = await coordinator.EnqueueAsync("a", userInVoice: false, CancellationToken.None);
        Assert.Equal(ProactiveDelivery.Dropped, first.Delivery);

        dmSucceeds = true;
        var second = await coordinator.EnqueueAsync("b", userInVoice: false, CancellationToken.None);
        Assert.Equal(ProactiveDelivery.Rang, second.Delivery);
    }

    [Fact]
    public async Task EnqueueAsync_SpeakCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();

        var coordinator = new ProactiveVoiceCoordinator(
            joinVoice: _ => Task.CompletedTask,
            createInvite: _ => Task.FromResult("https://invite.url"),
            sendRingDm: (_, _) => Task.CompletedTask,
            sendVoiceMessageDm: (_, _) => Task.CompletedTask,
            speak: (_, _, ct) => Task.FromCanceled(ct),
            leaveVoice: _ => Task.CompletedTask,
            ringTtl: DefaultTtl,
            queueCap: DefaultQueueCap,
            logger: NullLogger<ProactiveVoiceCoordinator>.Instance,
            timeProvider: TimeProvider.System);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.EnqueueAsync("hello", userInVoice: true, cts.Token));
    }

    [Fact]
    public async Task EnqueueAsync_RingInitCancelled_PropagatesCancellation_ResetsState()
    {
        using var cts = new CancellationTokenSource();

        var coordinator = new ProactiveVoiceCoordinator(
            joinVoice: ct => ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask,
            createInvite: _ => Task.FromResult("https://invite.url"),
            sendRingDm: (_, _) => Task.CompletedTask,
            sendVoiceMessageDm: (_, _) => Task.CompletedTask,
            speak: (_, _, _) => Task.CompletedTask,
            leaveVoice: _ => Task.CompletedTask,
            ringTtl: DefaultTtl,
            queueCap: DefaultQueueCap,
            logger: NullLogger<ProactiveVoiceCoordinator>.Instance,
            timeProvider: TimeProvider.System);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.EnqueueAsync("hello", userInVoice: false, cts.Token));

        // After cancellation, ring state must be cleared so the next call can retry the ring.
        var retry = await coordinator.EnqueueAsync("after", userInVoice: false, CancellationToken.None);
        Assert.Equal(ProactiveDelivery.Rang, retry.Delivery);
    }

    private static ProactiveVoiceCoordinator BuildCoordinator(
        FakeCallbacks callbacks,
        TimeProvider time,
        TimeSpan? ttl = null,
        int? queueCap = null) =>
        new(
            joinVoice: callbacks.JoinVoiceAsync,
            createInvite: callbacks.CreateInviteAsync,
            sendRingDm: callbacks.SendRingDmAsync,
            sendVoiceMessageDm: callbacks.SendVoiceMessageDmAsync,
            speak: callbacks.SpeakAsync,
            leaveVoice: callbacks.LeaveVoiceAsync,
            ringTtl: ttl ?? DefaultTtl,
            queueCap: queueCap ?? DefaultQueueCap,
            logger: NullLogger<ProactiveVoiceCoordinator>.Instance,
            timeProvider: time);

    private sealed class FakeCallbacks
    {
        public List<string> Spoken { get; } = [];
        public List<string?> SpokenHints { get; } = [];
        public List<string> InvitesCreated { get; } = [];
        public List<string> RingDmsSent { get; } = [];
        public List<string> VoiceMessageDmsSent { get; } = [];
        public int JoinCalls { get; private set; }
        public int LeaveCalls { get; private set; }
        public string InviteUrl { get; set; } = "https://discord.gg/fake-invite";

        public Task JoinVoiceAsync(CancellationToken ct)
        {
            this.JoinCalls++;
            return Task.CompletedTask;
        }

        public Task<string> CreateInviteAsync(CancellationToken ct)
        {
            this.InvitesCreated.Add(this.InviteUrl);
            return Task.FromResult(this.InviteUrl);
        }

        public Task SendRingDmAsync(string inviteUrl, CancellationToken ct)
        {
            this.RingDmsSent.Add(inviteUrl);
            return Task.CompletedTask;
        }

        public Task SendVoiceMessageDmAsync(string text, CancellationToken ct)
        {
            this.VoiceMessageDmsSent.Add(text);
            return Task.CompletedTask;
        }

        public Task SpeakAsync(string text, string? languageHint, CancellationToken ct)
        {
            this.Spoken.Add(text);
            this.SpokenHints.Add(languageHint);
            return Task.CompletedTask;
        }

        public Task LeaveVoiceAsync(CancellationToken ct)
        {
            this.LeaveCalls++;
            return Task.CompletedTask;
        }
    }
}
