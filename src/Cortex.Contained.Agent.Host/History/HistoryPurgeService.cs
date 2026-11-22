using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>Outcome of a history deletion (§11 step 4).</summary>
/// <param name="Deleted">Messages deleted.</param>
/// <param name="Inserted">Replacement messages inserted (tenant import).</param>
/// <param name="ErasureComplete">True when every cleanup step finished synchronously.</param>
/// <param name="CleanupPending">Steps that will resume on the next maintenance tick or at startup.</param>
/// <param name="QuarantinedMemories">Curated memories now quarantined for review (D10).</param>
public sealed record HistoryPurgeResult(int Deleted, int Inserted, bool ErasureComplete, IReadOnlyList<string> CleanupPending, int QuarantinedMemories);

/// <summary>
/// Owns every purge side effect (§11): takes the <see cref="HistoryAccessGate"/> write lease,
/// runs the single purge transaction (rows, journal, quarantine windows, pending erasure), then the
/// synchronous cleanup steps. Unfinished steps stay in <c>Meta.PendingErasure</c> and resume via
/// <see cref="ResumePendingAsync"/>. Called by every delete path and the tenant import.
/// </summary>
public sealed partial class HistoryPurgeService
{
    private readonly IHistoryErasureStore store;
    private readonly HistoryAccessGate gate;
    private readonly HistoryProjector projector;
    private readonly Dictionary<string, IErasureStep> steps;
    private readonly MemoryQuarantine? quarantine;
    private readonly IOptionsMonitor<HistoryOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<HistoryPurgeService> logger;

    public HistoryPurgeService(
        IHistoryErasureStore store,
        HistoryAccessGate gate,
        HistoryProjector projector,
        IEnumerable<IErasureStep> steps,
        IOptionsMonitor<HistoryOptions> options,
        TimeProvider time,
        ILogger<HistoryPurgeService> logger,
        MemoryQuarantine? quarantine = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        this.store = store;
        this.gate = gate;
        this.projector = projector;
        this.steps = steps.ToDictionary(s => s.Name, StringComparer.Ordinal);
        this.options = options;
        this.time = time;
        this.logger = logger;
        this.quarantine = quarantine;
    }

    /// <summary>Deletes history in <paramref name="scope"/>; with <paramref name="replacement"/> this is the tenant-import swap.</summary>
    /// <param name="scope">Which rows to delete.</param>
    /// <param name="replacement">Replacement rows inserted in the same transaction, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="quarantineCap">Quarantine windows end no later than this (the moment an import began), or null for the purge time.</param>
    public async Task<HistoryPurgeResult> PurgeAsync(
        HistoryPurgeScope scope,
        IReadOnlyList<MessageRecord>? replacement,
        CancellationToken cancellationToken,
        DateTimeOffset? quarantineCap = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var settings = this.options.CurrentValue;
        MessagePurgeOutcome outcome;
        IReadOnlyList<string> pending;

        await using (await this.gate.AcquireWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            var request = new MessagePurgeRequest(scope)
            {
                Now = this.time.GetUtcNow(),
                QuarantineSlack = TimeSpan.FromHours(settings.QuarantineExtractionSlackHours),
                QuarantineClusterGap = TimeSpan.FromHours(settings.QuarantineClusterGapHours),
                PendingSteps = ErasureSteps.ForPurge(scope),
                Replacement = replacement,
                QuarantineCap = quarantineCap,
            };
            outcome = await this.store.PurgeAsync(request, cancellationToken).ConfigureAwait(false);
            pending = await this.RunPendingLockedAsync(request.ErasureId, cancellationToken).ConfigureAwait(false);
        }

        var quarantined = this.quarantine is null
            ? 0
            : await this.quarantine.CountInWindowsAsync(outcome.Windows, cancellationToken).ConfigureAwait(false);
        this.LogPurged(scope.ChannelId ?? "(all)", outcome.Deleted, pending.Count == 0, quarantined);
        return new HistoryPurgeResult(outcome.Deleted, outcome.Inserted, pending.Count == 0, pending, quarantined);
    }

    /// <summary>
    /// Counts what <see cref="PurgeAsync"/> would delete for <paramref name="scope"/> now: the messages and the
    /// not-kept curated memories its quarantine windows would cover (the dependent memories). Deletes nothing.
    /// </summary>
    public async Task<HistoryPurgePreview> PreviewAsync(HistoryPurgeScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var settings = this.options.CurrentValue;
        var timestamps = await this.store.ReadPurgeTimestampsAsync(scope, cancellationToken).ConfigureAwait(false);
        var windows = QuarantineWindowBuilder.Build(
            timestamps,
            TimeSpan.FromHours(settings.QuarantineClusterGapHours),
            TimeSpan.FromHours(settings.QuarantineExtractionSlackHours),
            this.time.GetUtcNow());
        var dependent = this.quarantine is null ? 0 : await this.quarantine.CountInWindowsAsync(windows, cancellationToken).ConfigureAwait(false);
        return new HistoryPurgePreview(
            timestamps.Count,
            dependent,
            timestamps.Count > 0 ? timestamps[0] : null,
            timestamps.Count > 0 ? timestamps[^1] : null);
    }

    /// <summary>Resumes every pending erasure step (maintenance tick and startup). Returns what is still pending.</summary>
    public async Task<IReadOnlyList<string>> ResumePendingAsync(CancellationToken cancellationToken)
    {
        await using (await this.gate.AcquireWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            return await this.RunPendingLockedAsync(onlyTaskId: null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Records and runs the weekly maintenance <c>VACUUM</c> (§11 step 6).</summary>
    public async Task<IReadOnlyList<string>> RunMaintenanceVacuumAsync(CancellationToken cancellationToken)
    {
        await this.store.AppendErasureTaskAsync(
            new ErasureTask
            {
                Id = VacuumErasureStep.MaintenanceTaskPrefix + Guid.NewGuid().ToString("N"),
                CreatedAt = this.time.GetUtcNow(),
                Steps = [ErasureSteps.Vacuum],
            },
            cancellationToken).ConfigureAwait(false);
        return await this.ResumePendingAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> RunPendingLockedAsync(string? onlyTaskId, CancellationToken cancellationToken)
    {
        var stillPending = new List<string>();
        foreach (var task in await this.store.ReadPendingErasureAsync(cancellationToken).ConfigureAwait(false))
        {
            if (onlyTaskId is not null && !string.Equals(task.Id, onlyTaskId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var step in task.Steps)
            {
                bool done;
                try
                {
                    done = await this.RunStepAsync(task, step, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.LogStepFailed(step, task.Id, ex.Message);
                    done = false;
                }

                if (done)
                {
                    await this.store.CompleteErasureStepAsync(task.Id, step, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    stillPending.Add(step);
                }
            }
        }

        return stillPending;
    }

    private Task<bool> RunStepAsync(ErasureTask task, string step, CancellationToken cancellationToken)
    {
        if (step.StartsWith(ErasureSteps.RetiredGenerationPrefix, StringComparison.Ordinal)
            && long.TryParse(step.AsSpan(ErasureSteps.RetiredGenerationPrefix.Length), System.Globalization.CultureInfo.InvariantCulture, out var generation))
        {
            return this.projector.DeleteRetiredAsync(generation);
        }

        return this.steps.TryGetValue(step, out var handler)
            ? handler.RunAsync(task, cancellationToken)
            : Task.FromResult(true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "History purge of {Scope}: {Deleted} messages deleted, erasure complete={Complete}, {Quarantined} memories quarantined")]
    private partial void LogPurged(string scope, int deleted, bool complete, int quarantined);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Erasure step {Step} of task {TaskId} failed and stays pending: {Error}")]
    private partial void LogStepFailed(string step, string taskId, string error);
}
