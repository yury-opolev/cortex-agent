using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpCatalogPusherTests
{
    private sealed class FakeTarget : IMcpCatalogPushTarget
    {
        private readonly Exception? exceptionToThrow;

        public FakeTarget(string tenantId, bool throws = false, Exception? exceptionToThrow = null)
        {
            this.TenantId = tenantId;
            this.exceptionToThrow = throws ? exceptionToThrow ?? new InvalidOperationException("boom") : null;
        }

        public string TenantId { get; }

        public int PushCount { get; private set; }

        public McpToolCatalog? LastCatalog { get; private set; }

        public Task PushMcpToolCatalogAsync(McpToolCatalog catalog, CancellationToken cancellationToken)
        {
            this.PushCount++;
            this.LastCatalog = catalog;
            if (this.exceptionToThrow is not null)
            {
                throw this.exceptionToThrow;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Captures fully-formatted log messages so redaction assertions can inspect them.</summary>
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

    private static McpCatalogPusher BuildPusher(ILogger<McpCatalogPusher>? logger = null)
        => new(tenantRouter: null!, hostService: null!, logger: logger ?? NullLogger<McpCatalogPusher>.Instance);

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

    [Fact]
    public async Task PushToTargets_TargetThrows_LogDoesNotContainTheRawExceptionMessage()
    {
        // SECURITY: content-free — only the exception TYPE, consistent with the Bridge-side MCP
        // redaction guarantee (docs/security.md). A push failure could otherwise echo a fragment
        // of a hub connection detail.
        var capturingLogger = new CapturingLogger<McpCatalogPusher>();
        var secretLookingMessage = "hub push failed for https://user:s3cr3t@internal.example/hub";
        var failing = new FakeTarget("tenant-a", throws: true, exceptionToThrow: new InvalidOperationException(secretLookingMessage));

        await BuildPusher(capturingLogger).PushToTargetsAsync(new McpToolCatalog(), [failing], CancellationToken.None);

        var failureLogs = capturingLogger.Messages
            .Where(m => m.Contains("Failed to push", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var failureLog = Assert.Single(failureLogs);
        Assert.Contains(nameof(InvalidOperationException), failureLog, StringComparison.Ordinal);
        Assert.DoesNotContain(secretLookingMessage, failureLog, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", failureLog, StringComparison.Ordinal);
    }
}
