using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Agent;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Reminders;

/// <summary>A pending timer, as reported to the agent.</summary>
/// <param name="Id">Timer id, used to cancel it.</param>
/// <param name="Intent">What the agent asked to happen when it fires.</param>
/// <param name="Description">Optional short label.</param>
/// <param name="FiresAtUtc">When it will fire.</param>
/// <param name="SecondsRemaining">Seconds until it fires, floored at zero.</param>
public sealed record PendingSessionTimer(
    string Id,
    string Intent,
    string? Description,
    DateTimeOffset FiresAtUtc,
    int SecondsRemaining);

/// <summary>
/// In-process, in-memory one-shot timers bound to a live conversation.
/// <para>
/// A timer carries an INTENT, not a line of text. When it fires the intent is enqueued as an
/// <see cref="AgentMessageSource.SessionTimer"/> message on the conversation that created it, so
/// the model evaluates it against the live situation and decides what to say or do. The previous
/// design froze the exact words at schedule time and spoke them verbatim, which could not adapt to
/// anything that had happened since — and could only ever speak, never act.
/// </para>
/// <para>
/// Timers are deliberately in-memory: they are session-scoped pacing aids and do not survive an
/// agent restart. Calendar-style work belongs in <c>schedule_task</c>, which is persisted.
/// </para>
/// </summary>
public sealed partial class SessionTimerService : IDisposable
{
    /// <summary>Minimum allowed delay (seconds).</summary>
    public const int MinDelaySeconds = 1;

    /// <summary>Maximum allowed delay (seconds).</summary>
    public const int MaxDelaySeconds = 3600;

    /// <summary>Maximum concurrent active timers per conversation.</summary>
    public const int PerConversationCap = 10;

    private readonly AgentMessageChannel queue;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SessionTimerService> logger;

    private readonly ConcurrentDictionary<string, TimerEntry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> activeCountsByConversation = new(StringComparer.Ordinal);
    private readonly Lock countsLock = new();

    private bool disposed;

    public SessionTimerService(
        AgentMessageChannel queue,
        ILogger<SessionTimerService> logger,
        TimeProvider? timeProvider = null)
    {
        this.queue = queue;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Schedules a one-shot intent bound to <paramref name="conversationId"/>. Returns the timer id.
    /// </summary>
    /// <param name="conversationId">The conversation the timer fires back into.</param>
    /// <param name="channelId">Channel used for the fired message.</param>
    /// <param name="delaySeconds">Delay before firing.</param>
    /// <param name="intent">What should happen when it fires.</param>
    /// <param name="description">Optional short label shown when listing.</param>
    public string Schedule(
        string conversationId,
        string channelId,
        int delaySeconds,
        string intent,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent);

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
                    $"Per-conversation timer cap reached ({PerConversationCap}) for '{conversationId}'.");
            }

            this.activeCountsByConversation[conversationId] = current + 1;
        }

        var id = NewId();
        var firesAt = this.timeProvider.GetUtcNow().AddSeconds(delaySeconds);
        var timer = new Timer(state => this.OnFire((string)state!), id, delaySeconds * 1000, Timeout.Infinite);
        var entry = new TimerEntry
        {
            Id = id,
            ConversationId = conversationId,
            ChannelId = channelId,
            DelaySeconds = delaySeconds,
            Intent = intent,
            Description = description,
            FiresAtUtc = firesAt,
            Timer = timer,
        };

        if (!this.entries.TryAdd(id, entry))
        {
            // Should not happen — id collisions are astronomically unlikely.
            timer.Dispose();
            this.DecrementCount(conversationId);
            throw new InvalidOperationException("Timer id collision.");
        }

        this.LogTimerScheduled(id, conversationId, delaySeconds, intent);
        return id;
    }

    /// <summary>
    /// Pending timers for <paramref name="conversationId"/>, soonest first.
    /// <para>
    /// Without this the ids handed back by <c>create</c> were the only record a timer existed, so a
    /// context compaction left the agent unable to see — or cancel — its own timers.
    /// </para>
    /// </summary>
    public IReadOnlyList<PendingSessionTimer> List(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var now = this.timeProvider.GetUtcNow();
        return
        [
            .. this.entries.Values
                .Where(e => string.Equals(e.ConversationId, conversationId, StringComparison.Ordinal))
                .OrderBy(e => e.FiresAtUtc)
                .Select(e => new PendingSessionTimer(
                    e.Id,
                    e.Intent,
                    e.Description,
                    e.FiresAtUtc,
                    (int)Math.Max(0, Math.Ceiling((e.FiresAtUtc - now).TotalSeconds)))),
        ];
    }

    /// <summary>Cancels a pending timer. Returns true if cancelled, false if unknown.</summary>
    public bool Cancel(string timerId)
    {
        if (string.IsNullOrWhiteSpace(timerId) || !this.entries.TryRemove(timerId, out var entry))
        {
            return false;
        }

        entry.Dispose();
        this.DecrementCount(entry.ConversationId);
        this.LogTimerCancelled(timerId, entry.ConversationId);
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

        // Timer.Dispose(WaitHandle) signals once the timer is fully disposed AND any pending
        // callback has finished, so no OnFire can enqueue after Dispose returns.
        using var doneHandle = new ManualResetEvent(false);
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
                this.LogTimerFireFailed(entry.Id, ex.Message);
            }
        }

        this.entries.Clear();
        this.activeCountsByConversation.Clear();
    }

    /// <summary>
    /// Renders the intent for the model. Names the timer so it can be correlated with a listing,
    /// and states plainly that this is an intent to act on now — not text to repeat.
    /// </summary>
    internal static string BuildIntentText(TimerEntryView entry)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[Timer {entry.Id} fired after {entry.DelaySeconds}s]");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Description: {entry.Description}");
        }

        sb.AppendLine();
        sb.AppendLine(
            "Act on this intent now, using the current state of the conversation. It is an "
            + "instruction to you, not a line to repeat verbatim — say or do whatever the intent "
            + "actually calls for right now.");
        sb.AppendLine();
        sb.AppendLine("Intent:");
        sb.Append(entry.Intent);

        return sb.ToString();
    }

    private void OnFire(string timerId)
    {
        if (!this.entries.TryRemove(timerId, out var entry))
        {
            return;
        }

        try
        {
            entry.Timer.Dispose();
            this.DecrementCount(entry.ConversationId);

            var message = new AgentMessage
            {
                // The conversation that CREATED the timer, so the intent is evaluated against the
                // live session rather than in isolation.
                ConversationId = entry.ConversationId,
                ChannelId = entry.ChannelId,
                Text = BuildIntentText(entry.ToView()),
                Source = AgentMessageSource.SessionTimer,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Timestamp = this.timeProvider.GetUtcNow(),
            };

            if (!this.queue.TryEnqueue(message))
            {
                this.LogTimerEnqueueFailed(entry.Id, "agent message queue is full");
                return;
            }

            this.LogTimerFired(entry.Id, entry.ConversationId, entry.DelaySeconds);
        }
#pragma warning disable CA1031 // Timer callback must never throw
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogTimerFireFailed(entry.Id, ex.Message);
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

    /// <summary>The fields <see cref="BuildIntentText"/> needs, so it stays independently testable.</summary>
    internal readonly record struct TimerEntryView(string Id, int DelaySeconds, string Intent, string? Description);

    private sealed class TimerEntry : IDisposable
    {
        public required string Id { get; init; }

        public required string ConversationId { get; init; }

        public required string ChannelId { get; init; }

        public required int DelaySeconds { get; init; }

        public required string Intent { get; init; }

        public string? Description { get; init; }

        public required DateTimeOffset FiresAtUtc { get; init; }

        public required Timer Timer { get; init; }

        public TimerEntryView ToView() => new(this.Id, this.DelaySeconds, this.Intent, this.Description);

        public void Dispose() => this.Timer.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Timer {TimerId} scheduled on {ConversationId} for {DelaySeconds}s, intent='{Intent}'")]
    private partial void LogTimerScheduled(string timerId, string conversationId, int delaySeconds, string intent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Timer {TimerId} fired on {ConversationId} after {DelaySeconds}s")]
    private partial void LogTimerFired(string timerId, string conversationId, int delaySeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Timer {TimerId} cancelled on {ConversationId}")]
    private partial void LogTimerCancelled(string timerId, string conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Timer {TimerId} enqueue failed: {ErrorMessage}")]
    private partial void LogTimerEnqueueFailed(string timerId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Error, Message = "Timer {TimerId} fire failed: {ErrorMessage}")]
    private partial void LogTimerFireFailed(string timerId, string errorMessage);
}
