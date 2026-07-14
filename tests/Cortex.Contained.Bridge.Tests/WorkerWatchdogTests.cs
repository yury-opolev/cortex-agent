using Cortex.Contained.Bridge;
using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

/// <summary>
/// Proves the C1 fix: the stuck-connection watchdog re-push path (which reuses the existing
/// <c>HubClient</c> and therefore fires neither <c>OnClientConnected</c> nor
/// <c>HubConnection.Reconnected</c>) also re-pushes the MCP tool catalog to the reconnected agent.
/// Without it, the agent's <c>mcpCatalogReady</c> flag (reset on reconnect) stays false forever and
/// the whole subagent subsystem silently freezes until a Bridge restart.
///
/// The watchdog branch delegates the catalog push to <see cref="Worker.PushMcpCatalogSafelyAsync"/>,
/// so that is the seam exercised here (the full 30s health-check loop in <c>ExecuteAsync</c> is not
/// unit-testable). The other Worker collaborators are not touched by this method.
/// </summary>
public sealed class WorkerWatchdogTests
{
    [Fact]
    public async Task PushMcpCatalogSafelyAsync_PushesCurrentCatalog()
    {
        var catalogLogger = new CapturingLogger<McpCatalogPusher>();
        var router = BuildEmptyRouter();
        var hostService = new McpHostService(
            Substitute.For<IMcpServerConnectionFactory>(), NullLogger<McpHostService>.Instance);
        var pusher = new McpCatalogPusher(router, hostService, catalogLogger);

        var worker = BuildWorker(pusher);

        await worker.PushMcpCatalogSafelyAsync(CancellationToken.None);

        // The push reached the pusher's fan-out (an empty catalog to zero connected tenants is a
        // valid push — the agent treats empty as ready).
        Assert.Contains(catalogLogger.Messages, m => m.Contains("Pushing MCP tool catalog", StringComparison.Ordinal));

        await router.DisposeAsync();
    }

    [Fact]
    public async Task PushMcpCatalogSafelyAsync_NoPusher_IsNoOp()
    {
        var worker = BuildWorker(mcpCatalogPusher: null);

        // Must not throw when no MCP catalog pusher was injected (MCP disabled).
        await worker.PushMcpCatalogSafelyAsync(CancellationToken.None);
    }

    private static Worker BuildWorker(McpCatalogPusher? mcpCatalogPusher)
    {
        // Only PushMcpCatalogSafelyAsync is exercised; it touches nothing but the catalog pusher,
        // so the remaining collaborators are left null.
        return new Worker(
            tenantRouter: null!,
            channelManager: null!,
            dispatcher: null!,
            config: new BridgeConfig(),
            channelLifecycle: null!,
            connectionBootstrapper: null!,
            credentialsPusher: null!,
            logger: NullLogger<Worker>.Instance,
            containerManager: null,
            mcpCatalogPusher: mcpCatalogPusher);
    }

    private static TenantRouter BuildEmptyRouter()
    {
        var config = new BridgeConfig();
        var registry = new TenantRegistry(config, () => { }, NullLogger<TenantRegistry>.Instance);
        return new TenantRouter(registry, NullLoggerFactory.Instance, NullLogger<TenantRouter>.Instance);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => this.Messages.Add(formatter(state, exception));
    }
}
