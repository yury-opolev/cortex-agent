using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Reminders;

/// <summary>
/// In-process, in-memory store of one-shot timers bound to live
/// <c>discord-voice-*</c> conversations. When a timer fires, the pre-composed
/// <see cref="ReminderEntry.Text"/> is delivered straight through the
/// proactive-speech path via <see cref="IVoiceCueDeliverer"/> — no LLM
/// round-trip, no synthetic message injection.
/// </summary>
public sealed partial class SessionReminderService : IDisposable
{
    /// <summary>Conversation ids must start with this prefix to accept reminders.</summary>
    internal const string VoiceConversationPrefix = "discord-voice-";

    /// <summary>Minimum allowed delay (seconds).</summary>
    public const int MinDelaySeconds = 1;

    /// <summary>Maximum allowed delay (seconds).</summary>
    public const int MaxDelaySeconds = 3600;

    /// <summary>Maximum concurrent active reminders per conversation.</summary>
    public const int PerConversationCap = 10;

    private readonly IVoiceCueDeliverer deliverer;
    private readonly ILogger<SessionReminderService> logger;

    private readonly ConcurrentDictionary<string, ReminderEntry> entries = new();
    private readonly ConcurrentDictionary<string, int> activeCountsByConversation = new(StringComparer.Ordinal);
    private readonly object countsLock = new();

    private bool disposed;

    public SessionReminderService(
        IVoiceCueDeliverer deliverer,
        ILogger<SessionReminderService> logger)
    {
        this.deliverer = deliverer;
        this.logger = logger;
    }

    /// <summary>
    /// Schedule a one-shot reminder bound to the given conversation. Returns
    /// the reminder id. Throws if validation fails.
    /// </summary>
    public string Schedule(string conversationId, string channelId, int delaySeconds, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (!conversationId.StartsWith(VoiceConversationPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Reminders are only available for '{VoiceConversationPrefix}*' conversations (got '{conversationId}').",
                nameof(conversationId));
        }

        if (delaySeconds < MinDelaySeconds || delaySeconds > MaxDelaySeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delaySeconds),
                delaySeconds,
                $"delaySeconds must be between {MinDelaySeconds} and {MaxDelaySeconds}.");
        }

        // Reserve a slot atomically against the per-conversation cap.
        lock (this.countsLock)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);

            var current = this.activeCountsByConversation.GetValueOrDefault(conversationId);
            if (current >= PerConversationCap)
            {
                throw new InvalidOperationException(
                    $"Per-conversation reminder cap reached ({PerConversationCap}) for '{conversationId}'.");
            }

            this.activeCountsByConversation[conversationId] = current + 1;
        }

        var id = NewId();
        var realTimer = new Timer(state => this.OnFire((string)state!), id, delaySeconds * 1000, Timeout.Infinite);
        var entry = new ReminderEntry
        {
            Id = id,
            ConversationId = conversationId,
            ChannelId = channelId,
            DelaySeconds = delaySeconds,
            Text = text,
            Timer = realTimer,
        };

        if (!this.entries.TryAdd(id, entry))
        {
            // Should not happen — id collisions are astronomically unlikely.
            realTimer.Dispose();
            this.DecrementCount(conversationId);
            throw new InvalidOperationException("Reminder id collision.");
        }

        this.LogReminderScheduled(id, conversationId, delaySeconds, text);
        return id;
    }

    /// <summary>Cancel a pending reminder. Returns true if cancelled, false if unknown.</summary>
    public bool Cancel(string reminderId)
    {
        if (!this.entries.TryRemove(reminderId, out var entry))
        {
            return false;
        }

        entry.Dispose();
        this.DecrementCount(entry.ConversationId);
        this.LogReminderCancelled(reminderId, entry.ConversationId);
        return true;
    }

    public void Dispose()
    {
        lock (this.countsLock)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
        }

        // Wait for any in-flight Timer callbacks to drain before clearing state.
        // Timer.Dispose(WaitHandle) signals the handle once the timer is fully
        // disposed AND any pending callback has finished — guarantees no
        // OnFire calls SpeakAsync after Dispose returns.
        using var doneHandle = new System.Threading.ManualResetEvent(false);
        foreach (var entry in this.entries.Values)
        {
            try
            {
                entry.Timer.Dispose(doneHandle);
                doneHandle.WaitOne();
                doneHandle.Reset();
            }
#pragma warning disable CA1031 // Dispose must never throw
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogReminderFireFailed(entry.Id, ex.Message);
            }
        }

        this.entries.Clear();
        this.activeCountsByConversation.Clear();
    }

    private void OnFire(string reminderId)
    {
        if (!this.entries.TryRemove(reminderId, out var entry))
        {
            return;
        }

        try
        {
            entry.Timer.Dispose();
            this.DecrementCount(entry.ConversationId);

            _ = Task.Run(async () =>
            {
                try
                {
                    await this.deliverer.SpeakAsync(
                        entry.ConversationId,
                        entry.ChannelId,
                        entry.Text,
                        CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Timer thread must never throw
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    this.LogReminderEnqueueFailed(entry.Id, ex.Message);
                }
            });

            this.LogReminderFired(entry.Id, entry.ConversationId, entry.DelaySeconds);
        }
#pragma warning disable CA1031 // Timer callback must never throw
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogReminderFireFailed(entry.Id, ex.Message);
        }
    }

    private void DecrementCount(string conversationId)
    {
        lock (this.countsLock)
        {
            if (this.activeCountsByConversation.TryGetValue(conversationId, out var current))
            {
                if (current <= 1)
                {
                    this.activeCountsByConversation.TryRemove(conversationId, out _);
                }
                else
                {
                    this.activeCountsByConversation[conversationId] = current - 1;
                }
            }
        }
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reminder {ReminderId} scheduled on {ConversationId} for {DelaySeconds}s, text='{Text}'")]
    private partial void LogReminderScheduled(string reminderId, string conversationId, int delaySeconds, string text);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reminder {ReminderId} fired on {ConversationId} after {DelaySeconds}s")]
    private partial void LogReminderFired(string reminderId, string conversationId, int delaySeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reminder {ReminderId} cancelled on {ConversationId}")]
    private partial void LogReminderCancelled(string reminderId, string conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reminder {ReminderId} enqueue failed: {ErrorMessage}")]
    private partial void LogReminderEnqueueFailed(string reminderId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reminder {ReminderId} fire failed: {ErrorMessage}")]
    private partial void LogReminderFireFailed(string reminderId, string errorMessage);
}
