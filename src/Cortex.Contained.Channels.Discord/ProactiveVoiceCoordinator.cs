using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Coordinates proactive voice-channel message delivery based on whether the
/// user is currently in the target voice channel. When the user is reachable
/// in voice, messages bypass this coordinator via the handler's existing speak
/// path. When the user is not present, the coordinator joins the voice channel,
/// creates a short-lived invite, DMs the user the invite URL (a "ring"), and
/// queues messages until the user joins or the TTL fires.
/// On join within TTL → queued messages are spoken. On TTL expiry → queued
/// messages are sent as Discord voice-message attachments in the DM and the
/// bot leaves the voice channel.
/// </summary>
internal sealed partial class ProactiveVoiceCoordinator
{
    private readonly Func<CancellationToken, Task> joinVoice;
    private readonly Func<CancellationToken, Task<string>> createInvite;
    private readonly Func<string, CancellationToken, Task> sendRingDm;
    private readonly Func<string, CancellationToken, Task> sendVoiceMessageDm;
    private readonly Func<string, string?, CancellationToken, Task> speak;
    private readonly Func<CancellationToken, Task> leaveVoice;
    private readonly TimeSpan ringTtl;
    private readonly int queueCap;
    private readonly ILogger logger;
    private readonly TimeProvider timeProvider;

    public ProactiveVoiceCoordinator(
        Func<CancellationToken, Task> joinVoice,
        Func<CancellationToken, Task<string>> createInvite,
        Func<string, CancellationToken, Task> sendRingDm,
        Func<string, CancellationToken, Task> sendVoiceMessageDm,
        Func<string, string?, CancellationToken, Task> speak,
        Func<CancellationToken, Task> leaveVoice,
        TimeSpan ringTtl,
        int queueCap,
        ILogger logger,
        TimeProvider timeProvider)
    {
        this.joinVoice = joinVoice;
        this.createInvite = createInvite;
        this.sendRingDm = sendRingDm;
        this.sendVoiceMessageDm = sendVoiceMessageDm;
        this.speak = speak;
        this.leaveVoice = leaveVoice;
        this.ringTtl = ringTtl;
        this.queueCap = queueCap;
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    private readonly object stateLock = new();
    private readonly List<QueuedMessage> queue = [];
    private bool ringActive;

    /// <summary>One queued proactive message: the text to deliver and an
    /// optional language hint for TTS synthesis (e.g. <c>"en"</c> for
    /// enrollment-wizard prompts that must bypass the channel's sticky
    /// current language).</summary>
    private readonly record struct QueuedMessage(string Text, string? LanguageHint);
    private ITimer? ringTimer;
    private Task pendingFallbackTask = Task.CompletedTask;

    /// <summary>
    /// Enqueues a proactive message for delivery. When <paramref name="userInVoice"/>
    /// is <see langword="true"/> the message is spoken immediately. When the user is
    /// not in the voice channel, a "ring" sequence is initiated (join → invite → DM)
    /// and the message is queued until the user joins or the TTL fires.
    /// </summary>
    /// <param name="text">The text to speak or deliver.</param>
    /// <param name="userInVoice">
    /// Whether the linked user is currently a member of the configured voice channel.
    /// Independent of bot connection state — the caller should check the user's presence
    /// directly (e.g. <c>channel.Users.Any(u => u.Id == linkedUserId)</c>) rather than
    /// relying on whether the bot itself is connected.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="languageHint">Optional ISO 639-1 language code forwarded to the
    /// <c>speak</c> delegate (e.g. <c>"en"</c> for enrollment-wizard prompts that
    /// must bypass the channel's sticky current language). When <see langword="null"/>,
    /// the speak delegate uses its default routing.</param>
    public async Task<ProactiveOutcome> EnqueueAsync(string text, bool userInVoice, CancellationToken ct, string? languageHint = null)
    {
        if (userInVoice)
        {
            try
            {
                await this.speak(text, languageHint, ct).ConfigureAwait(false);
                return new ProactiveOutcome(ProactiveDelivery.Spoken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ProactiveOutcome(ProactiveDelivery.Dropped, ex.Message);
            }
        }

        bool shouldStartRing;
        bool droppedOldest = false;
        lock (this.stateLock)
        {
            this.queue.Add(new QueuedMessage(text, languageHint));
            if (this.queue.Count > this.queueCap)
            {
                this.queue.RemoveAt(0);
                droppedOldest = true;
            }

            shouldStartRing = !this.ringActive;
            if (shouldStartRing)
            {
                this.ringActive = true;
            }
        }

        if (droppedOldest)
        {
            this.LogQueueCapExceeded(this.queueCap);
        }

        if (!shouldStartRing)
        {
            return new ProactiveOutcome(ProactiveDelivery.Queued);
        }

        try
        {
            await this.joinVoice(ct).ConfigureAwait(false);
            var inviteUrl = await this.createInvite(ct).ConfigureAwait(false);
            await this.sendRingDm(inviteUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Ring initiation failed — abandon the ring so the next message can retry.
            lock (this.stateLock)
            {
                this.ringActive = false;
                this.queue.Clear();
            }

            if (ex is OperationCanceledException) { throw; }

            return new ProactiveOutcome(ProactiveDelivery.Dropped, ex.Message);
        }

        lock (this.stateLock)
        {
            this.ringTimer?.Dispose();
            this.ringTimer = this.timeProvider.CreateTimer(
                OnRingTimerElapsed,
                state: null,
                dueTime: this.ringTtl,
                period: Timeout.InfiniteTimeSpan);
        }

        return new ProactiveOutcome(ProactiveDelivery.Rang);
    }

    public async Task OnUserJoinedAsync(CancellationToken ct)
    {
        QueuedMessage[] drained;
        lock (this.stateLock)
        {
            if (!this.ringActive)
            {
                return;
            }

            drained = [.. this.queue];
            this.queue.Clear();
            this.ringActive = false;
            this.ringTimer?.Dispose();
            this.ringTimer = null;
        }

        foreach (var item in drained)
        {
            try
            {
                await this.speak(item.Text, item.LanguageHint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A mid-drain failure (e.g. VoiceNotConnectedException because the
                // channel was disposed mid-drain) must not surface as an
                // unobserved-task exception — the only callers of this method
                // are Task.Run-fired voice-state handlers.
                this.LogDrainSpeakFailed(ex.Message);
            }
        }
    }

    /// <summary>Test helper: awaits any in-flight ring-timeout fallback work.</summary>
    internal Task WaitForPendingFallbackAsync() => this.pendingFallbackTask;

    private void OnRingTimerElapsed(object? state)
    {
        this.pendingFallbackTask = Task.Run(() => RingFallbackAsync(CancellationToken.None));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proactive voice queue cap {QueueCap} exceeded; dropped oldest message")]
    private partial void LogQueueCapExceeded(int queueCap);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drained message could not be spoken: {Reason}")]
    private partial void LogDrainSpeakFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drained ring-fallback voice-message DM could not be sent: {Reason}")]
    private partial void LogDrainSendVoiceMessageDmFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ring-fallback leaveVoice failed: {Reason}")]
    private partial void LogLeaveVoiceFailed(string reason);

    private async Task RingFallbackAsync(CancellationToken ct)
    {
        QueuedMessage[] drained;
        lock (this.stateLock)
        {
            if (!this.ringActive)
            {
                return;
            }

            drained = [.. this.queue];
            this.queue.Clear();
            this.ringActive = false;
            this.ringTimer?.Dispose();
            this.ringTimer = null;
        }

        foreach (var item in drained)
        {
            try
            {
                await this.sendVoiceMessageDm(item.Text, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // RingFallbackAsync runs on a Task.Run from the ring TTL timer —
                // an exception here would surface only via UnobservedTaskException.
                // Log per-message and continue draining so a single failed DM does
                // not skip the leaveVoice cleanup or remaining queued messages.
                this.LogDrainSendVoiceMessageDmFailed(ex.Message);
            }
        }

        try
        {
            await this.leaveVoice(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.LogLeaveVoiceFailed(ex.Message);
        }
    }
}
