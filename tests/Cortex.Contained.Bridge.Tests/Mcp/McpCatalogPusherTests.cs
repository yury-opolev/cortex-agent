using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpCatalogPusherTests
{
    private sealed class FakeTarget : IMcpCatalogPushTarget
    {
        private readonly bool throws;

        public FakeTarget(string tenantId, bool throws = false)
        {
            this.TenantId = tenantId;
            this.throws = throws;
        }

        public string TenantId { get; }

        public int PushCount { get; private set; }

        public McpToolCatalog? LastCatalog { get; private set; }

        public Task PushMcpToolCatalogAsync(McpToolCatalog catalog, CancellationToken cancellationToken)
        {
            this.PushCount++;
            this.LastCatalog = catalog;
            if (this.throws)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        }
    }

    private static McpCatalogPusher BuildPusher()
        => new(tenantRouter: null!, hostService: null!, logger: NullLogger<McpCatalogPusher>.Instance);

    [Fact]
    public async Task PushToTargets_PushesToEveryTenant()
    {
        var catalog = new McpToolCatalog { Tools = [] };
        var t1 = new FakeTarget("default");
        var t2 = new FakeTarget("tenant-a");

        await BuildPusher().PushToTargetsAsync(catalog, [t1, t2], CancellationToken.None);

        Assert.Equal(1, t1.PushCount);
        Assert.Equal(1, t2.PushCount);
        Assert.Same(catalog, t1.LastCatalog);
    }

    [Fact]
    public async Task PushToTargets_IsolatesPerTenantFailures()
    {
        var t1 = new FakeTarget("default");
        var failing = new FakeTarget("tenant-a", throws: true);
        var t2 = new FakeTarget("tenant-b");

        await BuildPusher().PushToTargetsAsync(new McpToolCatalog(), [t1, failing, t2], CancellationToken.None);

        Assert.Equal(1, t1.PushCount);
        Assert.Equal(1, t2.PushCount);
    }
}
