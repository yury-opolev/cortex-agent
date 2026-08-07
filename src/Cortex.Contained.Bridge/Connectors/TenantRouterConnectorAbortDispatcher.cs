using Cortex.Contained.Bridge.Tenants;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Resolves the <see cref="Hub.HubClient"/> for a plugin channel's tenant and
/// calls <c>AbortGenerationAsync</c> on it. Mirrors the pattern used by
/// <see cref="Hosting.ChannelLifecycleManager"/> for voice barge-in.
/// </summary>
public sealed partial class TenantRouterConnectorAbortDispatcher : IConnectorAbortDispatcher
{
    private readonly TenantRouter tenantRouter;
    private readonly ILogger<TenantRouterConnectorAbortDispatcher> logger;

    /// <summary>Initialises a new <see cref="TenantRouterConnectorAbortDispatcher"/>.</summary>
    public TenantRouterConnectorAbortDispatcher(TenantRouter tenantRouter, ILogger<TenantRouterConnectorAbortDispatcher> logger)
    {
        this.tenantRouter = tenantRouter;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task AbortAsync(string channelId, string conversationId, CancellationToken ct)
    {
        var tenantId = this.tenantRouter.ResolveChannel(channelId);
        var client = tenantId is not null
            ? this.tenantRouter.GetClient(tenantId)
            : this.tenantRouter.GetDefaultClient();

        if (client is null || !client.IsConnected)
        {
            this.LogHubUnavailable(channelId, conversationId);
            return;
        }

        try
        {
            await client.AbortGenerationAsync(conversationId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.LogAbortFailed(channelId, conversationId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector abort: no connected hub client for channel {ChannelId}, conversation {ConversationId}. Abort skipped.")]
    private partial void LogHubUnavailable(string channelId, string conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector abort: hub call failed for channel {ChannelId}, conversation {ConversationId}.")]
    private partial void LogAbortFailed(string channelId, string conversationId, Exception ex);
}
