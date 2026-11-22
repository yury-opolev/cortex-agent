using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Recall;

/// <summary>The Bridge-wide reranker switch as the Recall page shows it.</summary>
/// <param name="Enabled"><c>Reranker:Enabled</c> in the Bridge configuration (shared by every tenant).</param>
/// <param name="Sidecar">The sidecar state after the last reconcile.</param>
public sealed record RerankerSwitchView(bool Enabled, RerankerSidecarStatus Sidecar);

/// <summary>
/// Owns <c>Reranker:Enabled</c>: persists the switch to <c>cortex.yml</c>, then converges the sidecar and the
/// agents' grants in a safe order — on enable the sidecar starts first and the grant follows (so agents do not
/// trip their circuit breaker on a container that is still loading); on disable the grant is withdrawn first
/// and the sidecar stops afterwards.
/// </summary>
public sealed partial class RerankerSwitch : IDisposable
{
    private readonly BridgeConfig config;
    private readonly Action<BridgeConfig> persist;
    private readonly RerankerSidecarLifecycle lifecycle;
    private readonly Func<CancellationToken, Task> pushGrant;
    private readonly ILogger<RerankerSwitch> logger;
    private readonly SemaphoreSlim converge = new(1, 1);

    public RerankerSwitch(
        BridgeConfig config,
        Action<BridgeConfig> persist,
        RerankerSidecarLifecycle lifecycle,
        Func<CancellationToken, Task> pushGrant,
        ILogger<RerankerSwitch> logger)
    {
        this.config = config;
        this.persist = persist;
        this.lifecycle = lifecycle;
        this.pushGrant = pushGrant;
        this.logger = logger;
    }

    /// <summary>The switch and the sidecar state.</summary>
    public RerankerSwitchView Current => new(this.config.Reranker.Enabled, this.lifecycle.Status);

    /// <summary>Persists <paramref name="enabled"/> (synchronously, so a failure surfaces to the caller).</summary>
    public void Set(bool enabled)
    {
        this.config.Reranker.Enabled = enabled;
        this.persist(this.config);
    }

    /// <summary>Converges the sidecar and every agent's grant with the persisted switch. Never throws.</summary>
    public async Task ConvergeAsync(CancellationToken cancellationToken)
    {
        await this.converge.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var enabled = this.config.Reranker.Enabled;
            if (enabled)
            {
                await this.lifecycle.ReconcileAsync(true, cancellationToken).ConfigureAwait(false);
                await this.pushGrant(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await this.pushGrant(cancellationToken).ConfigureAwait(false);
                await this.lifecycle.ReconcileAsync(false, cancellationToken).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Converging runs in the background; failures are logged and shown as sidecar state
        catch (Exception ex)
        {
            this.LogConvergeFailed(ex.Message);
        }
#pragma warning restore CA1031
        finally
        {
            this.converge.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => this.converge.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reranker switch could not converge: {Error}")]
    private partial void LogConvergeFailed(string error);
}
