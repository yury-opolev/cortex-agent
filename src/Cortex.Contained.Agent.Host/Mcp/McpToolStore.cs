using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Mcp;

/// <summary>
/// Mutable, thread-safe store of the agent's current MCP proxy tools. The Bridge
/// pushes a replace-all <see cref="McpToolCatalog"/> via the hub; <see cref="Update"/>
/// rebuilds the <see cref="McpProxyTool"/> set and bumps <see cref="Version"/> so the
/// <see cref="ToolRegistry"/> can invalidate its cached tool definitions.
/// </summary>
public sealed class McpToolStore
{
    private readonly IMcpGateway gateway;
    private readonly ILoggerFactory loggerFactory;
    private readonly object syncLock = new();

    private FrozenDictionary<string, IAgentTool> toolsByName =
        FrozenDictionary<string, IAgentTool>.Empty;
    private int version;

    public McpToolStore(IMcpGateway gateway, ILoggerFactory? loggerFactory = null)
    {
        this.gateway = gateway;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Monotonically increasing version, bumped on every <see cref="Update"/>.
    /// Consumers compare against a cached value to detect catalog changes.
    /// </summary>
    public int Version
    {
        get
        {
            lock (this.syncLock)
            {
                return this.version;
            }
        }
    }

    /// <summary>Snapshot of the current MCP proxy tools.</summary>
    public IReadOnlyCollection<IAgentTool> Tools
    {
        get
        {
            lock (this.syncLock)
            {
                return this.toolsByName.Values;
            }
        }
    }

    /// <summary>Look up a proxy tool by its namespaced full name (case-insensitive).</summary>
    public bool TryGet(string fullName, [NotNullWhen(true)] out IAgentTool? tool)
    {
        FrozenDictionary<string, IAgentTool> snapshot;
        lock (this.syncLock)
        {
            snapshot = this.toolsByName;
        }

        return snapshot.TryGetValue(fullName, out tool);
    }

    /// <summary>
    /// Replace the current tool set with proxies built from <paramref name="catalog"/>
    /// and bump <see cref="Version"/>. A null catalog or null tool list is treated as empty.
    /// </summary>
    public void Update(McpToolCatalog? catalog)
    {
        var logger = this.loggerFactory.CreateLogger<McpProxyTool>();
        var definitions = catalog?.Tools ?? [];

        var rebuilt = definitions
            .Select(IAgentTool (def) => new McpProxyTool(def, this.gateway, logger))
            .ToFrozenDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        lock (this.syncLock)
        {
            this.toolsByName = rebuilt;
            this.version++;
        }
    }
}
