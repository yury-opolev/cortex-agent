using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class TenantRouterConnectorAbortDispatcherTests
{
    [Fact]
    public async Task AbortAsync_NoConnectedClient_DoesNotThrow()
    {
        var registry = new TenantRegistry(
            new BridgeConfig
            {
                AgentHubUrl = string.Empty,
                HubToken = "12345678",
                Tenants = [],
            },
            static () => { },
            NullLogger<TenantRegistry>.Instance);
        var router = new TenantRouter(registry, NullLoggerFactory.Instance, NullLogger<TenantRouter>.Instance);
        var dispatcher = new TenantRouterConnectorAbortDispatcher(router, NullLogger<TenantRouterConnectorAbortDispatcher>.Instance);

        var ex = await Record.ExceptionAsync(() => dispatcher.AbortAsync("plugin:test:default", "conv1", CancellationToken.None));

        Assert.Null(ex);
    }
}
