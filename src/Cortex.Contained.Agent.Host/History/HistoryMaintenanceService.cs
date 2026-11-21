using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Maintenance tick (§11 steps 4 and 6): resumes pending erasure steps at startup and every
/// interval, and runs the weekly <c>VACUUM</c> of <c>messages.db</c> and the current index.
/// </summary>
public sealed partial class HistoryMaintenanceService : BackgroundService
{
    private readonly HistoryPurgeService purge;
    private readonly IHistoryErasureStore store;
    private readonly IHistorySource source;
    private readonly IOptionsMonitor<HistoryOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<HistoryMaintenanceService> logger;
    private readonly TimeSpan interval;

    public HistoryMaintenanceService(
        HistoryPurgeService purge,
        IHistoryErasureStore store,
        IHistorySource source,
        IOptionsMonitor<HistoryOptions> options,
        TimeProvider time,
        ILogger<HistoryMaintenanceService> logger)
        : this(purge, store, source, options, time, logger, TimeSpan.FromMinutes(10))
    {
    }

    internal HistoryMaintenanceService(
        HistoryPurgeService purge,
        IHistoryErasureStore store,
        IHistorySource source,
        IOptionsMonitor<HistoryOptions> options,
        TimeProvider time,
        ILogger<HistoryMaintenanceService> logger,
        TimeSpan interval)
    {
        this.purge = purge;
        this.store = store;
        this.source = source;
        this.options = options;
        this.time = time;
        this.logger = logger;
        this.interval = interval;
    }

    /// <summary>One maintenance pass; returns the steps still pending.</summary>
    public async Task<IReadOnlyList<string>> RunOnceAsync(CancellationToken cancellationToken)
    {
        var pending = await this.purge.ResumePendingAsync(cancellationToken).ConfigureAwait(false);
        if (!this.options.CurrentValue.Enabled)
        {
            // Erasure above always resumes; the routine VACUUM only runs for installs that use the history feature.
            // With no projector consuming the journal it would grow without bound, so drop it up to the head;
            // enabling History later forces a rebuild because the checkpoint then lies below JournalPrunedThrough.
            await this.PruneUnconsumedJournalAsync(cancellationToken).ConfigureAwait(false);
            return pending;
        }

        var last = await this.store.ReadLastVacuumAsync(cancellationToken).ConfigureAwait(false);
        var due = last is null || this.time.GetUtcNow() - last.Value >= TimeSpan.FromDays(this.options.CurrentValue.VacuumIntervalDays);
        return due ? await this.purge.RunMaintenanceVacuumAsync(cancellationToken).ConfigureAwait(false) : pending;
    }

    private async Task PruneUnconsumedJournalAsync(CancellationToken cancellationToken)
    {
        long head;
        await using (var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false))
        {
            head = (await snapshot.ReadMetaAsync(cancellationToken).ConfigureAwait(false)).JournalHead;
        }

        await this.source.PruneJournalAsync(head, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pending = await this.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (pending.Count > 0)
                {
                    this.LogStillPending(string.Join(", ", pending));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                this.LogTickFailed(ex.Message);
            }

            try
            {
                await Task.Delay(this.interval, this.time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "History erasure steps still pending: {Steps}")]
    private partial void LogStillPending(string steps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History maintenance tick failed: {Error}")]
    private partial void LogTickFailed(string error);
}
