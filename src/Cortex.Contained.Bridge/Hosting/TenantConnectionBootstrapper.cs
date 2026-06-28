using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Hosting;

/// <summary>
/// Wires every tenant <see cref="HubClient"/> when it connects: dispatcher
/// (outbound message routing), the coding-agent hub binder, hub event handlers
/// (logging, token refresh/reload, voice-gender/voiceprint/enrollment events),
/// and the reconnect re-push of credentials/channels/memory settings.
///
/// Extracted from <see cref="Worker"/> as the per-tenant connection-bootstrap
/// responsibility. The exact event subscription order and the reconnect push
/// order are preserved verbatim from the original Worker.
/// </summary>
public sealed partial class TenantConnectionBootstrapper
{
    private readonly HubMessageDispatcher dispatcher;
    private readonly CredentialsPusher credentialsPusher;
    private readonly BridgeConfig config;
    private readonly Cortex.Contained.Bridge.Coding.CodingHubBinder? externalAgentBinder;
    private readonly Cortex.Contained.Bridge.SpeakerId.SignalRVoiceprintCache? voiceprintCache;
    private readonly Cortex.Contained.Channels.Discord.IEnrollmentProgressNotifier? enrollmentProgressNotifier;
    private readonly Cortex.Contained.Bridge.Mcp.McpHostService? mcpHostService;
    private readonly Cortex.Contained.Bridge.Mcp.McpCatalogPusher? mcpCatalogPusher;
    private readonly ILogger<TenantConnectionBootstrapper> logger;

    /// <summary>
    /// Supplies the active channel ID list for the reconnect re-push. Set by the
    /// <see cref="Worker"/> before connecting so this collaborator stays decoupled
    /// from channel registration (which lives in the channel-lifecycle manager).
    /// </summary>
    public Func<string[]>? ActiveChannelIdsProvider { get; set; }

    public TenantConnectionBootstrapper(
        HubMessageDispatcher dispatcher,
        CredentialsPusher credentialsPusher,
        BridgeConfig config,
        ILogger<TenantConnectionBootstrapper> logger,
        Cortex.Contained.Bridge.Coding.CodingHubBinder? externalAgentBinder = null,
        Cortex.Contained.Bridge.SpeakerId.SignalRVoiceprintCache? voiceprintCache = null,
        Cortex.Contained.Channels.Discord.IEnrollmentProgressNotifier? enrollmentProgressNotifier = null,
        Cortex.Contained.Bridge.Mcp.McpHostService? mcpHostService = null,
        Cortex.Contained.Bridge.Mcp.McpCatalogPusher? mcpCatalogPusher = null)
    {
        this.dispatcher = dispatcher;
        this.credentialsPusher = credentialsPusher;
        this.config = config;
        this.logger = logger;
        this.externalAgentBinder = externalAgentBinder;
        this.voiceprintCache = voiceprintCache;
        this.enrollmentProgressNotifier = enrollmentProgressNotifier;
        this.mcpHostService = mcpHostService;
        this.mcpCatalogPusher = mcpCatalogPusher;
    }

    /// <summary>
    /// Called by <see cref="Tenants.TenantRouter"/> after any tenant's <see cref="HubClient"/> connects.
    /// Wires both dispatcher events (outbound message routing) and hub events (logging, token refresh).
    /// </summary>
    public void OnTenantClientConnected(HubClient client, string tenantId)
    {
        this.dispatcher.WireHubClient(client, tenantId);
        this.WireHubEvents(client, tenantId);
        this.externalAgentBinder?.WireHubClient(client, tenantId);
        this.WireMcp(client);
        this.LogTenantEventsWired(tenantId);
    }

    /// <summary>
    /// Wires MCP routing for a connected agent: tool invocations route to the host service (which
    /// attaches auth on the host), and the current tool catalog is pushed so the agent registers the
    /// available <c>mcp__*</c> tools. Re-pushed on reconnect.
    /// </summary>
    private void WireMcp(HubClient client)
    {
        if (this.mcpHostService is not null)
        {
            client.OnInvokeMcpTool += invocation =>
                this.mcpHostService.InvokeAsync(
                    invocation.ServerKey, invocation.ToolName, invocation.ArgumentsJson, CancellationToken.None);
        }

        if (this.mcpCatalogPusher is not null)
        {
            // Initial catalog push for this (possibly late-joining) agent.
            _ = this.PushMcpCatalogSafelyAsync();

            client.Reconnected += _ => this.PushMcpCatalogSafelyAsync();
        }
    }

    private async Task PushMcpCatalogSafelyAsync()
    {
        if (this.mcpCatalogPusher is null)
        {
            return;
        }

        try
        {
            await this.mcpCatalogPusher.PushCurrentCatalogAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A catalog-push failure must not break connection wiring.
        catch (Exception ex)
        {
            this.LogHealthCheckFailed($"Failed to push MCP catalog: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private void WireHubEvents(HubClient client, string tenantId)
    {

        // These are for logging only — actual message routing is handled by HubMessageDispatcher
        client.OnToolExecution += toolExec =>
        {
            this.LogToolExecution(toolExec.ConversationId, toolExec.ToolName, toolExec.Status);
            return Task.CompletedTask;
        };

        client.OnStatusChanged += status =>
        {
            this.LogStatusChanged(status.Status, status.ActiveConversations);
            return Task.CompletedTask;
        };

        client.Disconnected += ex =>
        {
            this.LogHubDisconnected(ex?.Message);
            return Task.CompletedTask;
        };

        client.Reconnected += async connectionId =>
        {
            this.LogHubReconnected(connectionId);
            // Re-push credentials, active channels, and memory settings after reconnect
            try
            {
                await this.credentialsPusher.PushCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
                var channelIds = this.ActiveChannelIdsProvider?.Invoke() ?? Array.Empty<string>();
                await this.credentialsPusher.PushActiveChannelsAsync(channelIds, CancellationToken.None).ConfigureAwait(false);
                await this.credentialsPusher.PushMemorySettingsAsync(CancellationToken.None).ConfigureAwait(false);
                await this.credentialsPusher.PushSpeakerIdConfigAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogHealthCheckFailed($"Failed to re-push state after reconnect: {ex.Message}");
            }
        };

        // Agent signals that it needs a token refresh (Anthropic OAuth token expired/expiring).
        client.OnTokenRefreshRequested += providerName =>
            this.credentialsPusher.HandleTokenRefreshRequestAsync(providerName, CancellationToken.None);

        // Agent signals that its token was revoked (403) — another process rotated it.
        client.OnTokenReloadRequested += providerName =>
            this.credentialsPusher.HandleTokenReloadRequestAsync(providerName);

        // Agent detected voice gender from personality — update tenant config
        client.OnVoiceGenderDetected += gender =>
        {
            if (this.config.Tenants.TryGetValue(tenantId, out var tenant)
                && !string.Equals(tenant.VoiceGender, gender, StringComparison.OrdinalIgnoreCase))
            {
                tenant.VoiceGender = gender;
                this.LogVoiceGenderUpdated(tenantId, gender);
            }

            return Task.CompletedTask;
        };

        // Agent invalidated this tenant's voiceprint cache entry.
        client.OnVoiceprintInvalidated += invalidatedTenantId =>
        {
            this.voiceprintCache?.Invalidate(invalidatedTenantId);
            return Task.CompletedTask;
        };

        // Agent pushed an enrollment progress event — forward to the Discord progress notifier.
        client.OnVoiceEnrollmentProgress += (progressTenantId, stateName, captured, required) =>
            this.enrollmentProgressNotifier?.ReportAsync(progressTenantId, stateName, captured, required).AsTask()
                ?? Task.CompletedTask;

        // Reconnected: any invalidation pushes we missed while disconnected
        // would leave stale (or negative-cached) entries. Flushing the entire
        // cache forces a refetch on the next verification call.
        client.Reconnected += _ =>
        {
            this.voiceprintCache?.InvalidateAll();
            return Task.CompletedTask;
        };
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tool execution in {ConversationId}: {ToolName} -> {ToolStatus}")]
    private partial void LogToolExecution(string conversationId, string toolName, ToolExecutionStatus toolStatus);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent status changed: {AgentStatus}, active={ActiveConversations}")]
    private partial void LogStatusChanged(AgentStatus agentStatus, int activeConversations);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hub disconnected: {Reason}")]
    private partial void LogHubDisconnected(string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hub reconnected: {ConnectionId}")]
    private partial void LogHubReconnected(string? connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health check failed: {ErrorMessage}")]
    private partial void LogHealthCheckFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hub events wired for tenant '{TenantId}'")]
    private partial void LogTenantEventsWired(string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice gender updated for tenant '{TenantId}': {Gender}")]
    private partial void LogVoiceGenderUpdated(string tenantId, string gender);
}
