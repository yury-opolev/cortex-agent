using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>Read side of <c>messages.db</c> used by the projector and recall tools.</summary>
public interface IHistorySource
{
    /// <summary>Opens a read-only, consistent snapshot on a separate connection.</summary>
    Task<HistorySourceSnapshot> OpenSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Deletes journal rows with <c>Seq ≤ throughSeq</c> and advances <c>JournalPrunedThrough</c>.</summary>
    Task PruneJournalAsync(long throughSeq, CancellationToken cancellationToken);

    /// <summary>The committed <c>Meta.PurgeGeneration</c>, read fresh (outside any snapshot) to fence index publications.</summary>
    Task<long> ReadPurgeGenerationAsync(CancellationToken cancellationToken);
}

/// <summary>Write side of <c>messages.db</c> used by the erasure workflow.</summary>
public interface IHistoryErasureStore
{
    /// <summary>Runs the single purge transaction (§11 step 2).</summary>
    Task<MessagePurgeOutcome> PurgeAsync(MessagePurgeRequest request, CancellationToken cancellationToken);

    /// <summary>Reads <c>Meta.PendingErasure</c>.</summary>
    Task<IReadOnlyList<ErasureTask>> ReadPendingErasureAsync(CancellationToken cancellationToken);

    /// <summary>Appends a task to <c>Meta.PendingErasure</c>.</summary>
    Task AppendErasureTaskAsync(ErasureTask task, CancellationToken cancellationToken);

    /// <summary>Removes <paramref name="stepName"/> from task <paramref name="taskId"/>; drops the task when no steps remain.</summary>
    Task CompleteErasureStepAsync(string taskId, string stepName, CancellationToken cancellationToken);

    /// <summary>Runs <c>PRAGMA wal_checkpoint(TRUNCATE)</c> on the writer connection.</summary>
    Task<WalCheckpointResult> CheckpointWalAsync(CancellationToken cancellationToken);

    /// <summary>Runs <c>VACUUM</c> on the writer connection and records the time.</summary>
    Task VacuumAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>When <see cref="VacuumAsync"/> last ran, or null.</summary>
    Task<DateTimeOffset?> ReadLastVacuumAsync(CancellationToken cancellationToken);

    /// <summary>Ascending timestamps of the rows a purge of <paramref name="scope"/> would delete (read-only; the purge preview).</summary>
    Task<IReadOnlyList<DateTimeOffset>> ReadPurgeTimestampsAsync(HistoryPurgeScope scope, CancellationToken cancellationToken);
}
