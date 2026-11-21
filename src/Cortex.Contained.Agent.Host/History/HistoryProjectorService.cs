using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Hosted loop around <see cref="HistoryProjector"/> (§9.5): initialize, rebuild when needed, then
/// apply the journal about once a second. Three consecutive failures halt the projector with a
/// health warning; it never skips a batch. Recall keeps working through verification and the
/// unindexed tail while halted.
/// </summary>
public sealed partial class HistoryProjectorService : BackgroundService
{
    private const int MaxConsecutiveFailures = 3;

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
        var settings = this.options.CurrentValue;
        if (!settings.Enabled)
        {
            this.LogDisabled();
            return;
        }

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
            return;
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
                return;
            }
            catch (Exception ex) when (ex is Storage.StorageKeyMismatchException or Storage.StorageLockedException)
            {
                this.status.Update(s => s with { Halted = true, LastError = ex.Message });
                this.LogHalted(ex.Message);
                return;
            }
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
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

            try
            {
                await Task.Delay(this.pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "History locator disabled (History:Enabled=false); projector not started")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Warning, Message = "History projector batch failed (attempt {Attempt}): {Error}")]
    private partial void LogBatchFailed(int attempt, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "History projector halted: {Error}")]
    private partial void LogHalted(string error);
}
