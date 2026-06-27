using Cortex.Contained.Agent.Host.Agent;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Periodic background service that scans all memories for near-duplicates
/// and merges them using the <see cref="MemoryConsolidationService"/>.
/// Runs at a configurable time of day (default: every 24 hours). On each run it:
/// <list type="number">
/// <item>Lists all memories via <see cref="MemoryManagementService"/>.</item>
/// <item>For each memory, searches for similar memories above a merge threshold.</item>
/// <item>Groups near-duplicate clusters and merges them via LLM consolidation.</item>
/// </list>
/// </summary>
public sealed partial class MemoryCompactionService : BackgroundService
{
    private readonly IMemoryManagementService management;
    private readonly IMemoryService memoryService;
    private readonly MemoryConsolidationService consolidation;
    private readonly MemoryExtractionService extraction;
    private readonly IModelProvider modelProvider;
    private readonly MaintenanceStore maintenanceStore;
    private readonly MemorySettingsStore memorySettingsStore;
    private readonly MemoryCompactionOptions options;
    private readonly ILogger<MemoryCompactionService> logger;

    /// <summary>Task name used for maintenance store tracking.</summary>
    internal const string TaskName = "memory-compaction";

    /// <summary>Fallback delay before the first compaction run if no preferred time is set.</summary>
    private static readonly TimeSpan FallbackInitialDelay = TimeSpan.FromMinutes(5);

    /// <summary>Number of similar memories to check per memory.</summary>
    private const int CompactionSearchLimit = 10;

    /// <summary>Page size when iterating through all memories.</summary>
    private const int PageSize = 50;

    public MemoryCompactionService(
        IMemoryManagementService management,
        IMemoryService memoryService,
        MemoryConsolidationService consolidation,
        MemoryExtractionService extraction,
        IModelProvider modelProvider,
        MaintenanceStore maintenanceStore,
        MemorySettingsStore memorySettingsStore,
        IOptions<MemoryCompactionOptions> options,
        ILogger<MemoryCompactionService> logger)
    {
        this.management = management;
        this.memoryService = memoryService;
        this.consolidation = consolidation;
        this.extraction = extraction;
        this.modelProvider = modelProvider;
        this.maintenanceStore = maintenanceStore;
        this.memorySettingsStore = memorySettingsStore;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!this.options.Enabled)
        {
            this.LogCompactionDisabled();
            return;
        }

        var interval = TimeSpan.FromHours(this.options.IntervalHours);
        this.LogCompactionServiceStarted(interval, this.options.PreferredTimeOfDay ?? "(not set)");

        // On startup, check if compaction is overdue and run a single catch-up if needed.
        await RunCatchUpAsync(stoppingToken).ConfigureAwait(false);

        var initialDelay = this.options.PreferredTimeOfDay is not null
            ? NightlyTaskScheduler.CalculateDelayToNextTime(this.options.PreferredTimeOfDay, DateTimeOffset.Now)
            : FallbackInitialDelay;
        this.LogCompactionNextRun(initialDelay);
        await Task.Delay(initialDelay, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitForExtractionIdleAsync(stoppingToken).ConfigureAwait(false);

                var today = DateOnly.FromDateTime(DateTime.Now);
                await RunTrackedCompactionAsync(today, stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Compaction loop must not crash
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                this.LogCompactionFailed(ex.Message);
            }
#pragma warning restore CA1031

            // Wait for next run
            var nextDelay = this.options.PreferredTimeOfDay is not null
                ? NightlyTaskScheduler.CalculateDelayToNextTime(this.options.PreferredTimeOfDay, DateTimeOffset.Now)
                : interval;
            this.LogCompactionNextRun(nextDelay);
            await Task.Delay(nextDelay, stoppingToken).ConfigureAwait(false);
        }

        this.LogCompactionServiceStopped();
    }

    /// <summary>
    /// Checks the maintenance store and runs a single compaction if overdue.
    /// Compaction doesn't need per-day catch-up — a single run deduplicates the
    /// entire memory store regardless of how many days were missed.
    /// </summary>
    private async Task RunCatchUpAsync(CancellationToken stoppingToken)
    {
        var lastCompleted = this.maintenanceStore.GetLastCompletedDate(TaskName);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var missedDates = NightlyTaskScheduler.GetMissedDates(lastCompleted, today);

        if (missedDates.Count == 0)
        {
            return;
        }

        this.LogCatchUpStarting(missedDates.Count);

        // Wait a short period after startup to let the system stabilize
        await Task.Delay(NightlyTaskScheduler.CatchUpStartupDelay, stoppingToken).ConfigureAwait(false);

        try
        {
            await WaitForExtractionIdleAsync(stoppingToken).ConfigureAwait(false);
            await RunTrackedCompactionAsync(today, stoppingToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Catch-up must not crash
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.LogCompactionFailed(ex.Message);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Runs compaction and records the run in the maintenance store.
    /// </summary>
    private async Task RunTrackedCompactionAsync(DateOnly targetDate, CancellationToken cancellationToken)
    {
        var runId = this.maintenanceStore.RecordRunStart(TaskName, targetDate);

        try
        {
            await RunCompactionAsync(cancellationToken).ConfigureAwait(false);
            this.maintenanceStore.RecordRunSuccess(runId, TaskName, targetDate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.maintenanceStore.RecordRunFailure(runId, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Runs a compaction sweep on demand. Waits for extraction to be idle first.
    /// Returns the number of memories checked and merged.
    /// </summary>
    public async Task<(int Checked, int Merged)> RunOnDemandAsync(CancellationToken cancellationToken)
    {
        await WaitForExtractionIdleAsync(cancellationToken).ConfigureAwait(false);
        return await RunCompactionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the extraction service to be idle before running compaction.
    /// </summary>
    private async Task WaitForExtractionIdleAsync(CancellationToken stoppingToken)
    {
        await this.extraction.WaitForIdleAsync(
            TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a single compaction sweep over all memories.
    /// </summary>
    private async Task<(int Checked, int Merged)> RunCompactionAsync(CancellationToken cancellationToken)
    {
        if (!this.memorySettingsStore.IsMemoryEnabled)
        {
            // Built-in memory disabled — skip the sweep.
            return (0, 0);
        }

        this.LogCompactionStarted();

        // Track which memory IDs have already been processed (merged or checked)
        // to avoid processing them again as part of a different cluster.
        var processedIds = new HashSet<string>(StringComparer.Ordinal);
        var totalMerged = 0;
        var totalChecked = 0;

        // Iterate through all memories in pages
        var offset = 0;
        while (true)
        {
            var page = await this.management.ListAsync(PageSize, offset, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var memory in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (processedIds.Contains(memory.MemoryId))
                {
                    continue;
                }

                processedIds.Add(memory.MemoryId);
                totalChecked++;

                // Search for similar memories
                var similar = await this.memoryService.SearchAsync(
                    memory.Content,
                    CompactionSearchLimit,
                    this.options.SimilarityThreshold,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                // Filter out the memory itself and already-processed memories
                var duplicates = similar
                    .Where(s => s.MemoryId != memory.MemoryId && !processedIds.Contains(s.MemoryId))
                    .ToList();

                if (duplicates.Count == 0)
                {
                    continue;
                }

                this.LogDuplicateClusterFound(memory.MemoryId, memory.Title ?? "(untitled)", duplicates.Count);

                // For each duplicate, use consolidation to merge it into the original memory.
                // We treat the duplicate's content as a "new fact" and the original memory
                // will be found by the consolidation search, leading to an UPDATE.
                foreach (var duplicate in duplicates)
                {
                    processedIds.Add(duplicate.MemoryId);

                    var fact = new MemoryConsolidationService.ConsolidationFact
                    {
                        Content = duplicate.Content,
                        Title = duplicate.Title,
                        Tags = duplicate.Tags.Count > 0 ? duplicate.Tags : null,
                    };

                    await this.consolidation.AcquireConsolidationLockAsync(cancellationToken).ConfigureAwait(false);
                    MemoryConsolidationService.ConsolidationResult result;
                    try
                    {
                        result = await this.consolidation.ConsolidateAsync(
                            fact, this.modelProvider.MemoryModel, "compaction", batchMemories: null, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        this.consolidation.ReleaseConsolidationLock();
                    }

                    if (result.Action == MemoryConsolidationService.ConsolidationAction.Updated
                        || result.Action == MemoryConsolidationService.ConsolidationAction.Noop)
                    {
                        // The consolidation merged or recognized the duplicate.
                        // If it was NOOP (already covered), delete the duplicate.
                        if (result.Action == MemoryConsolidationService.ConsolidationAction.Noop)
                        {
                            await this.memoryService.DeleteAsync(duplicate.MemoryId, cancellationToken).ConfigureAwait(false);
                            this.LogCompactionDeleted(duplicate.MemoryId, "already covered by existing memory");
                        }

                        totalMerged++;
                    }

                    this.LogCompactionResult(duplicate.MemoryId, result.Action, result.Reason ?? "");
                }
            }

            offset += page.Items.Count;

            // If we've processed all memories, stop
            if (offset >= page.TotalCount)
            {
                break;
            }
        }

        this.LogCompactionComplete(totalChecked, totalMerged);
        return (totalChecked, totalMerged);
    }

    // ── LoggerMessage source-generated methods ──────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory compaction service started (interval={Interval}, preferredTime={PreferredTime})")]
    private partial void LogCompactionServiceStarted(TimeSpan interval, string preferredTime);

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory compaction service disabled via configuration")]
    private partial void LogCompactionDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory compaction service stopped")]
    private partial void LogCompactionServiceStopped();

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory compaction sweep started")]
    private partial void LogCompactionStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Next compaction run in {Delay}")]
    private partial void LogCompactionNextRun(TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory compaction complete: checked {Checked} memories, merged/removed {Merged} duplicates")]
    private partial void LogCompactionComplete(int @checked, int merged);

    [LoggerMessage(Level = LogLevel.Error, Message = "Memory compaction sweep failed: {ErrorMessage}")]
    private partial void LogCompactionFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory compaction catch-up: {Count} missed day(s) detected, running single compaction")]
    private partial void LogCatchUpStarting(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate cluster found: memory {MemoryId} \"{Title}\" has {DuplicateCount} similar memories")]
    private partial void LogDuplicateClusterFound(string memoryId, string title, int duplicateCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Compaction result for {MemoryId}: action={Action}, reason={Reason}")]
    private partial void LogCompactionResult(string memoryId, MemoryConsolidationService.ConsolidationAction action, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compaction deleted memory {MemoryId}: {Reason}")]
    private partial void LogCompactionDeleted(string memoryId, string reason);
}
