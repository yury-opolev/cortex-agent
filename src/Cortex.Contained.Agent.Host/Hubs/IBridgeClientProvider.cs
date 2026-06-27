using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary>Abstraction over the connected Bridge client proxy, for testability.</summary>
public interface IBridgeClientProvider
{
    /// <summary>The current Bridge client proxy, or null if no Bridge is connected.</summary>
    IAgentHubClient? Client { get; }
}
