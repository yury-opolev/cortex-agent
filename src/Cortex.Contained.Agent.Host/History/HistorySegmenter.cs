using System.Globalization;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>One deterministic segment of a channel's history (§9.1, §9.2).</summary>
public sealed record HistorySegment(
    string ChannelId,
    string StartTs,
    string EndTs,
    string LocalDate,
    long FirstMsgId,
    long LastMsgId,
    IReadOnlyList<long> MsgIds,
    int Words,
    bool HasInternal,
    bool HasRecallEcho,
    string Fingerprint,
    string Body)
{
    /// <summary>Number of member messages.</summary>
    public int MsgCount => this.MsgIds.Count;
}

/// <summary>Segmentation parameters derived from <see cref="HistoryOptions"/>.</summary>
public sealed record SegmenterSettings(TimeZoneInfo TimeZone, TimeSpan IdleGap, int MaxSegmentWords)
{
    /// <summary>Builds settings from options.</summary>
    public static SegmenterSettings From(HistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SegmenterSettings(options.ResolveTimeZone(), TimeSpan.FromMinutes(options.IdleGapMinutes), options.MaxSegmentWords);
    }
}

/// <summary>
/// Pure, deterministic whole-channel segmenter (§9.2). Input order does not matter: rows are
/// sorted by <c>(Timestamp, Id)</c>. A new segment starts on an idle gap ≥ the configured gap,
/// a change of local calendar date, or before the word count would pass the cap.
/// </summary>
public static class HistorySegmenter
{
    /// <summary>Segmenter version; bump on any behaviour change.</summary>
    public const int Version = 1;

    /// <summary>True for rows the index covers: categories 0–3, excluding tool rows.</summary>
    public static bool IsIndexed(MessageRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Category is MessageCategory.Normal or MessageCategory.Internal or MessageCategory.System or MessageCategory.Proactive
            && !string.Equals(row.Role, "tool", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Segments every indexed row of <paramref name="channelId"/>.</summary>
    public static IReadOnlyList<HistorySegment> Segment(string channelId, IEnumerable<MessageRecord> rows, SegmenterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(settings);

        var ordered = rows
            .Where(r => IsIndexed(r) && string.Equals(r.ChannelId, channelId, StringComparison.Ordinal))
            .OrderBy(r => r.Timestamp.UtcTicks)
            .ThenBy(r => r.Id)
            .ToList();

        var segments = new List<HistorySegment>();
        var current = new List<(MessageRecord Row, string Body, int Words)>();
        DateOnly currentDate = default;
        var currentWords = 0;

        foreach (var row in ordered)
        {
            var body = HistoryRowText.IndexedBody(row);
            var words = HistoryTokenizer.CountTokens(body);
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.Timestamp, settings.TimeZone).DateTime);

            if (current.Count > 0)
            {
                var gap = row.Timestamp - current[^1].Row.Timestamp;
                if (gap >= settings.IdleGap || localDate != currentDate || currentWords + words > settings.MaxSegmentWords)
                {
                    segments.Add(Build(channelId, current, currentDate));
                    current.Clear();
                    currentWords = 0;
                }
            }

            if (current.Count == 0)
            {
                currentDate = localDate;
            }

            current.Add((row, body, words));
            currentWords += words;
        }

        if (current.Count > 0)
        {
            segments.Add(Build(channelId, current, currentDate));
        }

        return segments;
    }

    private static HistorySegment Build(string channelId, List<(MessageRecord Row, string Body, int Words)> members, DateOnly localDate)
    {
        var rows = members.Select(m => m.Row).ToList();
        return new HistorySegment(
            channelId,
            SqliteDateTimeText.Format(rows[0].Timestamp),
            SqliteDateTimeText.Format(rows[^1].Timestamp),
            localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            rows[0].Id,
            rows[^1].Id,
            rows.Select(r => r.Id).ToList(),
            members.Sum(m => m.Words),
            rows.Any(r => r.Category == MessageCategory.Internal),
            rows.Any(r => HistoryLabels.For(r) == HistoryLineLabel.RecallEcho),
            HistoryFingerprint.Compute(rows),
            string.Join('\n', members.Select(m => m.Body)));
    }
}
