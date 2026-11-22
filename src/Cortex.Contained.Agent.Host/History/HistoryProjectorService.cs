using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Hosted loop around <see cref="HistoryProjector"/> (§9.5): initialize, rebuild when needed, then
/// apply the journal about once a second. Three consecutive failures halt the projector with a
/// health warning; it never skips a batch. Recall keeps working through verification and the
/// unindexed tail while halted.
/// <para>
/// With dense retrieval on, each tick then runs embedding rounds. An embedder failure never halts the
/// projector: it is reported as <see cref="HistoryStatusSnapshot.LastEmbeddingError"/> and embedding
/// pauses for <see cref="EmbeddingBackoffTicks"/> ticks while keyword search keeps working.
/// </para>
/// </summary>
public sealed partial class HistoryProjectorService : BackgroundService
{
    private const int MaxConsecutiveFailures = 3;
   private const int MaxEmbeddingRoundsPerTick = 8;

   /// <summary>Ticks skipped after an embedder failure.</summary>
   internal const int EmbeddingBackoffTicks = 30;
    private readonly HistoryProjector projector;
    private readonly HistoryStatus status;
    private readonly IOptionsMonitor<HistoryOptions> options;
    private readonly ILogger<HistoryProjectorService> logger;
    private readonly TimeSpan pollInterval;
    private readonly Storage.IStorageState? storage;

    public HistoryProjectorService(
        HistoryProjector projector,
        HistoryStatus status,
        IOptionsMonitor<HistoryOptions> options,
        ILogger<HistoryProjectorService> logger,
        Storage.IStorageState? storage = null)
        : this(projector, status, options, logger, TimeSpan.FromSeconds(1), storage)
    {
    }

    internal HistoryProjectorService(
        HistoryProjector projector,
        HistoryStatus status,
        IOptionsMonitor<HistoryOptions> options,
        ILogger<HistoryProjectorService> logger,
        TimeSpan pollInterval,
        Storage.IStorageState? storage = null)
    {
        this.storage = storage;
        this.projector = projector;
        this.status = status;
        this.options = options;
        this.logger = logger;
        this.pollInterval = pollInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // History:Enabled is read every tick, so the Recall settings page can switch it at runtime:
        // switched on, the startup checks run and the projector resumes or rebuilds; switched off, the
        // loop idles (recall tools are hidden by HistoryToolsSwitch; the index stays until the next erasure
        // pass deletes it, and maintenance prunes the unconsumed journal, which forces a rebuild on re-enable).
        var running = false;
        var reportedDisabled = false;
        var failures = 0;
        var embeddingBackoff = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = this.options.CurrentValue;
            if (!settings.Enabled)
            {
                if (running)
                {
                    this.LogPaused();
                    running = false;
                }
                else if (!reportedDisabled)
                {
                    this.LogDisabled();
                }

                reportedDisabled = true;
                if (!await this.DelayAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            if (!running)
            {
                var start = await this.TryStartAsync(settings, stoppingToken).ConfigureAwait(false);
                if (start != StartOutcome.Started)
                {
                    return;
                }

                running = true;
                failures = 0;
                embeddingBackoff = 0;
            }

            try
            {
                if (!this.projector.IsInitialized)
                {
                    await this.projector.InitializeAsync(stoppingToken).ConfigureAwait(false);
                }

                if (this.projector.NeedsRebuild)
                {
                    await this.projector.RebuildAsync(stoppingToken).ConfigureAwait(false);
                }
                else if (await this.projector.ApplyPendingAsync(stoppingToken).ConfigureAwait(false) == ProjectorApplyResult.RebuildRequired)
                {
                    await this.projector.RetireCurrentAsync(writeLeaseHeld: false, stoppingToken).ConfigureAwait(false);
                }

                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                this.status.Update(s => s with { LastError = ex.Message });
                this.LogBatchFailed(failures, ex.Message);
                if (failures >= MaxConsecutiveFailures)
                {
                    this.status.Update(s => s with { Halted = true });
                    this.LogHalted(ex.Message);
                    return;
                }
            }

            if (failures == 0 && !this.projector.NeedsRebuild)
            {
                embeddingBackoff = await this.EmbedAsync(embeddingBackoff, stoppingToken).ConfigureAwait(false);
            }

            if (!await this.DelayAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<StartOutcome> TryStartAsync(HistoryOptions settings, CancellationToken stoppingToken)
    {
        var problems = settings.Validate().ToList();
        var capabilities = SqliteRuntimeInfo.Capabilities;
        if (!capabilities.SupportsHistoryIndex)
        {
            problems.Add($"SQLite {capabilities.Version} cannot host the history index: {capabilities.Error}");
        }

        if (problems.Count > 0)
        {
            var message = string.Join(" ", problems);
            this.status.Update(s => s with { Halted = true, LastError = message });
            this.LogHalted(message);
            return StartOutcome.Halted;
        }

        if (this.storage is not null)
        {
            // Encrypted storage opens only once the Bridge pushed the key; wait rather than burn retries.
            try
            {
                await this.storage.WhenOpenAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return StartOutcome.Stopped;
            }
            catch (Exception ex) when (ex is Storage.StorageKeyMismatchException or Storage.StorageLockedException)
            {
                this.status.Update(s => s with { Halted = true, LastError = ex.Message });
                this.LogHalted(ex.Message);
                return StartOutcome.Halted;
            }
        }

        this.LogStarted();
        return StartOutcome.Started;
    }

    private async Task<bool> DelayAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(this.pollInterval, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<int> EmbedAsync(int backoff, CancellationToken stoppingToken)
    {
        if (backoff > 0)
        {
            return backoff - 1;
        }

        try
        {
            for (var round = 0; round < MaxEmbeddingRoundsPerTick; round++)
            {
                var result = await this.projector.EmbedPendingAsync(stoppingToken).ConfigureAwait(false);
                this.status.Update(s => s with { LastEmbeddingError = null });
                if (result.Published == 0)
                {
                    break;
                }
            }

            return 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            this.status.Update(s => s with { LastEmbeddingError = ex.Message });
            this.LogEmbeddingFailed(ex.Message);
            return EmbeddingBackoffTicks;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "History locator disabled (History:Enabled=false); projector idle until it is enabled")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "History locator enabled; projector started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "History locator switched off at runtime; projector paused")]
    private partial void LogPaused();

    [LoggerMessage(Level = LogLevel.Warning, Message = "History projector batch failed (attempt {Attempt}): {Error}")]
    private partial void LogBatchFailed(int attempt, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "History projector halted: {Error}")]
    private partial void LogHalted(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History embedding failed; keyword search continues and embedding retries later: {Error}")]
    private partial void LogEmbeddingFailed(string error);

    private enum StartOutcome
    {
        Started,
        Halted,
        Stopped,
    }
}
