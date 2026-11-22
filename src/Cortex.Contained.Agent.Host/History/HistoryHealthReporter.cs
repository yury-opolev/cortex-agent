using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>The <c>history</c> block of <c>/health</c> (§13).</summary>
public sealed record HistoryHealth(
    bool Enabled,
    bool ToolsEnabled,
    string SqliteVersion,
    long? Generation,
    bool BuildComplete,
    bool BuildInProgress,
    long CheckpointLag,
    long JournalBacklog,
    bool Halted,
    string? LastError,
    int PendingErasureTasks,
    IReadOnlyList<string> PendingErasureSteps,
    int MaintenancePendingTasks)
{
    /// <summary><c>open</c>, <c>awaitingKey</c> (no storage key from the Bridge yet) or <c>failed</c> (wrong key).</summary>
    public string Storage { get; init; } = "open";

    /// <summary>Dense retrieval state and embedding lag (§4.2).</summary>
    public HistoryDenseHealth Dense { get; init; } = new("off", 0, 0, 0, 0, null, null);
}

/// <summary>The <c>history.dense</c> block of <c>/health</c>.</summary>
/// <param name="State"><c>off</c>, <c>on</c> or <c>unavailable: reason</c>.</param>
/// <param name="Embedded">Chunks with a vector.</param>
/// <param name="Total">All chunks of the served generation.</param>
/// <param name="Pending">Chunks waiting for a vector (embedding lag).</param>
/// <param name="Failed">Chunks the embedder rejected after the attempt limit.</param>
/// <param name="OldestPendingQueuedAt">When the oldest pending chunk was queued, or null.</param>
/// <param name="LastError">Last embedding failure, or null.</param>
public sealed record HistoryDenseHealth(
    string State, long Embedded, long Total, long Pending, long Failed, DateTimeOffset? OldestPendingQueuedAt, string? LastError)
{
    /// <summary>Builds the block from a projector status snapshot.</summary>
    public static HistoryDenseHealth From(HistoryStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var chunks = status.Chunks;
        return new HistoryDenseHealth(
            status.DenseState,
            chunks?.Embedded ?? 0,
            chunks?.Total ?? 0,
            chunks?.Pending ?? 0,
            chunks?.Failed ?? 0,
            chunks?.OldestPendingQueuedAt,
            status.LastEmbeddingError);
    }
}

/// <summary>Builds <see cref="HistoryHealth"/> from options, projector status and <c>messages.db</c> Meta.</summary>
public sealed class HistoryHealthReporter
{
    private readonly IHistorySource source;
    private readonly IHistoryErasureStore erasure;
    private readonly HistoryStatus status;
    private readonly HistoryToolsSwitch toolsSwitch;
    private readonly IOptionsMonitor<HistoryOptions> options;
    private readonly Storage.IStorageState? storage;

    public HistoryHealthReporter(
        IHistorySource source,
        IHistoryErasureStore erasure,
        HistoryStatus status,
        HistoryToolsSwitch toolsSwitch,
        IOptionsMonitor<HistoryOptions> options,
        Storage.IStorageState? storage = null)
    {
        this.storage = storage;
        this.source = source;
        this.erasure = erasure;
        this.status = status;
        this.toolsSwitch = toolsSwitch;
        this.options = options;
    }

    /// <summary>Current health snapshot.</summary>
    public async Task<HistoryHealth> SnapshotAsync(CancellationToken cancellationToken)
    {
        if (this.storage is { State: not Storage.StorageState.Open } locked)
        {
            // Never wait for the key here: health must answer while the store is locked.
            var lockedState = this.status.Snapshot();
            return new HistoryHealth(
                this.options.CurrentValue.Enabled,
                this.toolsSwitch.IsEnabled,
                SqliteRuntimeInfo.Capabilities.Version,
                lockedState.Generation,
                lockedState.BuildComplete,
                lockedState.BuildInProgress,
                0,
                0,
                lockedState.Halted,
                lockedState.LastError,
                0,
                [],
                0)
            {
                Storage = locked.State == Storage.StorageState.Failed ? "failed" : "awaitingKey",
                Dense = HistoryDenseHealth.From(lockedState),
            };
        }

        HistorySourceMeta meta;
        await using (var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false))
        {
            meta = await snapshot.ReadMetaAsync(cancellationToken).ConfigureAwait(false);
        }

        var allPending = await this.erasure.ReadPendingErasureAsync(cancellationToken).ConfigureAwait(false);
        var pending = allPending.Where(t => !IsMaintenance(t)).ToList();
        var state = this.status.Snapshot();
        return new HistoryHealth(
            this.options.CurrentValue.Enabled,
            this.toolsSwitch.IsEnabled,
            SqliteRuntimeInfo.Capabilities.Version,
            state.Generation,
            state.BuildComplete,
            state.BuildInProgress,
            Math.Max(0, meta.JournalHead - state.Checkpoint),
            Math.Max(0, meta.JournalHead - meta.JournalPrunedThrough),
            state.Halted,
            state.LastError,
            pending.Count,
            pending.SelectMany(t => t.Steps).Distinct(StringComparer.Ordinal).ToList(),
            allPending.Count - pending.Count)
        {
            Dense = HistoryDenseHealth.From(state),
        };
    }

    // Routine maintenance (weekly VACUUM) is housekeeping, not an erasure gap.
    private static bool IsMaintenance(ErasureTask task) =>
        task.Id.StartsWith(VacuumErasureStep.MaintenanceTaskPrefix, StringComparison.Ordinal);
}
