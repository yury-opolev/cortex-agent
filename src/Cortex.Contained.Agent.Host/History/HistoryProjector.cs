using System.Collections.Concurrent;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>Outcome of applying the journal to the current generation.</summary>
public enum ProjectorApplyResult
{
    /// <summary>The generation is up to date.</summary>
    Applied,

    /// <summary>No generation is being served.</summary>
    NoGeneration,

    /// <summary>Source identity or configuration changed: the generation must be retired and rebuilt.</summary>
    RebuildRequired,
}

/// <summary>
/// Maintains the history index (§9.5): resume-or-rebuild at startup, whole-channel journal
/// application in index transactions, journal pruning with a floor, bounded resumable builds,
/// and an atomic cut-over under the <see cref="HistoryAccessGate"/> write lease.
/// <para>
/// Lock order is always write lease → <c>applyLock</c>. Every index write runs under
/// <c>applyLock</c>; recall reads <see cref="Current"/> only while holding a read lease, and the
/// current generation is swapped or retired only under the write lease.
/// </para>
/// </summary>
public sealed partial class HistoryProjector : IDisposable
{
    private const int JournalBatch = 500;
    private const int MaxFencedAttempts = 3;

    private readonly IHistorySource source;
    private readonly IHistoryErasureStore erasure;
    private readonly HistoryIndexGenerations generations;
    private readonly HistoryAccessGate gate;
    private readonly IOptionsMonitor<HistoryOptions> options;
    private readonly HistoryStatus status;
    private readonly ILogger<HistoryProjector> logger;
    private readonly SemaphoreSlim applyLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> recomputeRequests = new(StringComparer.Ordinal);

    private HistoryIndexDatabase? current;
    private BuildState? build;
    private string? rebuildReason = "not initialized";
    private volatile bool initialized;

    public HistoryProjector(
        IHistorySource source,
        IHistoryErasureStore erasure,
        HistoryIndexGenerations generations,
        HistoryAccessGate gate,
        IOptionsMonitor<HistoryOptions> options,
        HistoryStatus status,
        ILogger<HistoryProjector> logger)
    {
        this.source = source;
        this.erasure = erasure;
        this.generations = generations;
        this.gate = gate;
        this.options = options;
        this.status = status;
        this.logger = logger;
    }

    /// <summary>Test seam: invoked at each cut-over step; throwing simulates a crash there.</summary>
    internal Action<CutOverStep>? CutOverHook { get; set; }

    /// <summary>The served generation. Read it only while holding a <see cref="HistoryAccessGate"/> read lease.</summary>
    public HistoryIndexDatabase? Current => Volatile.Read(ref this.current);

    /// <summary>True when no generation is served, the served one must be replaced, or a build is unfinished.</summary>
    public bool NeedsRebuild => this.Current is null || this.rebuildReason is not null || this.build is not null;

    /// <summary>True once <see cref="InitializeAsync"/> has opened (or ruled out) the on-disk generations.</summary>
    public bool IsInitialized => this.initialized;

    /// <summary>Asks the next journal application to recompute <paramref name="channelId"/> (fingerprint mismatch).</summary>
    public void RequestChannelRecompute(string channelId) => this.recomputeRequests.TryAdd(channelId, 0);

    /// <summary>
    /// Opens the current generation if it may resume, keeps one resumable in-progress build,
    /// and retires every other generation file.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fingerprint = HistoryConfigFingerprint.Compute(this.options.CurrentValue);
            HistorySourceMeta meta;
            await using (var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false))
            {
                meta = await snapshot.ReadMetaAsync(cancellationToken).ConfigureAwait(false);
            }

            var currentGen = this.generations.ReadCurrent();
            foreach (var gen in this.generations.List())
            {
                HistoryIndexDatabase database;
                try
                {
                    database = this.generations.Open(gen);
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 26)
                {
                    // Plaintext from before encryption at rest, or another key: unusable, so erased and rebuilt.
                    await this.DeleteTrackedAsync(gen, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var state = database.ReadState();
                if (gen == currentGen && ProjectorStartupDecision.RebuildReason(meta, state, fingerprint) is null)
                {
                    this.current = database;
                    continue;
                }

                if (gen != currentGen && this.build is null && !state.BuildComplete && IsResumable(meta, state, fingerprint))
                {
                    this.build = new BuildState(database, gen, state.CheckpointJournalSeq);
                    continue;
                }

                await this.RetireAsync(database, gen, cancellationToken).ConfigureAwait(false);
            }

            this.rebuildReason = this.current is null ? "no servable generation" : null;
            this.initialized = true;
            this.PublishStatus();
        }
        finally
        {
            this.applyLock.Release();
        }
    }

    /// <summary>
    /// Applies pending journal entries (and recompute requests) to the current generation in
    /// batches, then prunes the journal up to the floor.
    /// </summary>
    public async Task<ProjectorApplyResult> ApplyPendingAsync(CancellationToken cancellationToken)
    {
        await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = this.current;
            if (database is null)
            {
                return ProjectorApplyResult.NoGeneration;
            }

            if (!await this.ApplyJournalAsync(database, includeRecompute: true, cancellationToken).ConfigureAwait(false))
            {
                this.rebuildReason = "source identity or configuration changed";
                return ProjectorApplyResult.RebuildRequired;
            }

            var floor = database.ReadState().CheckpointJournalSeq;
            if (this.build is { } active)
            {
                floor = Math.Min(floor, active.Watermark);
            }

            await this.source.PruneJournalAsync(floor, cancellationToken).ConfigureAwait(false);
            this.PublishStatus();
            return ProjectorApplyResult.Applied;
        }
        finally
        {
            this.applyLock.Release();
        }
    }

    /// <summary>
    /// Builds (or resumes) a new generation from a consistent snapshot in bounded batches,
    /// replays the journal tail and cuts over atomically under the write lease (§9.5 steps 1–9).
    /// </summary>
    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var settings = SegmenterSettings.From(this.options.CurrentValue);
        var batchMessages = this.options.CurrentValue.BuildBatchMessages;
        BuildState state;

        await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state = this.build ?? await this.StartBuildAsync(cancellationToken).ConfigureAwait(false);
            this.build = state;
            this.PublishStatus();
        }
        finally
        {
            this.applyLock.Release();
        }

        this.LogBuildStarted(state.Generation, state.Watermark);

        await using (var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false))
        {
            var meta = await snapshot.ReadMetaAsync(cancellationToken).ConfigureAwait(false);
            var stateRow = state.Database.ReadState();
            if (!string.Equals(meta.DbInstanceId, stateRow.SourceInstanceId, StringComparison.Ordinal)
                || meta.RestoreGeneration != stateRow.SourceRestoreGeneration)
            {
                await this.AbandonBuildAsync(state, cancellationToken).ConfigureAwait(false);
                return;
            }

            var done = state.Database.ReadBuildProgress();
            var sizes = await snapshot.ReadChannelSizesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var batch in Batch(sizes.Where(kv => !done.Contains(kv.Key)), batchMessages))
            {
                var segments = new Dictionary<string, IReadOnlyList<HistorySegment>>(StringComparer.Ordinal);
                foreach (var channelId in batch)
                {
                    var rows = await snapshot.ReadChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
                    segments[channelId] = HistorySegmenter.Segment(channelId, rows, settings);
                }

                if (!await this.WriteBuildBatchAsync(state, new IndexBatch(segments) { BuildProgress = batch }, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await Task.Yield();
            }
        }

        // Replay (W, head] without blocking recall, then the final tail under the write lease.
        await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(this.build, state))
            {
                return;
            }

            if (!await this.ApplyJournalAsync(state.Database, includeRecompute: false, cancellationToken).ConfigureAwait(false))
            {
                await this.AbandonBuildLockedAsync(state, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            this.applyLock.Release();
        }

        HistoryIndexDatabase? retired;
        long? retiredGen;
        await using (await this.gate.AcquireWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(this.build, state))
                {
                    return;
                }

                if (!await this.ApplyJournalAsync(state.Database, includeRecompute: false, cancellationToken).ConfigureAwait(false))
                {
                    await this.AbandonBuildLockedAsync(state, cancellationToken).ConfigureAwait(false);
                    return;
                }

                this.CutOverHook?.Invoke(CutOverStep.BeforeComplete);
                state.Database.Apply(new IndexBatch(new Dictionary<string, IReadOnlyList<HistorySegment>>()) { MarkBuildComplete = true });
                this.CutOverHook?.Invoke(CutOverStep.AfterComplete);
                this.generations.WriteCurrent(state.Generation);
                this.CutOverHook?.Invoke(CutOverStep.AfterPointer);

                retired = this.current;
                retiredGen = retired is null ? null : retired.ReadState().Generation;
                Volatile.Write(ref this.current, state.Database);
                this.build = null;
                this.rebuildReason = null;
                this.PublishStatus();
            }
            finally
            {
                this.applyLock.Release();
            }
        }

        this.LogBuildCompleted(state.Generation);
        if (retired is not null && retiredGen is { } gen)
        {
            await this.RetireAsync(retired, gen, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Retires the current generation (identity or configuration changed). The caller either
    /// already holds the write lease (<paramref name="writeLeaseHeld"/>) or it is acquired here.
    /// </summary>
    public async Task RetireCurrentAsync(bool writeLeaseHeld, CancellationToken cancellationToken)
    {
        var lease = writeLeaseHeld ? null : await this.gate.AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (this.current is not { } database)
                {
                    return;
                }

                var gen = database.ReadState().Generation;
                Volatile.Write(ref this.current, null);
                this.rebuildReason ??= "retired";
                this.PublishStatus();
                await this.RetireAsync(database, gen, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                this.applyLock.Release();
            }
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Erasure step <see cref="ErasureSteps.Index"/>; the caller holds the write lease. Drops any
    /// in-progress build file, applies pending purges to the current generation, then optimizes
    /// FTS and truncates its WAL. With the feature disabled every generation file is deleted.
    /// Returns true when the index holds no purged content.
    /// </summary>
    public async Task<bool> EraseAsync(CancellationToken cancellationToken)
    {
        if (this.options.CurrentValue.Enabled && !this.initialized)
        {
            // Generation files are not open yet; the purge journal entry is applied at startup.
            return false;
        }

        await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.build is { } active)
            {
                active.Database.Dispose();
                this.build = null;
                await this.DeleteTrackedAsync(active.Generation, cancellationToken).ConfigureAwait(false);
            }

            if (!this.options.CurrentValue.Enabled)
            {
                this.current?.Dispose();
                Volatile.Write(ref this.current, null);
                var allGone = true;
                foreach (var gen in this.generations.List())
                {
                    allGone &= await this.DeleteTrackedAsync(gen, cancellationToken).ConfigureAwait(false);
                }

                this.PublishStatus();
                return allGone;
            }

            if (this.current is not { } database)
            {
                return true;
            }

            if (!await this.ApplyJournalAsync(database, includeRecompute: false, cancellationToken).ConfigureAwait(false))
            {
                var gen = database.ReadState().Generation;
                Volatile.Write(ref this.current, null);
                this.rebuildReason = "source identity changed";
                this.PublishStatus();
                return await this.RetireAsync(database, gen, cancellationToken).ConfigureAwait(false);
            }

            database.Optimize();
            return database.CheckpointTruncate().Complete;
        }
        finally
        {
            this.applyLock.Release();
        }
    }

    /// <summary>Erasure step for a retired generation left over from a failed unlink.</summary>
    public Task<bool> DeleteRetiredAsync(long generation) =>
        Task.FromResult(generation == this.Current?.ReadState().Generation || this.generations.Delete(generation));

    /// <summary>Runs <c>VACUUM</c> and a truncating checkpoint on the current generation.</summary>
    public async Task VacuumAsync(CancellationToken cancellationToken)
    {
        await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.current?.Vacuum();
            this.current?.CheckpointTruncate();
        }
        finally
        {
            this.applyLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.current?.Dispose();
        this.build?.Database.Dispose();
        this.applyLock.Dispose();
    }

    private static bool IsResumable(HistorySourceMeta meta, ProjectorState state, string fingerprint) =>
        string.Equals(state.SourceInstanceId, meta.DbInstanceId, StringComparison.Ordinal)
        && state.SourceRestoreGeneration == meta.RestoreGeneration
        && state.AppliedPurgeGeneration <= meta.PurgeGeneration
        && string.Equals(state.ConfigFingerprint, fingerprint, StringComparison.Ordinal)
        && state.CheckpointJournalSeq >= meta.JournalPrunedThrough
        && state.CheckpointJournalSeq <= meta.JournalHead;

    private static IEnumerable<IReadOnlyList<string>> Batch(IEnumerable<KeyValuePair<string, int>> channelSizes, int maxMessages)
    {
        var batch = new List<string>();
        var size = 0;
        foreach (var (channelId, count) in channelSizes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (batch.Count > 0 && size + count > maxMessages)
            {
                yield return batch;
                batch = [];
                size = 0;
            }

            batch.Add(channelId);
            size += count;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private async Task<BuildState> StartBuildAsync(CancellationToken cancellationToken)
    {
        await using var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var meta = await snapshot.ReadMetaAsync(cancellationToken).ConfigureAwait(false);
        var gen = this.generations.Next();
        var database = this.generations.Create(
            gen,
            new ProjectorState(gen, meta.JournalHead, meta.DbInstanceId, meta.RestoreGeneration, HistoryConfigFingerprint.Compute(this.options.CurrentValue), false, "[]")
            {
                AppliedPurgeGeneration = meta.PurgeGeneration,
            });
        return new BuildState(database, gen, meta.JournalHead);
    }

    private async Task<bool> WriteBuildBatchAsync(BuildState state, IndexBatch batch, CancellationToken cancellationToken)
    {
        await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(this.build, state))
            {
                return false;
            }

            state.Database.Apply(batch);
            return true;
        }
        finally
        {
            this.applyLock.Release();
        }
    }

    private async Task AbandonBuildAsync(BuildState state, CancellationToken cancellationToken)
    {
        await this.applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.AbandonBuildLockedAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.applyLock.Release();
        }
    }

    private async Task AbandonBuildLockedAsync(BuildState state, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(this.build, state))
        {
            return;
        }

        this.build = null;
        state.Database.Dispose();
        await this.DeleteTrackedAsync(state.Generation, cancellationToken).ConfigureAwait(false);
        this.PublishStatus();
    }

    /// <summary>
    /// Applies journal entries after the generation's checkpoint (whole-channel recomputation).
    /// Returns false when the source identity or configuration no longer matches the generation.
    /// A batch computed before a purge committed is never published: it is discarded and recomputed
    /// from a fresh snapshot (up to three times, then the failure halts the projector).
    /// Must run under <c>applyLock</c>.
    /// </summary>
    private async Task<bool> ApplyJournalAsync(HistoryIndexDatabase database, bool includeRecompute, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await this.ApplyJournalOnceAsync(database, includeRecompute, cancellationToken).ConfigureAwait(false);
            }
            catch (PublicationFencedException) when (attempt < MaxFencedAttempts)
            {
                this.LogPublicationFenced(attempt);
            }
        }
    }

    private async Task<bool> ApplyJournalOnceAsync(HistoryIndexDatabase database, bool includeRecompute, CancellationToken cancellationToken)
    {
        var settings = SegmenterSettings.From(this.options.CurrentValue);
        var fingerprint = HistoryConfigFingerprint.Compute(this.options.CurrentValue);
        var state = database.ReadState();

        await using var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var meta = await snapshot.ReadMetaAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(meta.DbInstanceId, state.SourceInstanceId, StringComparison.Ordinal)
            || meta.RestoreGeneration != state.SourceRestoreGeneration
            || !string.Equals(state.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        var checkpoint = state.CheckpointJournalSeq;
        while (true)
        {
            var entries = await snapshot.ReadJournalAsync(checkpoint, JournalBatch, cancellationToken).ConfigureAwait(false);
            var channels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry.ChannelId is not null)
                {
                    channels.Add(entry.ChannelId);
                    continue;
                }

                channels.UnionWith(database.ReadIndexedChannels());
                channels.UnionWith(await snapshot.ReadChannelIdsAsync(cancellationToken).ConfigureAwait(false));
            }

            if (includeRecompute)
            {
                foreach (var channelId in this.recomputeRequests.Keys)
                {
                    if (this.recomputeRequests.TryRemove(channelId, out _))
                    {
                        channels.Add(channelId);
                    }
                }
            }

            if (entries.Count == 0 && channels.Count == 0)
            {
                return true;
            }

            var segments = new Dictionary<string, IReadOnlyList<HistorySegment>>(StringComparer.Ordinal);
            foreach (var channelId in channels)
            {
                var rows = await snapshot.ReadChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
                segments[channelId] = HistorySegmenter.Segment(channelId, rows, settings);
            }

            long? newCheckpoint = entries.Count > 0 ? entries[^1].Seq : null;
            long? appliedPurge = entries.Where(e => e.Op == HistoryJournalOp.Purge).Select(e => e.PurgeGeneration).Max();

            // Fence: never publish a batch computed from a snapshot that a later purge has superseded.
            if (await this.source.ReadPurgeGenerationAsync(cancellationToken).ConfigureAwait(false) != meta.PurgeGeneration)
            {
                throw new PublicationFencedException();
            }

            database.Apply(new IndexBatch(segments) { CheckpointSeq = newCheckpoint, AppliedPurgeGeneration = appliedPurge });
            checkpoint = newCheckpoint ?? checkpoint;
            if (entries.Count < JournalBatch)
            {
                return true;
            }

            includeRecompute = false;
        }
    }

    /// <summary>Disposes and unlinks a generation, tracked in <c>PendingErasure</c> until done.</summary>
    private async Task<bool> RetireAsync(HistoryIndexDatabase database, long generation, CancellationToken cancellationToken)
    {
        database.Dispose();
        return await this.DeleteTrackedAsync(generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DeleteTrackedAsync(long generation, CancellationToken cancellationToken)
    {
        var step = ErasureSteps.RetiredGeneration(generation);
        var task = new ErasureTask
        {
            Id = "retire-" + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            Steps = [step],
        };
        await this.erasure.AppendErasureTaskAsync(task, cancellationToken).ConfigureAwait(false);
        if (!this.generations.Delete(generation))
        {
            this.LogRetireDeferred(generation);
            return false;
        }

        await this.erasure.CompleteErasureStepAsync(task.Id, step, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void PublishStatus()
    {
        var database = this.current;
        var state = database?.ReadState();
        this.status.Update(s => s with
        {
            Generation = state?.Generation,
            BuildComplete = state?.BuildComplete ?? false,
            BuildInProgress = this.build is not null,
            Checkpoint = state?.CheckpointJournalSeq ?? 0,
        });
    }

    private sealed record BuildState(HistoryIndexDatabase Database, long Generation, long Watermark);

    /// <summary>A purge committed after the batch's snapshot was taken; the batch must be recomputed.</summary>
    private sealed class PublicationFencedException : Exception
    {
        public PublicationFencedException()
            : base("A purge committed while the batch was computed; it was not published.")
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "History index batch fenced by a concurrent purge (attempt {Attempt}); recomputing")]
    private partial void LogPublicationFenced(int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "History index build of generation {Generation} started at journal watermark {Watermark}")]
    private partial void LogBuildStarted(long generation, long watermark);

    [LoggerMessage(Level = LogLevel.Information, Message = "History index generation {Generation} is now current")]
    private partial void LogBuildCompleted(long generation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not unlink retired history index generation {Generation}; erasure step stays pending")]
    private partial void LogRetireDeferred(long generation);
}
