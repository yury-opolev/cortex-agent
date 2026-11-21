using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Executes <c>history_search</c> and recall-mode <c>history_read</c> (§9.4, §10, §12). Every call
/// holds a <see cref="HistoryAccessGate"/> read lease from its first source read until the result
/// is assembled, reads the source on one snapshot, verifies every index hit, and merges matches
/// from the not-yet-indexed journal tail.
/// </summary>
public sealed partial class HistoryRecallService
{
    private const int MaxRelevanceCandidates = 400;
    private const int TimeOrderBatch = 64;

    private readonly IHistorySource source;
    private readonly HistoryProjector projector;
    private readonly HistoryAccessGate gate;
    private readonly HistoryStatus status;
    private readonly IOptionsMonitor<HistoryOptions> options;
    private readonly IHistoryEmbedder? embedder;
    private readonly TimeProvider time;

    public HistoryRecallService(
        IHistorySource source,
        HistoryProjector projector,
        HistoryAccessGate gate,
        HistoryStatus status,
        IOptionsMonitor<HistoryOptions> options,
        IHistoryEmbedder? embedder = null,
        TimeProvider? time = null)
    {
        this.source = source;
        this.projector = projector;
        this.gate = gate;
        this.status = status;
        this.options = options;
        this.embedder = embedder;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Test seam: awaited after the read lease and snapshot are taken (simulates a slow recall).</summary>
    internal Func<Task>? AfterSnapshotHook { get; set; }

    /// <summary>Runs one <c>history_search</c> page.</summary>
    public async Task<HistorySearchResult> SearchAsync(HistorySearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The query is embedded before the read lease, so a slow embedder never delays a purge.
        var (queryVector, denseNote) = await this.EmbedQueryAsync(request, cancellationToken).ConfigureAwait(false);
        await using var lease = await this.gate.AcquireReadAsync(cancellationToken).ConfigureAwait(false);
        await using var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (this.AfterSnapshotHook is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        var meta = await snapshot.ReadMetaAsync(cancellationToken).ConfigureAwait(false);
        using var reader = this.projector.Current?.OpenReader();
        var state = reader?.ReadState();
        var checkpoint = state?.CheckpointJournalSeq ?? 0;

        // Fence (§4.1): an index that has not applied the latest purge is withheld; only the
        // not-yet-indexed tail, matched against the source, is searched.
        var purgeFenced = state is not null && state.AppliedPurgeGeneration < meta.PurgeGeneration;
        var index = purgeFenced ? null : reader;
        var chunks = reader?.ReadChunkCounts();
        if (queryVector is not null && (index is null || chunks is not { Embedded: > 0 }))
        {
            (queryVector, denseNote) = (null, index is null ? "fallback: index withheld" : "fallback: no chunk is embedded yet");
        }
        else if (queryVector is not null && index!.ReadVectorDimensions() is { } storedDimensions && storedDimensions != queryVector.Length)
        {
            // The embedder changed under a live index; the generation is rebuilt on the next apply.
            (queryVector, denseNote) = (null, "fallback: the embedder's vector dimensions changed; the index is being rebuilt");
        }

        var mode = queryVector is null ? HistorySearchMode.Keyword : HistorySearchMode.Hybrid;
        var binding = HistoryCursorCodec.Binding(request with { Mode = mode }, state?.Generation);

        var position = HistoryCursorPosition.Start;
        if (request.Cursor is { } cursor)
        {
            if (!HistoryCursorCodec.TryDecode(cursor, out var cursorBinding, out position)
                || !string.Equals(cursorBinding, binding, StringComparison.Ordinal))
            {
                return new HistorySearchResult
                {
                    Coverage = Empty(state),
                    Binding = binding,
                    Error = "This cursor is stale or invalid: the query, filters or index generation changed since it was issued. Run the search again without a cursor.",
                };
            }
        }

        var parsed = QueryParser.Build(
            QueryParser.Split(request.Query),
            request.Order,
            term => index?.DocumentFrequency(term) ?? 0,
            index?.SegmentCount() ?? 0,
            this.options.CurrentValue.DfCapRatio);
        if (parsed is null)
        {
            return new HistorySearchResult
            {
                Coverage = Empty(state),
                Binding = binding,
                Error = "The query has no searchable words. Use content words, a \"quoted phrase\" or stem*.",
            };
        }

        parsed = QueryParser.ForVariant(parsed, this.options.CurrentValue.KeywordVariant, request.Order);
        var filter = new RecallFilter(request.Channel, request.Since, request.Until, request.IncludeInternal, request.IncludeEchoes);
        var (tailRows, tailCount, tailTruncated) = await this.ReadTailAsync(snapshot, checkpoint, cancellationToken).ConfigureAwait(false);
        var coverage = BuildCoverage(index, state, meta.JournalHead, checkpoint, request.Channel, this.status.Snapshot().Halted, tailCount, tailTruncated) with
        {
            PurgeFenced = purgeFenced,
            Dense = mode == HistorySearchMode.Hybrid ? "on" : denseNote ?? "off",
            Chunks = chunks,
        };

        var result = request.Order == HistoryOrder.Time
            ? await this.SearchTimeOrderAsync(snapshot, index, parsed, request, filter, position, tailRows, cancellationToken).ConfigureAwait(false)
            : mode == HistorySearchMode.Hybrid
                ? await this.SearchHybridAsync(snapshot, index!, parsed, request, filter, position, tailRows, queryVector!, cancellationToken).ConfigureAwait(false)
                : await this.SearchRelevanceAsync(snapshot, index, parsed, request, filter, position, tailRows, cancellationToken).ConfigureAwait(false);

        return result with
        {
            Coverage = coverage,
            Binding = binding,
            Mode = mode,
            DroppedTerms = parsed.DroppedTerms.Select(t => string.Join(' ', t.Tokens)).ToList(),
            TotalMatches = request.Order == HistoryOrder.Time && request.Cursor is null && !result.HasMore && coverage.IsComplete
                ? result.Hits.Count
                : null,
        };
    }

    /// <summary>Runs a recall-mode <c>history_read</c>.</summary>
    public async Task<HistoryReadResult> ReadAsync(HistoryReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var lease = await this.gate.AcquireReadAsync(cancellationToken).ConfigureAwait(false);
        await using var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);

        var meta = await snapshot.ReadMetaAsync(cancellationToken).ConfigureAwait(false);
        using var reader = this.projector.Current?.OpenReader();
        var state = reader?.ReadState();
        var checkpoint = state?.CheckpointJournalSeq ?? 0;
        var (_, tailCount, tailTruncated) = await this.ReadTailAsync(snapshot, checkpoint, cancellationToken).ConfigureAwait(false);
        var halted = this.status.Snapshot().Halted;
        HistoryCoverage CoverageFor(string? channel) =>
            BuildCoverage(reader, state, meta.JournalHead, checkpoint, channel, halted, tailCount, tailTruncated);

        if (request.AroundMessageId is { } anchorId)
        {
            var anchor = await snapshot.ReadByIdAsync(anchorId, cancellationToken).ConfigureAwait(false);
            if (anchor is null || !HistorySegmenter.IsIndexed(anchor))
            {
                return new HistoryReadResult { Error = $"Message {anchorId} was not found. It may have been deleted." };
            }

            if (request.ChannelId is not null && !string.Equals(request.ChannelId, anchor.ChannelId, StringComparison.Ordinal))
            {
                return new HistoryReadResult { Error = $"Message {anchorId} belongs to channel '{anchor.ChannelId}', not '{request.ChannelId}'." };
            }

            if (anchor.Category == Contracts.Hub.MessageCategory.Internal && !request.IncludeInternal)
            {
                return new HistoryReadResult { Error = $"Message {anchorId} is internal (not necessarily delivered). Pass includeInternal=true to read it." };
            }

            var rows = await snapshot.ReadAroundAsync(anchor, request.Window, request.IncludeInternal, cancellationToken).ConfigureAwait(false);
            var anchorIndex = rows.ToList().FindIndex(r => r.Id == anchorId);
            return new HistoryReadResult
            {
                Lines = rows.Select(HistoryLine.From).ToList(),
                HasMore = anchorIndex >= request.Window || rows.Count - anchorIndex - 1 >= request.Window,
                AnchorId = anchorId,
                Coverage = CoverageFor(anchor.ChannelId),
            };
        }

        if (string.IsNullOrWhiteSpace(request.ChannelId))
        {
            return new HistoryReadResult { Error = "Provide channelId, or aroundMessageId from a history_search result." };
        }

        var (range, hasMore) = await snapshot.ReadRangeAsync(
            request.ChannelId, request.Since, request.Until, request.Limit, request.IncludeInternal, cancellationToken).ConfigureAwait(false);
        return new HistoryReadResult { Lines = range.Select(HistoryLine.From).ToList(), HasMore = hasMore, Coverage = CoverageFor(request.ChannelId) };
    }

    private static HistoryCoverage BuildCoverage(
        HistoryIndexReader? reader, ProjectorState? state, long journalHead, long checkpoint, string? channel, bool halted, int tailCount, bool tailTruncated) =>
        new(
            (reader?.ReadCoverage() ?? []).Where(c => channel is null || c.ChannelId == channel).ToList(),
            state?.Generation,
            state?.BuildComplete ?? false,
            halted,
            Math.Max(0, journalHead - checkpoint),
            tailCount,
            tailTruncated);

    private static HistoryCoverage Empty(ProjectorState? state) =>
        new([], state?.Generation, state?.BuildComplete ?? false, false, 0, 0, false);

    private static HistoryHit SegmentHit(VerifiedSegment verified, HistoryCursorPosition resume)
    {
        var segment = verified.Segment;
        var echoOnly = verified.Matched.All(r => HistoryLabels.For(r) == HistoryLineLabel.RecallEcho);
        var relevance = -segment.Score;
        return new HistoryHit(
            segment.ChannelId,
            verified.Matched.Select(HistoryLine.From).ToList(),
            segment.FirstMsgId,
            segment.LastMsgId,
            SqliteDateTimeText.Parse(segment.StartTs),
            SqliteDateTimeText.Parse(segment.EndTs),
            HistoryHitSource.Indexed,
            echoOnly ? relevance * 0.5 : relevance,
            echoOnly,
            resume,
            segment.MsgIds);
    }

    private static HistoryHit MessageHit(MessageRecord row, HistoryHitSource source) =>
        new(row.ChannelId, [HistoryLine.From(row)], row.Id, row.Id, row.Timestamp, row.Timestamp, source, 0, false,
            new HistoryCursorPosition(0, 0, SqliteDateTimeText.Format(row.Timestamp), row.Id), [row.Id]);

    private static int CompareKey(MessageRecord row, string ts, long id)
    {
        var byTs = string.CompareOrdinal(SqliteDateTimeText.Format(row.Timestamp), ts);
        return byTs != 0 ? byTs : row.Id.CompareTo(id);
    }

    private async Task<(IReadOnlyList<MessageRecord> Rows, int Count, bool Truncated)> ReadTailAsync(
        HistorySourceSnapshot snapshot, long checkpoint, CancellationToken cancellationToken)
    {
        var max = this.options.CurrentValue.UnindexedTailMax;
        var ids = new HashSet<long>();
        var truncated = false;
        var after = checkpoint;
        while (!truncated)
        {
            var entries = await snapshot.ReadJournalAsync(after, 500, cancellationToken).ConfigureAwait(false);
            foreach (var id in entries.SelectMany(e => e.MessageIds))
            {
                if (ids.Count >= max)
                {
                    truncated = true;
                    break;
                }

                ids.Add(id);
            }

            if (entries.Count < 500)
            {
                break;
            }

            after = entries[^1].Seq;
        }

        var rows = await snapshot.ReadByIdsAsync(ids, cancellationToken).ConfigureAwait(false);
        return (rows, ids.Count, truncated);
    }

    private async Task<HistorySearchResult> SearchRelevanceAsync(
        HistorySourceSnapshot snapshot,
        HistoryIndexReader? reader,
        ParsedHistoryQuery parsed,
        HistorySearchRequest request,
        RecallFilter filter,
        HistoryCursorPosition position,
        IReadOnlyList<MessageRecord> tailRows,
        CancellationToken cancellationToken)
    {
        var tailHits = TailHits(tailRows, parsed, filter);

        var hits = new List<HistoryHit>();
        var deferredEchoes = new List<HistoryHit>();
        var unavailable = 0;
        var takenTail = Math.Min(request.Limit, Math.Max(0, tailHits.Count - position.TailSkip));
        hits.AddRange(tailHits.Skip(position.TailSkip).Take(takenTail));
        var tailNext = position.TailSkip + takenTail;

        var offset = position.IndexOffset;
        var exhausted = reader is null;
        var scanned = 0;
        var pageSize = request.Limit * 3;
        while (!exhausted && hits.Count < request.Limit && scanned < MaxRelevanceCandidates)
        {
            var batch = reader!.SearchRelevance(parsed.MatchExpression, new SegmentFilter(request.Channel, Format(request.Since), Format(request.Until)), pageSize, offset, parsed.Table);
            exhausted = batch.Count < pageSize;
            var (verified, missing) = await HistoryVerifier.VerifyAsync(
                snapshot, batch, parsed, filter, forTimeOrder: false, this.projector.RequestChannelRecompute, cancellationToken).ConfigureAwait(false);
            unavailable += missing;

            var consumed = 0;
            foreach (var candidate in verified)
            {
                consumed++;
                if (candidate.Matched.Count == 0)
                {
                    continue;
                }

                var hit = SegmentHit(candidate, new HistoryCursorPosition(offset + consumed, tailHits.Count, null, 0));
                if (hit.EchoOnly)
                {
                    deferredEchoes.Add(hit);
                    continue;
                }

                hits.Add(hit);
                if (hits.Count == request.Limit)
                {
                    exhausted = exhausted && consumed == batch.Count;
                    break;
                }
            }

            offset += consumed;
            scanned += consumed;
        }

        var next = new HistoryCursorPosition(offset, tailNext, null, 0);
        var hasMore = !exhausted || tailNext < tailHits.Count;
        if (request.IncludeEchoes)
        {
            hits.AddRange(deferredEchoes.OrderByDescending(e => e.Score).Take(Math.Max(0, request.Limit - hits.Count))
                .Select(e => e with { Resume = next }));
        }
        else if (hits.Count == 0 && exhausted && position == HistoryCursorPosition.Start)
        {
            hits.AddRange(deferredEchoes.OrderByDescending(e => e.Score).Take(request.Limit).Select(e => e with { Resume = next }));
        }

        return new HistorySearchResult
        {
            Hits = hits,
            HasMore = hasMore,
            Next = next,
            Unavailable = unavailable,
            Coverage = Empty(null),
            Binding = string.Empty,
        };
    }

    private async Task<HistorySearchResult> SearchTimeOrderAsync(
        HistorySourceSnapshot snapshot,
        HistoryIndexReader? reader,
        ParsedHistoryQuery parsed,
        HistorySearchRequest request,
        RecallFilter filter,
        HistoryCursorPosition position,
        IReadOnlyList<MessageRecord> tailRows,
        CancellationToken cancellationToken)
    {
        bool After(MessageRecord row) => position.AfterTs is null || CompareKey(row, position.AfterTs, position.AfterId) > 0;

        var buffer = new SortedDictionary<(string Ts, long Id), (MessageRecord Row, HistoryHitSource Source)>();
        var visibleTail = tailRows.Where(r => filter.Admits(r, forTimeOrder: true) && After(r)).ToList();
        if (visibleTail.Count > 0)
        {
            var matched = FtsRematcher.Match(parsed.MatchExpression, visibleTail, parsed.Table);
            foreach (var row in visibleTail.Where(r => matched.Contains(r.Id)))
            {
                buffer[(SqliteDateTimeText.Format(row.Timestamp), row.Id)] = (row, HistoryHitSource.Unindexed);
            }
        }

        var hits = new List<HistoryHit>();
        var unavailable = 0;
        var exhausted = reader is null;
        (string StartTs, long SegId)? segmentCursor = null;
        var since = position.AfterTs ?? Format(request.Since);
        while (hits.Count < request.Limit)
        {
            var watermark = (string?)null;
            if (!exhausted)
            {
                var batch = reader!.SearchTimeOrder(parsed.MatchExpression, new SegmentFilter(request.Channel, since, Format(request.Until)), TimeOrderBatch, segmentCursor);
                exhausted = batch.Count < TimeOrderBatch;
                if (batch.Count > 0)
                {
                    segmentCursor = (batch[^1].StartTs, batch[^1].SegId);
                    watermark = exhausted ? null : batch[^1].StartTs;
                }

                var (verified, missing) = await HistoryVerifier.VerifyAsync(
                    snapshot, batch, parsed, filter, forTimeOrder: true, this.projector.RequestChannelRecompute, cancellationToken).ConfigureAwait(false);
                unavailable += missing;
                foreach (var row in verified.SelectMany(v => v.Matched).Where(After))
                {
                    var key = (SqliteDateTimeText.Format(row.Timestamp), row.Id);
                    if (!buffer.ContainsKey(key))
                    {
                        buffer[key] = (row, HistoryHitSource.Indexed);
                    }
                }
            }

            while (hits.Count < request.Limit && buffer.Count > 0)
            {
                var first = buffer.First();
                if (watermark is not null && string.CompareOrdinal(first.Key.Ts, watermark) >= 0)
                {
                    break;
                }

                buffer.Remove(first.Key);
                hits.Add(MessageHit(first.Value.Row, first.Value.Source));
            }

            if (exhausted)
            {
                break;
            }
        }

        var hasMore = buffer.Count > 0 || !exhausted;
        if (hits.Count == 0 && !hasMore && position == HistoryCursorPosition.Start && !request.IncludeEchoes)
        {
            return await this.SearchTimeOrderAsync(
                snapshot, reader, parsed, request with { IncludeEchoes = true }, filter with { IncludeEchoes = true }, position, tailRows, cancellationToken).ConfigureAwait(false);
        }

        return new HistorySearchResult
        {
            Hits = hits,
            HasMore = hasMore,
            Next = hits.Count > 0 ? hits[^1].Resume : position,
            Unavailable = unavailable,
            Coverage = Empty(null),
            Binding = string.Empty,
        };
    }

    private static string? Format(DateTimeOffset? value) => value is { } v ? SqliteDateTimeText.Format(v) : null;
}
