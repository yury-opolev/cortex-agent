using Cortex.Contained.Bridge.Mcp.Actions;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the unauthenticated <c>/health</c> probe endpoint.
/// </summary>
internal static partial class HealthEndpoints
{
    /// <summary>Bound applied to the MCP action aggregate query so /health stays cheap.</summary>
    private const int McpActionAggregateLimit = 1000;

    /// <summary>Maps the <c>/health</c> endpoint onto <paramref name="app"/>.</summary>
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // --- Health endpoint (unauthenticated) ---
        app.MapGet("/health", async (
            TenantRouter tenantRouter,
            IMcpActionStore mcpActionStore,
            TenantRegistry tenants,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
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

            // Aggregate, content-free MCP action counts for the default tenant. A failure here
            // (store unavailable, query error) degrades to null + a warning — it must NEVER
            // make the Bridge itself report unhealthy, since Healthy reflects agent
            // connectivity only.
            var healthLogger = loggerFactory.CreateLogger("Cortex.Contained.Bridge.HealthEndpoint");
            var defaultTenantId = tenants.GetDefaultTenant()?.Id;
            var mcpActions = await TryBuildMcpActionAggregateAsync(
                mcpActionStore, defaultTenantId, healthLogger, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new HealthInfo
            {
                Healthy = connected,
                Timestamp = DateTimeOffset.UtcNow,
                Version = version,
                Metrics = metrics,
                McpActions = mcpActions,
            });
        });
    }

    /// <summary>
    /// Computes the MCP action aggregate for <paramref name="tenantId"/>. Returns null (and logs
    /// a warning) on ANY failure or when no tenant is available — this probe must never throw or
    /// propagate into the /health response's <c>Healthy</c> flag.
    /// </summary>
    internal static async Task<McpActionAggregateSnapshot?> TryBuildMcpActionAggregateAsync(
        IMcpActionStore store, string? tenantId, ILogger logger, CancellationToken cancellationToken)
    {
        if (tenantId is null)
        {
            return null;
        }

        try
        {
            var actions = await store.ListAsync(
                new McpActionQuery { TenantId = tenantId, Limit = McpActionAggregateLimit },
                cancellationToken).ConfigureAwait(false);

            var countsByState = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var action in actions)
            {
                var key = McpActionWireStatus.From(action.State);
                countsByState[key] = countsByState.GetValueOrDefault(key) + 1;
            }

            return new McpActionAggregateSnapshot { CountsByState = countsByState, TotalCount = actions.Count };
        }
#pragma warning disable CA1031 // A metrics failure must never make the Bridge report unhealthy.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // SECURITY: content-free — only the exception TYPE, consistent with the rest of the
            // Bridge-side MCP redaction guarantee (docs/security.md). A store fault message could
            // otherwise echo a fragment of a query parameter or connection detail.
            LogMcpActionAggregateFailed(logger, ex.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP action aggregate probe failed for /health: {ErrorMessage}")]
    private static partial void LogMcpActionAggregateFailed(ILogger logger, string errorMessage);
}
