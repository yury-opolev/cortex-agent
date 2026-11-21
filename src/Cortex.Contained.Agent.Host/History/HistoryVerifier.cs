using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>Row-level visibility of one recall call.</summary>
/// <param name="Channel">Only this channel, or null.</param>
/// <param name="Since">Inclusive lower bound on message timestamps.</param>
/// <param name="Until">Exclusive upper bound on message timestamps.</param>
/// <param name="IncludeInternal">Show Internal rows (D9).</param>
/// <param name="IncludeEchoes">Show recall-echo rows in time order.</param>
public sealed record RecallFilter(string? Channel, DateTimeOffset? Since, DateTimeOffset? Until, bool IncludeInternal, bool IncludeEchoes)
{
    /// <summary>True when <paramref name="row"/> may be shown and re-matched.</summary>
    public bool Admits(MessageRecord row, bool forTimeOrder)
    {
        ArgumentNullException.ThrowIfNull(row);
        return HistorySegmenter.IsIndexed(row)
            && (this.Channel is null || string.Equals(row.ChannelId, this.Channel, StringComparison.Ordinal))
            && (this.IncludeInternal || row.Category != MessageCategory.Internal)
            && (this.Since is null || row.Timestamp >= this.Since)
            && (this.Until is null || row.Timestamp < this.Until)
            && (!forTimeOrder || this.IncludeEchoes || HistoryLabels.For(row) != HistoryLineLabel.RecallEcho);
    }
}

/// <summary>A candidate segment after source verification.</summary>
/// <param name="Segment">The index row.</param>
/// <param name="Matched">Visible member rows that match the query now, in <c>(Timestamp, Id)</c> order.</param>
public sealed record VerifiedSegment(IndexedSegment Segment, IReadOnlyList<MessageRecord> Matched);

/// <summary>
/// Verification of index hits against the source (§10): re-read member rows on the caller's
/// snapshot, compare the full-content fingerprint, and re-match the full query with
/// <see cref="FtsRematcher"/>. Segments whose fingerprint changed are queued for recomputation.
/// </summary>
public static class HistoryVerifier
{
    /// <summary>Verifies <paramref name="segments"/>; returns them in input order plus the missing-row count.</summary>
    public static async Task<(IReadOnlyList<VerifiedSegment> Segments, int Unavailable)> VerifyAsync(
        HistorySourceSnapshot snapshot,
        IReadOnlyList<IndexedSegment> segments,
        string matchExpression,
        RecallFilter filter,
        bool forTimeOrder,
        Action<string> requestRecompute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(requestRecompute);

        var ids = segments.SelectMany(s => s.MsgIds).Distinct().ToList();
        var rows = (await snapshot.ReadByIdsAsync(ids, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.Id);
        var visible = rows.Values.Where(r => filter.Admits(r, forTimeOrder)).ToList();
        var matched = visible.Count == 0 ? new HashSet<long>() : FtsRematcher.Match(matchExpression, visible);

        var unavailable = 0;
        var verified = new List<VerifiedSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var members = segment.MsgIds.Where(rows.ContainsKey).Select(id => rows[id])
                .OrderBy(r => r.Timestamp.UtcTicks).ThenBy(r => r.Id).ToList();
            unavailable += segment.MsgIds.Count - members.Count;
            if (members.Count != segment.MsgIds.Count
                || !string.Equals(HistoryFingerprint.Compute(members), segment.Fingerprint, StringComparison.Ordinal))
            {
                requestRecompute(segment.ChannelId);
            }

            verified.Add(new VerifiedSegment(segment, members.Where(r => matched.Contains(r.Id) && filter.Admits(r, forTimeOrder)).ToList()));
        }

        return (verified, unavailable);
    }
}
