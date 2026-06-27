using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the unauthenticated <c>/health</c> probe endpoint.
/// </summary>
internal static class HealthEndpoints
{
    /// <summary>Maps the <c>/health</c> endpoint onto <paramref name="app"/>.</summary>
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // --- Health endpoint (unauthenticated) ---
        app.MapGet("/health", async (TenantRouter tenantRouter, CancellationToken cancellationToken) =>
        {
            var client = tenantRouter.GetDefaultClient();
            var connected = client?.IsConnected == true;
            var version = typeof(Worker).Assembly.GetName().Version?.ToString() ?? "0.0.0";

            // When the agent is reachable, pull its live operational metrics through the
            // Ping path so /health can surface queue pressure and token-refresh health.
            // Metrics stays null if the probe fails — the endpoint must never throw.
            AgentMetricsSnapshot? metrics = null;
            if (connected && client is not null)
            {
                try
                {
                    // Bound the agent round-trip: a wedged-but-connected agent must degrade
                    // the probe to Metrics = null, never hang it (PingAsync itself has no
                    // timeout — same linked-CTS pattern as TenantHealthService).
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
                    var agentHealth = await client.PingAsync(timeoutCts.Token).ConfigureAwait(false);
                    metrics = agentHealth.Metrics;
                }
#pragma warning disable CA1031 // The health probe must never fail because the agent ping did.
                catch
                {
                    metrics = null;
                }
#pragma warning restore CA1031
            }

            return Results.Ok(new HealthInfo
            {
                Healthy = connected,
                Timestamp = DateTimeOffset.UtcNow,
                Version = version,
                Metrics = metrics,
            });
        });
    }
}
