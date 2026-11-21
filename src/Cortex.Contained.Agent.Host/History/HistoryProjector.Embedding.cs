using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>Outcome of one embedding round.</summary>
/// <param name="Published">Chunk rows that received a vector.</param>
/// <param name="Discarded">Vectors not published because a fence failed (generation, purge or revision).</param>
/// <param name="Failed">Chunks whose text the embedder rejected (counted towards their attempt limit).</param>
public sealed record HistoryEmbeddingRound(int Published, int Discarded, int Failed)
{
    /// <summary>Nothing to do (dense retrieval off, no embedder, or no eligible chunk).</summary>
    public static HistoryEmbeddingRound Idle { get; } = new(0, 0, 0);
}

/// <summary>
/// Embedding jobs (§4.2): chunks without a vector are the job queue, written atomically with their
/// segments. A round reads eligible jobs, recomputes each chunk's text from the source, embeds it
/// outside every lock, and publishes the vectors in one index transaction only when the generation,
/// the purge generation and the chunk's text hash (document revision) are all unchanged.
/// </summary>
public sealed partial class HistoryProjector
{
    /// <summary>Test seam: awaited after embedding and before the publication fences are checked.</summary>
    internal Func<Task>? BeforeVectorPublishHook { get; set; }

    /// <summary>Embeds up to one batch of eligible chunks of the current generation.</summary>
    public async Task<HistoryEmbeddingRound> EmbedPendingAsync(CancellationToken cancellationToken)
    {
        var settings = this.options.CurrentValue;
        if (!settings.Dense.Enabled || this.embedder is null)
        {
            return HistoryEmbeddingRound.Idle;
        }

        if (this.embedder.UnavailableReason is not null)
        {
            this.PublishStatus();
            return HistoryEmbeddingRound.Idle;
        }

        HistoryIndexDatabase? database;
        IReadOnlyList<PendingChunk> pending;
        await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            database = this.current;
            pending = database?.ReadPendingChunks(
                settings.Dense.EmbedBatchSize,
                this.time.GetUtcNow(),
                TimeSpan.FromSeconds(settings.Dense.OpenSegmentDebounceSeconds),
                settings.Dense.OpenSegmentDebounceMessages) ?? [];
        }
        finally
        {
            this.applyLock.Release();
        }

        if (database is null || pending.Count == 0)
        {
            return HistoryEmbeddingRound.Idle;
        }

        var (jobs, purgeGeneration) = await this.ReadJobTextsAsync(pending, HistoryChunkSettings.From(settings.Dense), cancellationToken).ConfigureAwait(false);
        var (vectors, rejected) = await this.EmbedJobsAsync(jobs, cancellationToken).ConfigureAwait(false);
        if (this.BeforeVectorPublishHook is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (rejected.Count > 0 && ReferenceEquals(this.current, database))
            {
                database.RecordEmbeddingFailure(rejected);
            }

            // Fences: same generation, no purge since the source read; PublishVectors checks the revision.
            if (!ReferenceEquals(this.current, database)
                || await this.source.ReadPurgeGenerationAsync(cancellationToken).ConfigureAwait(false) != purgeGeneration)
            {
                this.LogVectorsFenced(vectors.Count);
                return new HistoryEmbeddingRound(0, vectors.Count, rejected.Count);
            }

            var published = database.PublishVectors(vectors);
            this.PublishStatus();
            return new HistoryEmbeddingRound(published, Math.Max(0, vectors.Count - published), rejected.Count);
        }
        finally
        {
            this.applyLock.Release();
        }
    }

    private async Task<(List<(PendingChunk Job, string Text)> Jobs, long PurgeGeneration)> ReadJobTextsAsync(
        IReadOnlyList<PendingChunk> pending, HistoryChunkSettings chunking, CancellationToken cancellationToken)
    {
        await using var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var meta = await snapshot.ReadMetaAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await snapshot.ReadByIdsAsync(pending.SelectMany(p => p.SegmentMsgIds).Distinct().ToList(), cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.Id);
        var jobs = new List<(PendingChunk, string)>();
        foreach (var job in pending)
        {
            if (!job.SegmentMsgIds.All(rows.ContainsKey))
            {
                continue; // Deleted since projection: the journal will recompute the channel.
            }

            var segmentRows = job.SegmentMsgIds.Select(id => rows[id]).OrderBy(r => r.Timestamp.UtcTicks).ThenBy(r => r.Id).ToList();
            var chunks = HistoryChunker.Chunk(segmentRows, chunking);
            if (job.Ordinal < chunks.Count && string.Equals(chunks[job.Ordinal].Chunk.TextHash, job.TextHash, StringComparison.Ordinal))
            {
                jobs.Add((job, chunks[job.Ordinal].Text));
            }
        }

        return (jobs, meta.PurgeGeneration);
    }

    private async Task<(List<(string TextHash, float[] Vector)> Vectors, List<long> Rejected)> EmbedJobsAsync(
        List<(PendingChunk Job, string Text)> jobs, CancellationToken cancellationToken)
    {
        var vectors = new List<(string, float[])>();
        var rejected = new List<long>();
        if (jobs.Count == 0)
        {
            return (vectors, rejected);
        }

        try
        {
            var embedded = await this.embedder!.EmbedDocumentsAsync(jobs.Select(j => j.Text).ToList(), cancellationToken).ConfigureAwait(false);
            vectors.AddRange(jobs.Select((j, i) => (j.Job.TextHash, embedded[i])));
        }
        catch (EmbeddingInputTooLongException)
        {
            // Isolate the chunk the embedder rejects; the others still get their vectors.
            foreach (var (job, text) in jobs)
            {
                try
                {
                    var single = await this.embedder!.EmbedDocumentsAsync([text], cancellationToken).ConfigureAwait(false);
                    vectors.Add((job.TextHash, single[0]));
                }
                catch (EmbeddingInputTooLongException)
                {
                    rejected.Add(job.ChunkId);
                }
            }
        }

        return (vectors, rejected);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "History vectors for {Count} chunks not published: the generation or purge generation changed while embedding")]
    private partial void LogVectorsFenced(int count);
}
