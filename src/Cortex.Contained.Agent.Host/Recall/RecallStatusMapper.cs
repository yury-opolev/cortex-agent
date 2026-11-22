using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;

namespace Cortex.Contained.Agent.Host.Recall;

/// <summary>Maps effective options and subsystem snapshots to the Recall DTOs (pure, no I/O).</summary>
public static class RecallStatusMapper
{
    /// <summary>The effective settings (options already carry the overrides through the post-configures).</summary>
    public static RecallSettingsDto Settings(HistoryOptions history, RerankerOptions reranker, MemorySearchOptions memorySearch, RecallOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(memorySearch);
        ArgumentNullException.ThrowIfNull(overrides);
        return new RecallSettingsDto
        {
            HistoryEnabled = history.Enabled,
            HistorySearchMode = RecallSettingsValidation.Text(history.DefaultSearchMode),
            HistoryDenseEnabled = history.Dense.Enabled,
            MemorySearchMode = RecallSettingsValidation.Text(memorySearch.DefaultMode),
            RerankHistoryTopN = reranker.History.TopN,
            RerankHistoryMaxDocChars = reranker.History.MaxDocChars,
            RerankMemoryTopN = reranker.Memory.TopN,
            RerankMemoryMaxDocChars = reranker.Memory.MaxDocChars,
            RerankTimeoutMs = reranker.TimeoutMs,
            Overridden = overrides.Names(),
        };
    }

    /// <summary>The Recall status page payload.</summary>
    public static RecallStatusDto Status(RecallSettingsDto settings, HistoryHealth history, RerankHealth reranker, EventBackfillStatus? backfill, int quarantinedMemories)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(reranker);
        var dense = history.Dense;
        return new RecallStatusDto
        {
            Settings = settings,
            History = new HistoryIndexStatusDto
            {
                Enabled = history.Enabled,
                ToolsEnabled = history.ToolsEnabled,
                Storage = history.Storage,
                Generation = history.Generation,
                BuildComplete = history.BuildComplete,
                BuildInProgress = history.BuildInProgress,
                CheckpointLag = history.CheckpointLag,
                JournalBacklog = history.JournalBacklog,
                Halted = history.Halted,
                LastError = history.LastError,
                PendingErasureTasks = history.PendingErasureTasks,
                PendingErasureSteps = history.PendingErasureSteps,
                MaintenancePendingTasks = history.MaintenancePendingTasks,
            },
            Dense = new DenseEmbeddingStatusDto
            {
                State = dense.State,
                Embedded = dense.Embedded,
                Total = dense.Total,
                Pending = dense.Pending,
                Failed = dense.Failed,
                OldestPendingQueuedAt = dense.OldestPendingQueuedAt,
                LastError = dense.LastError,
            },
            Reranker = new RerankerStatusDto
            {
                Enabled = reranker.Enabled,
                State = reranker.State,
                ConsecutiveFailures = reranker.ConsecutiveFailures,
                CircuitOpenUntil = reranker.CircuitOpenUntil,
                LastError = reranker.LastError,
                LastLatencyMs = reranker.LastLatencyMs,
                LastCallAt = reranker.LastCallAt,
            },
            EventBackfill = new EventBackfillStatusDto
            {
                Running = backfill?.Running ?? false,
                LastRunAt = backfill?.LastRunAt,
                LastScanned = backfill?.Last?.Scanned,
                LastInserted = backfill?.Last?.Inserted,
                LastElapsedMs = backfill?.Last is { } last ? (long)last.Elapsed.TotalMilliseconds : null,
            },
            QuarantinedMemories = quarantinedMemories,
        };
    }
}
