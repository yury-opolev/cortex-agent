namespace Cortex.Contained.Agent.Host.History;

/// <summary>Which rows a purge deletes. Null members are unbounded.</summary>
/// <param name="ChannelId">Restrict to one channel, or null for every channel.</param>
/// <param name="OlderThan">Delete rows with <c>Timestamp &lt; OlderThan</c>, or null for no upper bound.</param>
/// <param name="From">Delete rows with <c>Timestamp &gt;= From</c>, or null for no lower bound (a "between dates" purge sets both).</param>
public sealed record HistoryPurgeScope(string? ChannelId, DateTimeOffset? OlderThan, DateTimeOffset? From = null)
{
    /// <summary>Every row.</summary>
    public static HistoryPurgeScope All { get; } = new(null, null);

    /// <summary>True when only one channel is affected.</summary>
    public bool IsChannelScoped => this.ChannelId is not null;

    /// <summary>Null when the scope is usable; otherwise why not (an empty or inverted time range).</summary>
    public string? Problem() =>
        this.From is { } from && this.OlderThan is { } olderThan && from >= olderThan
            ? "The start of the range must be before its end."
            : null;
}

/// <summary>What a purge would delete (shown before the owner confirms).</summary>
/// <param name="Messages">Rows the purge would delete.</param>
/// <param name="DependentMemories">Not-kept curated memories inside the purge's quarantine windows.</param>
/// <param name="RangeFrom">Oldest affected row, or null.</param>
/// <param name="RangeTo">Newest affected row, or null.</param>
public sealed record HistoryPurgePreview(int Messages, int DependentMemories, DateTimeOffset? RangeFrom, DateTimeOffset? RangeTo);

/// <summary>Input to the single purge transaction in <c>MessageStore.PurgeAsync</c>.</summary>
public sealed record MessagePurgeRequest(HistoryPurgeScope Scope)
{
    /// <summary>Purge time; caps quarantine windows.</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Quarantine windows end no later than this instant when set (tenant import: memories created
    /// after the import began are the import's own and must not be hidden). Defaults to <see cref="Now"/>.
    /// </summary>
    public DateTimeOffset? QuarantineCap { get; init; }

    /// <summary>Added after the last purged message of a quarantine window (delayed memory extraction).</summary>
    public TimeSpan QuarantineSlack { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Purged messages further apart than this start a new quarantine window.</summary>
    public TimeSpan QuarantineClusterGap { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Erasure workflow id; also tags the quarantine windows.</summary>
    public string ErasureId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Steps to record in <c>Meta.PendingErasure</c>, or null to record none.</summary>
    public IReadOnlyList<string>? PendingSteps { get; init; }

    /// <summary>Replacement rows inserted in the same transaction (tenant import); bumps <c>RestoreGeneration</c>.</summary>
    public IReadOnlyList<Storage.MessageRecord>? Replacement { get; init; }
}

/// <summary>Result of the purge transaction.</summary>
/// <param name="Deleted">Rows deleted.</param>
/// <param name="Inserted">Replacement rows inserted.</param>
/// <param name="Windows">Quarantine windows recorded (D10).</param>
/// <param name="RangeFrom">Oldest deleted timestamp, or null when nothing was deleted.</param>
/// <param name="RangeTo">Newest deleted timestamp, or null when nothing was deleted.</param>
public sealed record MessagePurgeOutcome(
    int Deleted,
    int Inserted,
    IReadOnlyList<QuarantineWindow> Windows,
    DateTimeOffset? RangeFrom,
    DateTimeOffset? RangeTo)
{
    /// <summary>Events deleted with the rows (tombstones are kept).</summary>
    public int DeletedEvents { get; init; }

    /// <summary>The <c>Meta.PurgeGeneration</c> this purge committed.</summary>
    public long PurgeGeneration { get; init; }
}
