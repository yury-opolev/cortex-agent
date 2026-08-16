using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Agent;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Reminders;

/// <summary>Outcome of asking to cancel a timer.</summary>
public enum SessionTimerCancelOutcome
{
    /// <summary>Cancelled before it fired.</summary>
    Cancelled = 0,

    /// <summary>No timer with that id (never existed, or fired long enough ago to be pruned).</summary>
    NotFound = 1,

    /// <summary>It already fired and the agent has it — too late to stop.</summary>
    AlreadyFired = 2,
}

/// <summary>Lifecycle of a session timer, as reported to the agent.</summary>
public enum SessionTimerStatus
{
    /// <summary>Waiting to fire. This is the only state in which it can be cancelled.</summary>
    Pending = 0,

    /// <summary>
    /// Already fired and handed to the agent. Retained briefly so the agent can SEE that it went
    /// off — and therefore why cancelling it now fails — rather than finding it silently absent.
    /// </summary>
    Fired = 1,
}

/// <summary>A timer, as reported to the agent.</summary>
/// <param name="Id">Timer id, used to cancel it.</param>
/// <param name="Intent">What the agent asked to happen when it fires.</param>
/// <param name="Description">Optional short label.</param>
/// <param name="Status">Whether it is still pending or has already fired.</param>
/// <param name="FiresAtUtc">When it fires (or fired).</param>
/// <param name="SecondsRemaining">Seconds until it fires, floored at zero. Zero once fired.</param>
/// <param name="SecondsSinceFired">
/// Seconds since it fired, zero while pending. "Fired two seconds ago" and "fired two minutes ago"
/// call for very different behaviour, so the age is reported rather than just the fact.
/// </param>
public sealed record SessionTimerInfo(
    string Id,
    string Intent,
    string? Description,
    SessionTimerStatus Status,
    DateTimeOffset FiresAtUtc,
    int SecondsRemaining,
    int SecondsSinceFired);

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
public sealed partial class SessionTimerService : IDisposable, IAsyncDisposable
{
    /// <summary>Minimum allowed delay (seconds).</summary>
    public const int MinDelaySeconds = 1;

    /// <summary>Maximum allowed delay (seconds).</summary>
    public const int MaxDelaySeconds = 3600;

    /// <summary>Maximum concurrent active timers per conversation.</summary>
    public const int PerConversationCap = 10;

    /// <summary>
    /// How long a fired timer stays visible in <see cref="List"/> before being pruned. It exists so
    /// a failed cancel can say "it already fired" instead of "no such timer", which is the
    /// difference between the agent understanding what happened and guessing.
    /// </summary>
    public static readonly TimeSpan FiredRetention = TimeSpan.FromMinutes(2);

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
        this.PruneFired(this.timeProvider.GetUtcNow());
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

        // Created disarmed and armed only after the entry is visible: an armed timer whose callback
        // beat the TryAdd would find nothing and return silently, leaving a permanent zombie that
        // never fires yet holds a cap slot.
        var timer = this.timeProvider.CreateTimer(
            state => this.OnFire((string)state!),
            id,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

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

        timer.Change(TimeSpan.FromSeconds(delaySeconds), Timeout.InfiniteTimeSpan);

        // Re-check under the lock: the disposed check above happened before the entry existed, so a
        // shutdown running concurrently would have swept the dictionary without seeing it, leaving
        // an armed timer able to fire after DisposeAsync promised nothing more would.
        bool disposedDuringSchedule;
        lock (this.countsLock)
        {
            disposedDuringSchedule = this.disposed;
        }

        if (disposedDuringSchedule)
        {
            this.entries.TryRemove(id, out _);
            timer.Dispose();
            this.DecrementCount(conversationId);
            throw new ObjectDisposedException(nameof(SessionTimerService));
        }

        this.LogTimerScheduled(id, conversationId, delaySeconds, intent);
        return id;
    }

    /// <summary>Live timers counted against <see cref="PerConversationCap"/>. Excludes fired ones.</summary>
    internal int ActiveCount(string conversationId) =>
        this.activeCountsByConversation.GetValueOrDefault(conversationId);

    /// <summary>
    /// Timers for <paramref name="conversationId"/> — pending first, then recently fired, each
    /// group soonest-first.
    /// <para>
    /// Without this the ids handed back by <c>create</c> were the only record a timer existed, so a
    /// context compaction left the agent unable to see — or cancel — its own timers.
    /// </para>
    /// </summary>
    public IReadOnlyList<SessionTimerInfo> List(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var now = this.timeProvider.GetUtcNow();
        this.PruneFired(now);

        var listed = new List<(TimerEntry Entry, SessionTimerInfo Info)>();
        foreach (var pair in this.entries)
        {
            var entry = pair.Value;
            if (!string.Equals(entry.ConversationId, conversationId, StringComparison.Ordinal))
            {
                continue;
            }

            // One snapshot per entry: reading the state repeatedly could report Pending alongside a
            // fired timer's zeroed countdown.
            var (state, firedAt) = entry.Lifecycle.Snapshot();
            if (state == SessionTimerState.Cancelled)
            {
                continue;
            }

            var fired = state == SessionTimerState.Fired;
            listed.Add((entry, new SessionTimerInfo(
                entry.Id,
                entry.Intent,
                entry.Description,
                fired ? SessionTimerStatus.Fired : SessionTimerStatus.Pending,
                entry.FiresAtUtc,
                fired ? 0 : (int)Math.Max(0, Math.Ceiling((entry.FiresAtUtc - now).TotalSeconds)),
                fired && firedAt is { } at ? (int)Math.Max(0, Math.Floor((now - at).TotalSeconds)) : 0)));
        }

        return
        [
            .. listed
                .OrderBy(x => x.Info.Status)
                .ThenBy(x => x.Entry.FiresAtUtc)
                .Select(x => x.Info),
        ];
    }

    /// <summary>
    /// Cancels a PENDING timer belonging to <paramref name="conversationId"/>. A timer that has
    /// already fired cannot be cancelled — the agent has it and may already be acting on it — so
    /// this reports failure rather than pretending.
    /// </summary>
    public SessionTimerCancelOutcome Cancel(string conversationId, string timerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        if (string.IsNullOrWhiteSpace(timerId))
        {
            return SessionTimerCancelOutcome.NotFound;
        }

        this.PruneFired(this.timeProvider.GetUtcNow());

        // Scoped to the caller's conversation: the service is a process-wide singleton, so an id
        // alone must not reach into a conversation the agent cannot even list.
        if (!this.entries.TryGetValue(timerId, out var entry)
            || !string.Equals(entry.ConversationId, conversationId, StringComparison.Ordinal))
        {
            return SessionTimerCancelOutcome.NotFound;
        }

        if (!entry.Lifecycle.TryClaimCancel())
        {
            // Terminal either way, so this read cannot go stale. A concurrent cancel that won is
            // NOT "already fired" — claiming so would assert an intent was delivered when none was.
            return entry.Lifecycle.Snapshot().State == SessionTimerState.Fired
                ? SessionTimerCancelOutcome.AlreadyFired
                : SessionTimerCancelOutcome.NotFound;
        }

        this.entries.TryRemove(timerId, out _);
        entry.Dispose();
        this.DecrementCount(entry.ConversationId);
        this.LogTimerCancelled(timerId, entry.ConversationId);
        return SessionTimerCancelOutcome.Cancelled;
    }

    private void PruneFired(DateTimeOffset now)
    {
        // Runs from every public entry point, so retention is a property of the timer rather than
        // of whether the agent happened to call list — fired entries cannot pile up in a
        // conversation that never lists.
        foreach (var pair in this.entries)
        {
            var (state, firedAt) = pair.Value.Lifecycle.Snapshot();
            if (state == SessionTimerState.Fired
                && firedAt is { } at
                && now - at > FiredRetention
                && this.entries.TryRemove(pair.Key, out var stale))
            {
                stale.Dispose();
            }
        }
    }

    /// <summary>
    /// Stops every timer, waiting for any callback already running so nothing can be enqueued after
    /// this returns. Prefer this over <see cref="Dispose"/>, which cannot offer that guarantee.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!this.TryBeginDispose())
        {
            return;
        }

        foreach (var pair in this.entries)
        {
            try
            {
                // Unlike the synchronous Dispose, this waits for an in-flight callback to finish.
                await pair.Value.Timer.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Dispose must never throw
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogTimerDisposeFailed(ex, pair.Key);
            }
        }

        this.ClearState();
    }

    /// <summary>
    /// Synchronous shutdown, for containers that dispose synchronously — a singleton registered as
    /// <see cref="IAsyncDisposable"/> only would make <c>ServiceProvider.Dispose()</c> throw. This
    /// does NOT wait for a callback that is already running; use <see cref="DisposeAsync"/> when
    /// that matters.
    /// </summary>
    public void Dispose()
    {
        if (!this.TryBeginDispose())
        {
            return;
        }

        foreach (var pair in this.entries)
        {
            try
            {
                pair.Value.Timer.Dispose();
            }
#pragma warning disable CA1031 // Dispose must never throw
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogTimerDisposeFailed(ex, pair.Key);
            }
        }

        this.ClearState();
    }

    private bool TryBeginDispose()
    {
        lock (this.countsLock)
        {
            if (this.disposed)
            {
                return false;
            }

            this.disposed = true;
        }

        // Take every outstanding claim before disposing anything: a callback already past its
        // dictionary lookup then loses its claim and stays silent, so neither disposal path can
        // let a timer enqueue on the way out.
        foreach (var pair in this.entries)
        {
            pair.Value.Lifecycle.TryClaimCancel();
        }

        return true;
    }

    private void ClearState()
    {
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
        if (!this.entries.TryGetValue(timerId, out var entry))
        {
            return;
        }

        // Claim the transition before doing anything observable. Losing means Cancel got there
        // first, so this callback must stay completely silent — no enqueue, no slot release —
        // otherwise the agent is told "cancelled" and then handed the intent anyway.
        if (!entry.Lifecycle.TryClaimFire(this.timeProvider.GetUtcNow()))
        {
            return;
        }

        // The slot is freed at the instant of firing: retention keeps the entry visible so the
        // agent can see why a later cancel fails, but visibility is not a capacity reservation.
        this.DecrementCount(entry.ConversationId);

        try
        {
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

        public required ITimer Timer { get; init; }

        /// <summary>The one-shot claim arbitrating between firing and cancelling.</summary>
        public SessionTimerLifecycle Lifecycle { get; } = new();

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Timer {TimerId} dispose failed")]
    private partial void LogTimerDisposeFailed(Exception exception, string timerId);
}

