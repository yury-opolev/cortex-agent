using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>A parsed <c>history_search</c> call.</summary>
public sealed record HistorySearchRequest
{
    /// <summary>The agent's query (§9.3).</summary>
    public required string Query { get; init; }

    /// <summary>Inclusive lower bound on matching messages' timestamps.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exclusive upper bound on matching messages' timestamps.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Restrict to one channel.</summary>
    public string? Channel { get; init; }

    /// <summary>Relevance (default) or exhaustive time order.</summary>
    public HistoryOrder Order { get; init; } = HistoryOrder.Relevance;

    /// <summary>Include Internal rows (D9).</summary>
    public bool IncludeInternal { get; init; }

    /// <summary>Include recall echoes without requiring that nothing else matches.</summary>
    public bool IncludeEchoes { get; init; }

    /// <summary>Hits per page (1–30).</summary>
    public int Limit { get; init; } = 8;

    /// <summary>Opaque cursor from a previous page, or null.</summary>
    public string? Cursor { get; init; }
}

/// <summary>Where the next page continues. Relevance uses offsets; time order uses a message key.</summary>
/// <param name="IndexOffset">Index candidates already consumed (relevance).</param>
/// <param name="TailSkip">Unindexed-tail hits already returned (relevance).</param>
/// <param name="AfterTs">Canonical timestamp of the last returned message (time order).</param>
/// <param name="AfterId">Id of the last returned message (time order).</param>
public sealed record HistoryCursorPosition(int IndexOffset, int TailSkip, string? AfterTs, long AfterId)
{
    /// <summary>The start of a result stream.</summary>
    public static HistoryCursorPosition Start { get; } = new(0, 0, null, 0);
}

/// <summary>Whether a hit came from the index or from the not-yet-indexed journal tail.</summary>
public enum HistoryHitSource
{
    /// <summary>Found through the FTS index and verified against the source.</summary>
    Indexed,

    /// <summary>Matched directly in rows the index has not applied yet.</summary>
    Unindexed,
}

/// <summary>One verified source row shown to the agent.</summary>
public sealed record HistoryLine(long MessageId, string ChannelId, DateTimeOffset Timestamp, HistoryLineLabel Label, string Text)
{
    /// <summary>Builds a line from a source row.</summary>
    public static HistoryLine From(MessageRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new HistoryLine(row.Id, row.ChannelId, row.Timestamp, HistoryLabels.For(row), HistoryRowText.DisplayContent(row));
    }
}

/// <summary>One search hit: matching lines of one segment (relevance) or one message (time order).</summary>
/// <param name="MemberIds">Every message id of the located segment (locator membership, §14.2).</param>
public sealed record HistoryHit(
    string ChannelId,
    IReadOnlyList<HistoryLine> Lines,
    long FirstMsgId,
    long LastMsgId,
    DateTimeOffset From,
    DateTimeOffset To,
    HistoryHitSource Source,
    double Score,
    bool EchoOnly,
    HistoryCursorPosition Resume,
    IReadOnlyList<long> MemberIds);

/// <summary>Coverage block reported with every recall response (§10).</summary>
public sealed record HistoryCoverage(
    IReadOnlyList<ChannelCoverage> Channels,
    long? Generation,
    bool BuildComplete,
    bool Halted,
    long Lag,
    int UnindexedTail,
    bool UnindexedTailTruncated)
{
    /// <summary>True when the index was withheld because it has not applied the latest purge yet.</summary>
    public bool PurgeFenced { get; init; }

    /// <summary>True when "no hits" may be stated without qualification beyond the listed ranges.</summary>
    public bool IsComplete => this.BuildComplete && !this.Halted && !this.UnindexedTailTruncated && !this.PurgeFenced;
}

/// <summary>Result of <see cref="HistoryRecallService.SearchAsync"/>.</summary>
public sealed record HistorySearchResult
{
    /// <summary>Verified hits of this page.</summary>
    public IReadOnlyList<HistoryHit> Hits { get; init; } = [];

    /// <summary>True when more hits may exist after this page.</summary>
    public bool HasMore { get; init; }

    /// <summary>Position after the whole page.</summary>
    public HistoryCursorPosition Next { get; init; } = HistoryCursorPosition.Start;

    /// <summary>Coverage of this response.</summary>
    public required HistoryCoverage Coverage { get; init; }

    /// <summary>Cursor binding of this query on the current index state.</summary>
    public required string Binding { get; init; }

    /// <summary>Member rows referenced by the index that no longer exist.</summary>
    public int Unavailable { get; init; }

    /// <summary>Terms dropped by the document-frequency cap.</summary>
    public IReadOnlyList<string> DroppedTerms { get; init; } = [];

    /// <summary>Exact number of matching messages, only for complete time-order result sets.</summary>
    public int? TotalMatches { get; init; }

    /// <summary>Set instead of results when the request cannot run.</summary>
    public string? Error { get; init; }
}

/// <summary>A parsed <c>history_read</c> call in recall mode.</summary>
public sealed record HistoryReadRequest
{
    /// <summary>Channel to read; optional with <see cref="AroundMessageId"/>.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Inclusive lower bound.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exclusive upper bound.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Maximum rows for a range read.</summary>
    public int Limit { get; init; } = 200;

    /// <summary>Read ± <see cref="Window"/> rows around this <c>Messages.Id</c>.</summary>
    public long? AroundMessageId { get; init; }

    /// <summary>Rows before and after <see cref="AroundMessageId"/>.</summary>
    public int Window { get; init; } = 10;

    /// <summary>Include Internal rows.</summary>
    public bool IncludeInternal { get; init; }
}

/// <summary>Result of <see cref="HistoryRecallService.ReadAsync"/>.</summary>
public sealed record HistoryReadResult
{
    /// <summary>Lines in <c>(Timestamp, Id)</c> order.</summary>
    public IReadOnlyList<HistoryLine> Lines { get; init; } = [];

    /// <summary>True when rows outside the returned slice exist.</summary>
    public bool HasMore { get; init; }

    /// <summary>The highlighted message, if any.</summary>
    public long? AnchorId { get; init; }

    /// <summary>Set instead of lines when the request cannot run.</summary>
    public string? Error { get; init; }

    /// <summary>What the index covered when this read ran; null only for errors.</summary>
    public HistoryCoverage? Coverage { get; init; }
}
