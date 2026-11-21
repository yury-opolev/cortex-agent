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
            allPending.Count - pending.Count);
    }

    // Routine maintenance (weekly VACUUM) is housekeeping, not an erasure gap.
    private static bool IsMaintenance(ErasureTask task) =>
        task.Id.StartsWith(VacuumErasureStep.MaintenanceTaskPrefix, StringComparison.Ordinal);
}
