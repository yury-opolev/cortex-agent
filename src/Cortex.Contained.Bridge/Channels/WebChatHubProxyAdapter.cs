using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Channels.WebChat;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Channels;

/// <summary>
/// Adapts <see cref="TenantRouter"/> to <see cref="IWebChatHubProxy"/>
/// so the WebChat library stays decoupled from Bridge internals.
/// Routes to the default tenant's <see cref="Hub.HubClient"/>.
/// </summary>
public sealed class WebChatHubProxyAdapter : IWebChatHubProxy
{
    private readonly TenantRouter tenantRouter;

    public WebChatHubProxyAdapter(TenantRouter tenantRouter)
    {
        this.tenantRouter = tenantRouter;
    }

    /// <inheritdoc />
    public async Task<AgentStatusInfo> GetStatusAsync(CancellationToken cancellationToken)
    {
        var client = this.tenantRouter.GetDefaultClient();
        if (client is null)
        {
            return new AgentStatusInfo { Status = AgentStatus.Error, ActiveConversations = 0 };
        }

        return await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AbortGenerationAsync(string conversationId, CancellationToken cancellationToken)
    {
        var client = this.tenantRouter.GetDefaultClient();
        if (client is not null)
        {
            await client.AbortGenerationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }
    }
}
