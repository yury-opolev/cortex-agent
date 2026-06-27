using Cortex.Contained.Bridge.RemoteServices;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Tokens;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Hosting;

/// <summary>
/// Builds and pushes LLM provider credentials, the active-channel list, and
/// host-persisted memory settings to a connected agent, and handles OAuth
/// token refresh/reload requests from the agent.
///
/// Extracted from <see cref="Worker"/> as the single responsibility for the
/// "push state on connect/reconnect" and token-lifecycle concerns. Pushes are
/// performed against the default tenant (channels/memory) or every connected
/// tenant (credentials), via <see cref="Tenants.TenantRouter"/>.
/// </summary>
public sealed partial class CredentialsPusher : ICredentialReplisher
{
    private readonly Tenants.TenantRouter tenantRouter;
    private readonly BridgeConfig config;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly SecretManager secretManager;
    private readonly ModelCatalog modelCatalog;
    private readonly RemoteServiceResolver resolver;
    private readonly TokenRefreshService tokenRefreshService;
    private readonly ILogger<CredentialsPusher> logger;

    internal CredentialsPusher(
        Tenants.TenantRouter tenantRouter,
        BridgeConfig config,
        IHttpClientFactory httpClientFactory,
        SecretManager secretManager,
        ModelCatalog modelCatalog,
        RemoteServiceResolver resolver,
        TokenRefreshService tokenRefreshService,
        ILogger<CredentialsPusher> logger)
    {
        this.tenantRouter = tenantRouter;
        this.config = config;
        this.httpClientFactory = httpClientFactory;
        this.secretManager = secretManager;
        this.modelCatalog = modelCatalog;
        this.resolver = resolver;
        this.tokenRefreshService = tokenRefreshService;
        this.logger = logger;
    }

    /// <summary>
    /// Builds the <see cref="LlmCredentials"/> payload from the current config and pushes it
    /// to the agent over SignalR. Called at startup, after reconnect, after setup-wizard saves,
    /// and after every OAuth token refresh so the agent always has the latest access tokens.
    /// </summary>
    public async Task PushCredentialsAsync(CancellationToken cancellationToken)
    {
        var providers = await this.BuildProviderCredentialsAsync(cancellationToken).ConfigureAwait(false);

        if (providers.Count == 0)
        {
            this.LogNoCredentialsToPush();
            return;
        }

        // Order providers by fallback order so the agent uses the first provider's
        // default model. Providers not in the fallback list are appended at the end.
        if (this.config.LlmProxy.FallbackOrder.Count > 0)
        {
            var byName = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var ordered = new List<LlmProviderCredential>(providers.Count);
            var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in this.config.LlmProxy.FallbackOrder)
            {
                if (byName.TryGetValue(name, out var match) && addedNames.Add(match.Name))
                {
                    ordered.Add(match);
                }
            }

            // Append any providers not listed in the fallback order
            foreach (var p in providers)
            {
                if (addedNames.Add(p.Name))
                {
                    ordered.Add(p);
                }
            }

            providers = ordered;
        }

        var credentials = new LlmCredentials
        {
            Providers = providers,
        };

        this.LogPushingCredentials(providers.Count);

        // Push to all connected tenant clients (including the default tenant)
        foreach (var tenantId in this.tenantRouter.GetConnectedTenantIds())
        {
            var client = this.tenantRouter.GetClient(tenantId);
            if (client?.IsConnected == true)
            {
                try
                {
                    await client.ProvideCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Individual tenant failures should not block others
                catch (Exception ex)
                {
                    this.LogCredentialPushToTenantFailed(tenantId, ex.Message);
                }
#pragma warning restore CA1031
            }
        }

        this.LogCredentialsPushed(providers.Count);
    }

    /// <summary>
    /// Builds the per-provider <see cref="LlmProviderCredential"/> list pushed to agents from
    /// the current config. For a Copilot provider, mints a short-lived bearer via
    /// <see cref="TokenRefreshService.RefreshAsync"/> (single-flight + cached, so this is cheap
    /// and safe to call on every push) and emits a <see cref="CredentialKind.GitHubCopilotBearer"/>
    /// credential carrying the minted bearer — the durable PAT never leaves the Bridge. If the
    /// mint fails (Bridge offline / GitHub error), the provider is logged and skipped so a single
    /// provider's failure never crashes the whole push.
    ///
    /// Extracted as the testable seam for the credential build (the tenant/HTTP fan-out is a
    /// separate concern handled by <see cref="PushCredentialsAsync"/>).
    /// </summary>
    internal async Task<List<LlmProviderCredential>> BuildProviderCredentialsAsync(
        CancellationToken cancellationToken)
    {
        var providers = new List<LlmProviderCredential>();

        foreach (var providerConfig in this.config.LlmProviders)
        {
            // Skip providers that have no usable credential at all
            if (string.IsNullOrWhiteSpace(providerConfig.ApiKey))
            {
                continue;
            }

            // Enrich model definitions with limits from models.dev catalog
            // (fills in any models that lack explicit ContextWindow/MaxOutputTokens)
            this.modelCatalog.EnrichModelDefinitions(providerConfig);

            var kind = ResolveCredentialKind(providerConfig);

            // GitHub Copilot: exchange the durable PAT for a short-lived bearer on the Bridge
            // and push only the bearer. The PAT (provider.ApiKey) stays on the Bridge.
            string? copilotBearer = null;
            long copilotBearerExpiresAt = 0;
            if (kind == CredentialKind.GitHubCopilotBearer)
            {
                TokenRefreshResult mint;
                try
                {
                    mint = await this.tokenRefreshService
                        .RefreshAsync(providerConfig, cancellationToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A single provider's mint failure must not crash the whole push
                catch (Exception ex)
                {
                    this.LogCopilotBearerMintFailed(providerConfig.Name, ex.Message);
                    continue;
                }
#pragma warning restore CA1031

                if (!mint.Success || string.IsNullOrEmpty(mint.AccessToken))
                {
                    this.LogCopilotBearerMintFailed(
                        providerConfig.Name, mint.Error ?? "no token returned");
                    continue;
                }

                copilotBearer = mint.AccessToken;
                copilotBearerExpiresAt = mint.ExpiresAtMs;
            }

            var credential = new LlmProviderCredential
            {
                Name = providerConfig.Name,
                Api = providerConfig.Api,
                BaseUrl = providerConfig.BaseUrl,
                Kind = kind,

                // ApiKey for static keys only. NEVER for Copilot — the PAT must not enter the
                // container; the agent receives only the minted bearer below.
                ApiKey = kind == CredentialKind.ApiKey
                    ? providerConfig.ApiKey
                    : null,

                // AccessToken: OAuth/setup-token credentials carry the stored token; Copilot
                // carries the freshly-minted short-lived bearer.
                AccessToken = kind switch
                {
                    CredentialKind.AnthropicOAuth
                        or CredentialKind.AnthropicSetupToken
                        or CredentialKind.GitHubOAuth => providerConfig.ApiKey,
                    CredentialKind.GitHubCopilotBearer => copilotBearer,
                    _ => null,
                },

                // Refresh token only for real Anthropic OAuth (not setup tokens, not Copilot).
                RefreshToken = kind == CredentialKind.AnthropicOAuth
                    ? providerConfig.RefreshToken
                    : null,

                // Expiry for Anthropic OAuth (stored) and Copilot bearer (minted).
                AccessTokenExpiresAt = kind switch
                {
                    CredentialKind.AnthropicOAuth => providerConfig.TokenExpiresAt,
                    CredentialKind.GitHubCopilotBearer => copilotBearerExpiresAt,
                    _ => 0,
                },

                Models = providerConfig.Models,
                DefaultModel = providerConfig.DefaultModel,
                MemoryModel = providerConfig.MemoryModel,
                ModelMetadata = providerConfig.ModelDefinitions.Count > 0
                    ? providerConfig.ModelDefinitions.Select(d => new LlmModelMetadata
                    {
                        Id = d.Id,
                        ContextWindow = d.ContextWindow,
                        MaxOutputTokens = d.MaxOutputTokens,
                    }).ToList()
                    : null,
            };

            providers.Add(credential);
        }

        return providers;
    }

    /// <summary>Maps the Bridge's string-based <c>TokenType</c> to the contract's <see cref="CredentialKind"/>.</summary>
    internal static CredentialKind ResolveCredentialKind(LlmProviderConfig config)
    {
        if (!string.Equals(config.TokenType, "oauth", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(config.TokenType, "pat", StringComparison.OrdinalIgnoreCase))
        {
            return CredentialKind.ApiKey;
        }

        if (string.Equals(config.TokenType, "pat", StringComparison.OrdinalIgnoreCase))
        {
            // The PAT is exchanged for a short-lived bearer on the Bridge and pushed as
            // GitHubCopilotBearer — the durable PAT never leaves the Bridge.
            return CredentialKind.GitHubCopilotBearer;
        }

        // "oauth" — distinguish Anthropic vs GitHub by the API type
        if (string.Equals(config.Api, "anthropic-messages", StringComparison.OrdinalIgnoreCase))
        {
            // Setup tokens (from `claude setup-token`) have no refresh token and
            // no expiry. Real OAuth tokens (from PKCE/device code) always have both.
            return string.IsNullOrEmpty(config.RefreshToken) && config.TokenExpiresAt == 0
                ? CredentialKind.AnthropicSetupToken
                : CredentialKind.AnthropicOAuth;
        }

        return CredentialKind.GitHubOAuth;
    }

    /// <summary>
    /// Builds the list of active channel IDs and pushes it to the agent.
    /// Called at startup and after reconnect so the agent knows which
    /// channels are available for message delivery.
    /// </summary>
    public async Task PushActiveChannelsAsync(string[] channelIds, CancellationToken cancellationToken)
    {
        var channelList = string.Join(", ", channelIds);
        this.LogPushingActiveChannels(channelIds.Length, channelList);
        var client = this.tenantRouter.GetDefaultClient();
        if (client is not null)
        {
            await client.SetActiveChannelsAsync(channelIds, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pushes host-persisted memory settings (thresholds, compaction options, and the
    /// embedding endpoint/key) to <b>every</b> connected tenant's agent, not just the
    /// default tenant — so multi-tenant deployments all receive the same config. Mirrors
    /// <see cref="PushCredentialsAsync"/>: loop connected tenants, push, and isolate
    /// per-tenant failures so one tenant's error doesn't block the others.
    /// </summary>
    public async Task PushMemorySettingsAsync(CancellationToken cancellationToken)
    {
        var config = this.BuildMemoryConfig();
        this.LogPushingMemorySettings();

        var targets = this.tenantRouter.GetConnectedTenantIds()
            .Select(tenantId => (tenantId, client: this.tenantRouter.GetClient(tenantId)))
            .Where(t => t.client?.IsConnected == true)
            .Select(t => new MemoryConfigPushTarget(t.tenantId, t.client!))
            .ToList();

        await this.PushMemoryConfigToTargetsAsync(config, targets, cancellationToken).ConfigureAwait(false);

        this.LogMemorySettingsPushed();
    }

    /// <summary>
    /// Pushes <paramref name="config"/> to each target, isolating per-tenant failures.
    /// Extracted as the testable seam for the multi-tenant push loop (the router/clients
    /// themselves are sealed and not mockable).
    /// </summary>
    internal async Task PushMemoryConfigToTargetsAsync(
        MemoryConfig config,
        IReadOnlyList<IMemoryConfigPushTarget> targets,
        CancellationToken cancellationToken)
    {
        foreach (var target in targets)
        {
            try
            {
                await target.UpdateMemoryConfigAsync(config, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Individual tenant failures should not block others
            catch (Exception ex)
            {
                this.LogMemorySettingsPushToTenantFailed(target.TenantId, ex.Message);
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Pushes the effective voice-id (speaker-id) enable flag to <b>every</b> connected
    /// tenant's agent so the voice-enrollment tool family is hidden live when voice-id is
    /// disabled. Mirrors <see cref="PushMemorySettingsAsync"/>: per-tenant failures are
    /// isolated so one tenant's error doesn't block the others.
    /// </summary>
    public async Task PushSpeakerIdConfigAsync(CancellationToken cancellationToken)
    {
        var config = new SpeakerIdConfig
        {
            Enabled = SpeechToggles.EffectiveVoiceId(this.config.Speech),
        };

        foreach (var tenantId in this.tenantRouter.GetConnectedTenantIds())
        {
            var client = this.tenantRouter.GetClient(tenantId);
            if (client is not { IsConnected: true })
            {
                continue;
            }

            try
            {
                await client.UpdateSpeakerIdConfigAsync(config, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Individual tenant failures should not block others
            catch (Exception ex)
            {
                this.LogSpeakerIdPushToTenantFailed(tenantId, ex.Message);
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Builds the <see cref="MemoryConfig"/> payload from current config, resolver, and secrets.
    /// Extracted for testability — does not perform any I/O or SignalR calls.
    /// </summary>
    internal MemoryConfig BuildMemoryConfig()
    {
        var mem = this.config.Memory;
        return new MemoryConfig
        {
            Enabled = mem.Enabled,
            DuplicateThreshold = mem.DuplicateThreshold,
            CompactionSimilarityThreshold = mem.CompactionSimilarityThreshold,
            CompactionEnabled = mem.CompactionEnabled,
            IdleCompactionEnabled = mem.IdleCompactionEnabled,
            IdleResetMinutes = mem.IdleResetMinutes,
            CompactionPreserveRecentTurns = mem.CompactionPreserveRecentTurns,
            OllamaEndpoint = this.resolver.EffectiveEmbeddingEndpoint(mem.EmbeddingEndpoint),
            OllamaApiKey = this.secretManager.GetApiKey("embeddings-provider"),
        };
    }

    /// <summary>
    /// Handles a token-refresh request from the agent.
    /// Refreshes the Anthropic OAuth token, persists the new tokens to DPAPI,
    /// updates the in-memory config, and returns the fresh tokens directly
    /// via SignalR Client Results. This avoids the deadlock where a separate
    /// <c>ProvideCredentials</c> call would be queued behind the in-progress
    /// hub method on the agent side.
    /// </summary>
    public Task<TokenRefreshResult> HandleTokenRefreshRequestAsync(
        string providerName, CancellationToken cancellationToken)
    {
        this.LogTokenRefreshRequested(providerName);

        var provider = this.config.LlmProviders.Find(p =>
            string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            this.LogTokenRefreshUnknownProvider(providerName);
            return Task.FromResult(new TokenRefreshResult { Success = false, Error = $"Unknown provider: {providerName}" });
        }

        // Delegate the cache + single-flight + strategy dispatch + persist + reload-fallback
        // to TokenRefreshService. The Anthropic-only behaviour is preserved: only the
        // AnthropicOAuthRefreshStrategy is registered, and its CanHandle returns false for
        // non-OAuth / non-Anthropic / no-refresh-token providers (the old inline gate).
        return this.tokenRefreshService.RefreshAsync(provider, cancellationToken);
    }

    /// <summary>
    /// Handles a token-reload request from the agent. Re-reads credentials from
    /// secrets.json without attempting an OAuth refresh (the refresh token is likely
    /// also stale since another process rotated both tokens).
    /// </summary>
    public Task<TokenRefreshResult> HandleTokenReloadRequestAsync(string providerName)
    {
        this.LogTokenReloadRequested(providerName);

        var provider = this.config.LlmProviders.Find(p =>
            string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            return Task.FromResult(new TokenRefreshResult
            {
                Success = false, Error = $"Unknown provider: {providerName}",
            });
        }

        var reloaded = TokenRefreshService.TryReloadTokenFromSecrets(this.secretManager, provider);
        if (reloaded)
        {
            this.LogTokenReloadedFromSecrets(providerName);
            return Task.FromResult(new TokenRefreshResult
            {
                Success = true,
                AccessToken = provider.ApiKey,
                RefreshToken = provider.RefreshToken,
                ExpiresAtMs = provider.TokenExpiresAt,
            });
        }

        return Task.FromResult(new TokenRefreshResult
        {
            Success = false,
            Error = "No fresher token found in secrets.json. Re-authenticate via setup.",
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No LLM credentials to push to agent (no providers with API keys)")]
    private partial void LogNoCredentialsToPush();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to mint Copilot bearer for provider '{ProviderName}' — skipping push for this provider: {Error}")]
    private partial void LogCopilotBearerMintFailed(string providerName, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushing LLM credentials to agent: {ProviderCount} providers")]
    private partial void LogPushingCredentials(int providerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM credentials pushed to agent: {ProviderCount} providers")]
    private partial void LogCredentialsPushed(int providerCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to push credentials to tenant '{TenantId}': {Error}")]
    private partial void LogCredentialPushToTenantFailed(string tenantId, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refresh requested by agent for provider: {ProviderName}")]
    private partial void LogTokenRefreshRequested(string providerName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token refresh requested for unknown provider: {ProviderName}")]
    private partial void LogTokenRefreshUnknownProvider(string providerName);

    [LoggerMessage(Level = LogLevel.Information, Message = "OAuth token reloaded from secrets.json for provider {ProviderName} (another process may have refreshed it)")]
    private partial void LogTokenReloadedFromSecrets(string providerName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token reload requested for provider {ProviderName} (token revoked by another process)")]
    private partial void LogTokenReloadRequested(string providerName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushing active channels to agent: {ChannelCount} channels [{ChannelIds}]")]
    private partial void LogPushingActiveChannels(int channelCount, string channelIds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushing memory settings to agent")]
    private partial void LogPushingMemorySettings();

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory settings pushed to agent")]
    private partial void LogMemorySettingsPushed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to push memory settings to tenant '{TenantId}': {Error}")]
    private partial void LogMemorySettingsPushToTenantFailed(string tenantId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to push voice-id config to tenant '{TenantId}': {Error}")]
    private partial void LogSpeakerIdPushToTenantFailed(string tenantId, string error);
}

/// <summary>
/// One connected tenant the memory config is pushed to. Abstracted so the multi-tenant
/// push loop can be unit-tested without the sealed <c>TenantRouter</c>/<c>HubClient</c>.
/// </summary>
internal interface IMemoryConfigPushTarget
{
    /// <summary>The tenant ID, used for per-tenant failure logging.</summary>
    string TenantId { get; }

    /// <summary>Pushes the memory config to this tenant's agent.</summary>
    Task UpdateMemoryConfigAsync(MemoryConfig config, CancellationToken cancellationToken);
}

/// <summary>Adapts a connected <see cref="Hub.HubClient"/> to <see cref="IMemoryConfigPushTarget"/>.</summary>
internal sealed class MemoryConfigPushTarget : IMemoryConfigPushTarget
{
    private readonly Hub.HubClient client;

    public MemoryConfigPushTarget(string tenantId, Hub.HubClient client)
    {
        this.TenantId = tenantId;
        this.client = client;
    }

    public string TenantId { get; }

    public Task UpdateMemoryConfigAsync(MemoryConfig config, CancellationToken cancellationToken)
        => this.client.UpdateMemoryConfigAsync(config, cancellationToken);
}
