using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>One backfill batch.</summary>
/// <param name="LastId">Highest <c>Messages.Id</c> scanned (the next batch starts after it).</param>
/// <param name="Scanned">Rows scanned.</param>
/// <param name="Inserted">Rows that received a <c>legacy_import</c> event.</param>
public sealed record EventBackfillBatch(long LastId, int Scanned, int Inserted);

/// <summary>Outcome of a whole backfill run.</summary>
public sealed record EventBackfillReport(int Scanned, int Inserted, TimeSpan Elapsed);

/// <summary>Whether a backfill is running and how the last one (since the agent started) ended.</summary>
public sealed record EventBackfillStatus(bool Running, EventBackfillReport? Last, DateTimeOffset? LastRunAt);

/// <summary>Writer-side backfill step, implemented by <c>MessageStore</c>.</summary>
public interface IEventBackfillStore
{
    /// <summary>
    /// In one writer transaction: gives every row with <c>Id &gt; afterId</c> (at most
    /// <paramref name="batchSize"/>) that has no event a <c>legacy_import</c> event.
    /// </summary>
    Task<EventBackfillBatch> BackfillLegacyEventsAsync(long afterId, int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// The explicit event backfill (D2): <c>legacy_import</c> events for rows stored before the event
/// log existed. Runs in the maintenance window — the <see cref="HistoryAccessGate"/> write lease
/// pauses recall and purges — in bounded batches. Idempotent (rows that already have an event are
/// skipped; the idempotency key is <c>legacy:{Messages.Id}</c>) and it never resurrects purged rows,
/// because it reads only rows that exist inside the same transaction that writes their events.
/// </summary>
public sealed partial class EventBackfillService
{
    /// <summary>Rows per writer transaction.</summary>
    public const int BatchSize = 1000;

    private readonly IEventBackfillStore store;
    private readonly HistoryAccessGate gate;
    private readonly TimeProvider time;
    private readonly ILogger<EventBackfillService> logger;
    private int running;
    private volatile EventBackfillStatus last = new(false, null, null);

    public EventBackfillService(IEventBackfillStore store, HistoryAccessGate gate, TimeProvider time, ILogger<EventBackfillService> logger)
    {
        this.store = store;
        this.gate = gate;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>The current state (for the Recall status page).</summary>
    public EventBackfillStatus Status => this.last with { Running = Volatile.Read(ref this.running) > 0 };

    /// <summary>Runs the backfill to the end of the table.</summary>
    public async Task<EventBackfillReport> RunAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.running);
        try
        {
            var report = await this.RunCoreAsync(cancellationToken).ConfigureAwait(false);
            this.last = new EventBackfillStatus(false, report, this.time.GetUtcNow());
            return report;
        }
        finally
        {
            Interlocked.Decrement(ref this.running);
        }
    }

    private async Task<EventBackfillReport> RunCoreAsync(CancellationToken cancellationToken)
    {
        var started = this.time.GetTimestamp();
        var scanned = 0;
        var inserted = 0;
        await using (await this.gate.AcquireWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            long after = 0;
            while (true)
            {
                var batch = await this.store.BackfillLegacyEventsAsync(after, BatchSize, cancellationToken).ConfigureAwait(false);
                if (batch.Scanned == 0)
                {
                    break;
                }

                scanned += batch.Scanned;
                inserted += batch.Inserted;
                after = batch.LastId;
            }
        }

        var report = new EventBackfillReport(scanned, inserted, this.time.GetElapsedTime(started));
        this.LogBackfilled(report.Scanned, report.Inserted, (long)report.Elapsed.TotalMilliseconds);
        return report;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Event backfill scanned {Scanned} messages and wrote {Inserted} legacy_import events in {ElapsedMs} ms")]
    private partial void LogBackfilled(int scanned, int inserted, long elapsedMs);
}
