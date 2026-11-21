using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Hybrid relevance (§4.2): the BM25 list and the exact dense list are verified against the source,
/// fused (RRF by default, BM25 priority for exact intent), optionally re-weighted by recency, and
/// near-duplicate worker segments are collapsed. A dense hit is served only while its segment is
/// exactly as embedded (fingerprint = document revision); its lines are the best chunk's visible rows.
/// </summary>
public sealed partial class HistoryRecallService
{
    private const int KeywordCandidates = 50;

    private static List<HistoryHit> TailHits(IReadOnlyList<MessageRecord> tailRows, ParsedHistoryQuery parsed, RecallFilter filter)
    {
        var visibleTail = tailRows.Where(r => filter.Admits(r, forTimeOrder: false)).ToList();
        var tailMatched = visibleTail.Count == 0 ? new HashSet<long>() : FtsRematcher.Match(parsed.MatchExpression, visibleTail, parsed.Table);
        return visibleTail.Where(r => tailMatched.Contains(r.Id))
            .GroupBy(r => r.ChannelId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select((g, i) =>
            {
                var lines = g.OrderBy(r => r.Timestamp.UtcTicks).ThenBy(r => r.Id).ToList();
                return new HistoryHit(g.Key, lines.Select(HistoryLine.From).ToList(), lines.Min(r => r.Id), lines.Max(r => r.Id),
                    lines[0].Timestamp, lines[^1].Timestamp, HistoryHitSource.Unindexed, 0, false, new HistoryCursorPosition(0, i + 1, null, 0),
                    lines.Select(r => r.Id).ToList());
            })
            .ToList();
    }

    private static bool EchoOnly(IReadOnlyList<MessageRecord> rows) => rows.All(r => HistoryLabels.For(r) == HistoryLineLabel.RecallEcho);

    private static string? AuthorKey(IReadOnlyList<MessageRecord> rows, string channelId)
    {
        var labels = rows.Select(HistoryLabels.For).Distinct().ToList();
        return labels is [HistoryLineLabel.Delivered or HistoryLineLabel.Internal]
            ? HistoryLabels.Text(labels[0]) + "@" + channelId
            : null;
    }

    /// <summary>Embeds the query (outside the read lease) or says why this call is keyword-only.</summary>
    private async Task<(float[]? Vector, string? Note)> EmbedQueryAsync(HistorySearchRequest request, CancellationToken cancellationToken)
    {
        var dense = this.options.CurrentValue.Dense;
        if (!dense.Enabled)
        {
            return (null, "off");
        }

        if (request.Order == HistoryOrder.Time || request.Mode == HistorySearchMode.Keyword)
        {
            return (null, request.Order == HistoryOrder.Time ? "not used: order=time is exhaustive keyword" : "not used: mode=keyword");
        }

        if (this.embedder is null)
        {
            return (null, "unavailable: no embedder configured");
        }

        if (this.embedder.UnavailableReason is { } reason)
        {
            return (null, "unavailable: " + reason);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(dense.QueryTimeoutMs);
        try
        {
            return (await this.embedder.EmbedQueryAsync(request.Query, timeout.Token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "fallback: the query embedding timed out");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return (null, "fallback: the embedder failed");
        }
    }

    private async Task<HistorySearchResult> SearchHybridAsync(
        HistorySourceSnapshot snapshot,
        HistoryIndexReader reader,
        ParsedHistoryQuery parsed,
        HistorySearchRequest request,
        RecallFilter filter,
        HistoryCursorPosition position,
        IReadOnlyList<MessageRecord> tailRows,
        float[] queryVector,
        CancellationToken cancellationToken)
    {
        var settings = this.options.CurrentValue;
        var tailHits = TailHits(tailRows, parsed, filter);

        // Keyword list: BM25 candidates verified and re-matched against the source (§10).
        var keywordSegments = reader.SearchRelevance(
            parsed.MatchExpression, new SegmentFilter(request.Channel, Format(request.Since), Format(request.Until)), KeywordCandidates, 0, parsed.Table);
        var (keywordVerified, keywordMissing) = await HistoryVerifier.VerifyAsync(
            snapshot, keywordSegments, parsed, filter, forTimeOrder: false, this.projector.RequestChannelRecompute, cancellationToken).ConfigureAwait(false);
        var keyword = keywordVerified.Where(v => v.Matched.Count > 0).ToDictionary(v => v.Segment.SegId);

        // Dense list: exact scan with the filters applied before scoring; best chunk per segment.
        var bestChunk = new Dictionary<long, DenseHit>();
        foreach (var hit in reader.SearchDense(
            queryVector,
            new DenseFilter(request.Channel, Format(request.Since), Format(request.Until), request.IncludeInternal),
            settings.Dense.Candidates,
            settings.Dense.Int8Threshold,
            settings.Dense.Int8Oversample))
        {
            bestChunk.TryAdd(hit.SegId, hit);
        }

        var (dense, denseMissing) = await this.VerifyDenseAsync(snapshot, reader, bestChunk, filter, cancellationToken).ConfigureAwait(false);
        var segments = keyword.Values.Select(v => v.Segment).Concat(dense.Values.Select(v => v.Segment))
            .GroupBy(s => s.SegId).ToDictionary(g => g.Key, g => g.First());

        IReadOnlyList<MessageRecord> Rows(long segId) =>
            (keyword.TryGetValue(segId, out var k) ? k.Matched : [])
            .Concat(dense.TryGetValue(segId, out var d) ? d.Rows : [])
            .DistinctBy(r => r.Id)
            .OrderBy(r => r.Timestamp.UtcTicks).ThenBy(r => r.Id)
            .ToList();

        (List<long> Order, Dictionary<long, FusedCandidate> Scores, int Collapsed) Rank(bool includeEchoes)
        {
            var keywordList = keyword.Values.Where(v => includeEchoes || !EchoOnly(v.Matched))
                .Select(v => new RankedCandidate(v.Segment.SegId, -v.Segment.Score)).ToList();
            var denseList = bestChunk.Values.Where(h => dense.TryGetValue(h.SegId, out var v) && (includeEchoes || !EchoOnly(v.Rows)))
                .Select(h => new RankedCandidate(h.SegId, h.Score)).ToList();
            var now = this.time.GetUtcNow();
            var fused = HybridFusion.Fuse(keywordList, denseList, settings.Ranking, QueryIntent.HasExactIntent(request.Query))
                .Select(f => f with
                {
                    Score = f.Score * RecencyPrior.Multiplier(
                        SqliteDateTimeText.Parse(segments[f.SegId].EndTs), now, settings.Ranking.RecencyWeight, settings.Ranking.RecencyHalfLifeDays),
                })
                .OrderByDescending(f => f.Score).ThenBy(f => f.KeywordRank ?? int.MaxValue).ThenBy(f => f.SegId)
                .ToList();
            var order = fused.Select(f => f.SegId).ToList();
            var collapsed = 0;
            if (settings.Ranking.CollapseWorkerDuplicates && fused.Count > 1)
            {
                var vectors = reader.ReadChunkVectors(bestChunk.Values.Select(h => h.ChunkId).ToList());
                var candidates = fused.Select(f => new CollapseCandidate(
                    f.SegId,
                    f.Score,
                    AuthorKey(Rows(f.SegId), segments[f.SegId].ChannelId),
                    bestChunk.TryGetValue(f.SegId, out var best) && vectors.TryGetValue(best.ChunkId, out var vector) ? vector : null)).ToList();
                var (collapsedOrder, count) = NearDuplicateCollapse.Apply(candidates, settings.Ranking.MmrLambda, settings.Ranking.DuplicateSimilarity);
                order = [.. collapsedOrder];
                collapsed = count;
            }

            return (order, fused.ToDictionary(f => f.SegId), collapsed);
        }

        var ranked = Rank(request.IncludeEchoes);
        if (ranked.Order.Count == 0 && tailHits.Count == 0 && position == HistoryCursorPosition.Start && !request.IncludeEchoes)
        {
            ranked = Rank(includeEchoes: true); // Only echoes match: show them rather than nothing.
        }

        var hits = new List<HistoryHit>();
        var takenTail = Math.Min(request.Limit, Math.Max(0, tailHits.Count - position.TailSkip));
        hits.AddRange(tailHits.Skip(position.TailSkip).Take(takenTail));
        var tailNext = position.TailSkip + takenTail;
        var offset = position.IndexOffset;
        while (hits.Count < request.Limit && offset < ranked.Order.Count)
        {
            var segId = ranked.Order[offset++];
            var segment = segments[segId];
            var rows = Rows(segId);
            var echoOnly = EchoOnly(rows);
            var score = ranked.Scores[segId].Score;
            hits.Add(new HistoryHit(
                segment.ChannelId,
                rows.Select(HistoryLine.From).ToList(),
                segment.FirstMsgId,
                segment.LastMsgId,
                SqliteDateTimeText.Parse(segment.StartTs),
                SqliteDateTimeText.Parse(segment.EndTs),
                HistoryHitSource.Indexed,
                echoOnly ? score * 0.5 : score,
                echoOnly,
                new HistoryCursorPosition(offset, tailHits.Count, null, 0),
                segment.MsgIds)
            {
                MatchedBy = keyword.ContainsKey(segId) && dense.ContainsKey(segId) ? HistoryMatchKind.Both
                    : keyword.ContainsKey(segId) ? HistoryMatchKind.Keyword
                    : HistoryMatchKind.Semantic,
            });
        }

        return new HistorySearchResult
        {
            Hits = hits,
            HasMore = offset < ranked.Order.Count || tailNext < tailHits.Count,
            Next = new HistoryCursorPosition(offset, tailNext, null, 0),
            Unavailable = keywordMissing + denseMissing,
            CollapsedDuplicates = ranked.Collapsed,
            TopSimilarity = bestChunk.Values.Where(h => dense.ContainsKey(h.SegId)).Select(h => (double?)h.Score).Max() ?? 0,
            Coverage = Empty(null),
            Binding = string.Empty,
        };
    }

    /// <summary>
    /// Re-reads the members of each dense candidate on the caller's snapshot. A segment whose rows changed
    /// since projection (fingerprint mismatch) is dropped and queued for recomputation: its vectors may
    /// describe text that no longer exists. The shown rows are the best chunk's rows visible to the filter.
    /// </summary>
    private async Task<(Dictionary<long, DenseVerified> Verified, int Unavailable)> VerifyDenseAsync(
        HistorySourceSnapshot snapshot,
        HistoryIndexReader reader,
        Dictionary<long, DenseHit> bestChunk,
        RecallFilter filter,
        CancellationToken cancellationToken)
    {
        var segments = reader.ReadSegmentsById(bestChunk.Keys.ToList());
        var rows = (await snapshot.ReadByIdsAsync(segments.SelectMany(s => s.MsgIds).Distinct().ToList(), cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.Id);
        var verified = new Dictionary<long, DenseVerified>();
        var unavailable = 0;
        foreach (var segment in segments)
        {
            var members = segment.MsgIds.Where(rows.ContainsKey).Select(id => rows[id])
                .OrderBy(r => r.Timestamp.UtcTicks).ThenBy(r => r.Id).ToList();
            unavailable += segment.MsgIds.Count - members.Count;
            if (members.Count != segment.MsgIds.Count
                || !string.Equals(HistoryFingerprint.Compute(members), segment.Fingerprint, StringComparison.Ordinal))
            {
                this.projector.RequestChannelRecompute(segment.ChannelId);
                continue;
            }

            if (!bestChunk.TryGetValue(segment.SegId, out var best))
            {
                continue;
            }

            var chunkMembers = best.MsgIds.ToHashSet();
            var visible = members.Where(r => chunkMembers.Contains(r.Id) && filter.Admits(r, forTimeOrder: false)).ToList();
            if (visible.Count > 0)
            {
                verified[segment.SegId] = new DenseVerified(segment, visible);
            }
        }

        return (verified, unavailable);
    }

    private sealed record DenseVerified(IndexedSegment Segment, IReadOnlyList<MessageRecord> Rows);
}
