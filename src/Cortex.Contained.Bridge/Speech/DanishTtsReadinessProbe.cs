using Cortex.Contained.Speech.Tts;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Periodically refreshes <see cref="RoestDanishTtsProvider"/> readiness by
/// polling the sidecar's /health, so CompositeTtsEngine's live IsReady check
/// starts routing Danish text as soon as the on-demand container is up (and
/// stops if it goes away). Cheap: a down sidecar fails fast (connection refused)
/// and CheckReadyAsync swallows the error → IsReady stays false.
/// </summary>
public sealed partial class DanishTtsReadinessProbe : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(10);

    private readonly RoestDanishTtsProvider provider;
    private readonly ILogger<DanishTtsReadinessProbe> logger;

    public DanishTtsReadinessProbe(RoestDanishTtsProvider provider, ILogger<DanishTtsReadinessProbe> logger)
    {
        this.provider = provider;
        this.logger = logger;
    }

    /// <summary>One readiness poll. Extracted for testability. Never throws.</summary>
    internal async Task ProbeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.provider.CheckReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Defense-in-depth: a probe failure must not crash the loop.
        catch (Exception ex)
        {
            this.LogProbeError(ex.Message);
        }
#pragma warning restore CA1031
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ProbeInterval);

        // Probe once immediately, then on each tick.
        await this.ProbeOnceAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await this.ProbeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Danish TTS readiness probe error: {Error}")]
    private partial void LogProbeError(string error);
}
