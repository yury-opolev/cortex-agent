using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cortex.Contained.Common.Auth;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge;

/// <summary>
/// Helpers for the first-run setup wizard: YAML generation, provider templates,
/// model fetching from provider APIs, GitHub Copilot OAuth device flow, and DTOs.
/// </summary>
public static class SetupHelpers
{
    // ── Anthropic OAuth (Authorization Code + PKCE) ────────────────

    /// <summary>Anthropic OAuth App client ID (registered UUID).
    /// Used for both PKCE authorization-code and device-code flows.</summary>
    internal const string AnthropicOAuthClientId = ""; // no official client id bundled — set one to enable Anthropic subscription OAuth

    private const string AnthropicAuthUrlPro = "https://claude.ai/oauth/authorize";
    private const string AnthropicTokenUrl = "https://console.anthropic.com/v1/oauth/token";
    private const string AnthropicRedirectUri = "https://console.anthropic.com/oauth/code/callback";
    private const string AnthropicOAuthScopes = "org:create_api_key user:profile user:inference";

    /// <summary>
    /// Generates a PKCE code verifier and its S256 challenge.
    /// Returns (verifier, challenge) — the verifier must be kept secret server-side and sent
    /// in the token-exchange request; the challenge goes in the authorization URL.
    /// </summary>
    public static (string Verifier, string Challenge) GenerateAnthropicPkce()
    {
        // 64 random bytes → base64url → ~86-char verifier (matches opencode)
        var bytes = RandomNumberGenerator.GetBytes(64);
        var verifier = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        // S256 challenge = BASE64URL(SHA256(ASCII(verifier)))
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return (verifier, challenge);
    }

    /// <summary>
    /// Builds the Anthropic authorization URL the user must open in their browser.
    /// The verifier returned by <see cref="GenerateAnthropicPkce"/> must be kept server-side
    /// and passed to <see cref="ExchangeAnthropicCodeAsync"/> when the user pastes the code.
    /// </summary>
    public static string BuildAnthropicAuthUrl(string codeChallenge, string codeVerifier)
    {
        var url = new UriBuilder(AnthropicAuthUrlPro);
        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        query["code"] = "true";           // non-standard Anthropic flag required by their auth server
        query["response_type"] = "code";
        query["client_id"] = AnthropicOAuthClientId;
        query["redirect_uri"] = AnthropicRedirectUri;
        query["scope"] = AnthropicOAuthScopes;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";
        query["state"] = codeVerifier;    // verifier doubles as state (opencode convention)
        url.Query = query.ToString();
        return url.ToString();
    }

    /// <summary>
    /// Exchanges an authorization code (pasted by the user) for an access + refresh token pair.
    /// </summary>
    public static async Task<AnthropicOAuthTokenResponse> ExchangeAnthropicCodeAsync(
        string code, string codeVerifier, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        // Anthropic's callback page returns "code#state" — both parts must be sent in the body
        var hashIdx   = code.IndexOf('#', StringComparison.Ordinal);
        var bareCode  = hashIdx >= 0 ? code[..hashIdx].Trim() : code.Trim();
        var stateValue = hashIdx >= 0 ? code[(hashIdx + 1)..].Trim() : null;

        using var request = new HttpRequestMessage(HttpMethod.Post, AnthropicTokenUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Build the body; include "state" only when present (mirrors opencode behaviour)
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["grant_type"]    = "authorization_code",
            ["client_id"]     = AnthropicOAuthClientId,
            ["code"]          = bareCode,
            ["redirect_uri"]  = AnthropicRedirectUri,
            ["code_verifier"] = codeVerifier,
        };
        if (stateValue is not null)
        {
            body["state"] = stateValue;
        }

        request.Content = new StringContent(
            body.ToJsonString(),
            Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Anthropic token exchange failed ({(int)response.StatusCode}): {json}"));
        }

        return JsonSerializer.Deserialize<AnthropicOAuthTokenResponse>(json, JsonCatalogOptions)
            ?? throw new InvalidOperationException("Failed to deserialize Anthropic token response.");
    }

    /// <summary>
    /// Uses a refresh token to obtain a fresh access + refresh token pair.
    /// Delegates to <see cref="Cortex.Contained.Contracts.Auth.OAuthTokenService"/>.
    /// </summary>
    public static Task<AnthropicOAuthTokenResponse> RefreshAnthropicTokenAsync(
        string refreshToken, HttpClient httpClient, CancellationToken cancellationToken = default)
        => OAuthTokenService.RefreshAnthropicTokenAsync(refreshToken, httpClient, cancellationToken);

    // ── GitHub OAuth Device Flow ────────────────────────────────────

    /// <summary>
    /// Default GitHub OAuth App client ID for the Copilot device flow.
    /// Users can register their own OAuth App and override this in cortex.yml.
    /// </summary>
    internal const string DefaultCopilotOAuthClientId = "Ov23li8tweQw6odWQebz"; // shared default GitHub OAuth App — register your own and override in config

    /// <summary>Safety margin added to the polling interval to avoid clock-skew issues.</summary>
    private const int OAuthPollingSafetyMarginMs = 3000;

    /// <summary>
    /// Initiate the GitHub OAuth device flow for Copilot.
    /// Returns a device code, user code, and verification URL that the user
    /// must visit to authorise Cortex.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="clientId">
    /// GitHub OAuth App client ID. If null or empty, <see cref="DefaultCopilotOAuthClientId"/> is used.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<CopilotDeviceFlowResponse> InitiateCopilotDeviceFlowAsync(
        HttpClient httpClient, string? clientId = null, CancellationToken cancellationToken = default)
    {
        var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? DefaultCopilotOAuthClientId : clientId;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("cortex-agent/1.0.0");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { client_id = effectiveClientId, scope = "read:user" }),
            Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var deviceData = JsonSerializer.Deserialize<GitHubDeviceCodeResponse>(json, JsonCatalogOptions)
            ?? throw new InvalidOperationException("Failed to parse device code response.");

        return new CopilotDeviceFlowResponse
        {
            DeviceCode = deviceData.DeviceCode ?? throw new InvalidOperationException("No device_code in response."),
            UserCode = deviceData.UserCode ?? throw new InvalidOperationException("No user_code in response."),
            VerificationUri = deviceData.VerificationUri ?? "https://github.com/login/device",
            ExpiresInSeconds = deviceData.ExpiresIn,
            PollingIntervalSeconds = deviceData.Interval > 0 ? deviceData.Interval : 5,
        };
    }

    /// <summary>
    /// Poll the GitHub OAuth token endpoint once.
    /// Returns <see cref="CopilotPollResult"/> with the access token on success,
    /// or a status indicating the flow is still pending / failed.
    /// </summary>
    /// <param name="deviceCode">Device code from <see cref="InitiateCopilotDeviceFlowAsync"/>.</param>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="clientId">
    /// GitHub OAuth App client ID. Must match the one used in <see cref="InitiateCopilotDeviceFlowAsync"/>.
    /// If null or empty, <see cref="DefaultCopilotOAuthClientId"/> is used.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<CopilotPollResult> PollCopilotTokenAsync(
        string deviceCode, HttpClient httpClient, string? clientId = null, CancellationToken cancellationToken = default)
    {
        var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? DefaultCopilotOAuthClientId : clientId;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("cortex-agent/1.0.0");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                client_id = effectiveClientId,
                device_code = deviceCode,
                grant_type = "urn:ietf:params:oauth:grant-type:device_code",
            }),
            Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new CopilotPollResult { Status = "failed" };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var tokenData = JsonSerializer.Deserialize<GitHubAccessTokenResponse>(json, JsonCatalogOptions);

        if (!string.IsNullOrEmpty(tokenData?.AccessToken))
        {
            return new CopilotPollResult
            {
                Status = "success",
                AccessToken = tokenData.AccessToken,
            };
        }

        // Map GitHub error codes
        return (tokenData?.Error) switch
        {
            "authorization_pending" => new CopilotPollResult { Status = "pending" },
            "slow_down" => new CopilotPollResult
            {
                Status = "pending",
                RetryAfterSeconds = (tokenData?.Interval ?? 10) + OAuthPollingSafetyMarginMs / 1000,
            },
            "expired_token" => new CopilotPollResult { Status = "expired" },
            "access_denied" => new CopilotPollResult { Status = "denied" },
            _ => new CopilotPollResult { Status = "failed" },
        };
    }

    // ── YAML Generation ────────────────────────────────────────────

    /// <summary>Generate a cortex.yml config file from a setup request.</summary>
    public static string GenerateYaml(SetupRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cortex Configuration — generated by Setup Wizard");
        sb.AppendLine();
        sb.AppendLine("agentHubUrl: http://localhost:5100/hub/agent");
        sb.AppendLine("hubToken: ${CORTEX_HUB_TOKEN:-dev-token-change-me}");
        sb.AppendLine();
        sb.AppendLine("webUi:");
        sb.AppendLine("  enabled: true");
        sb.AppendLine("  port: 5080");
        sb.AppendLine("  bindAddress: 127.0.0.1");
        sb.AppendLine();
        sb.AppendLine("llmProviders:");

        var providerName = ResolveProviderName(request.Provider);
        var api = ResolveApi(request.Provider);
        var baseUrl = ResolveBaseUrl(request.Provider);
        // Pass "oauth" as authMethod when the request carries a refresh token (Anthropic OAuth)
        var authMethod = !string.IsNullOrEmpty(request.RefreshToken) ? "oauth" : null;
        var tokenType = ResolveTokenType(request.Provider, authMethod);

        sb.AppendLine(CultureInfo.InvariantCulture, $"  - name: {providerName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    api: {api}");
        if (baseUrl is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    baseUrl: {baseUrl}");
        }

        if (tokenType != "bearer")
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    tokenType: {tokenType}");
        }

        // Emit clientId only when a custom (non-default) value is provided
        if (!string.IsNullOrWhiteSpace(request.ClientId) &&
            !string.Equals(request.ClientId, DefaultCopilotOAuthClientId, StringComparison.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    clientId: {request.ClientId}");
        }

        // Default model is the first selected model
        if (request.Models.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    defaultModel: {request.Models[0]}");
        }

        sb.AppendLine("    models:");
        foreach (var model in request.Models)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"      - {model}");
        }

        sb.AppendLine();
        sb.AppendLine("llmProxy:");
        sb.AppendLine("  fallbackOrder:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    - {providerName}");

        return sb.ToString();
    }

    /// <summary>
    /// Generate a cortex.yml config file from a fully-resolved list of provider configs
    /// and an explicit fallback order. Used by the multi-provider save endpoint.
    /// </summary>
    public static string GenerateYaml(
        IReadOnlyList<LlmProviderConfig> providers,
        IReadOnlyList<string> fallbackOrder)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cortex Configuration — generated by Setup Wizard");
        sb.AppendLine();
        sb.AppendLine("agentHubUrl: http://localhost:5100/hub/agent");
        sb.AppendLine("hubToken: ${CORTEX_HUB_TOKEN:-dev-token-change-me}");
        sb.AppendLine();
        sb.AppendLine("webUi:");
        sb.AppendLine("  enabled: true");
        sb.AppendLine("  port: 5080");
        sb.AppendLine("  bindAddress: 127.0.0.1");
        sb.AppendLine();
        sb.AppendLine("llmProviders:");

        foreach (var p in providers)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  - name: {p.Name}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    api: {p.Api}");

            if (p.BaseUrl is not null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    baseUrl: {p.BaseUrl}");
            }

            if (!string.IsNullOrEmpty(p.TokenType) && p.TokenType != "bearer")
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    tokenType: {p.TokenType}");
            }

            if (!string.IsNullOrWhiteSpace(p.ClientId) &&
                !string.Equals(p.ClientId, DefaultCopilotOAuthClientId, StringComparison.Ordinal))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    clientId: {p.ClientId}");
            }

        if (p.Models.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    defaultModel: {p.Models[0]}");

                if (!string.IsNullOrEmpty(p.MemoryModel))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    memoryModel: {p.MemoryModel}");
                }

                sb.AppendLine("    models:");
                foreach (var model in p.Models)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      - {model}");
                }
            }

            // Emit per-model metadata (context window, max output tokens) if available
            if (p.ModelDefinitions.Count > 0)
            {
                sb.AppendLine("    modelDefinitions:");
                foreach (var def in p.ModelDefinitions)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      - id: {def.Id}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        contextWindow: {def.ContextWindow}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        maxOutputTokens: {def.MaxOutputTokens}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("llmProxy:");
        sb.AppendLine("  fallbackOrder:");

        var effectiveFallback = fallbackOrder.Count > 0
            ? fallbackOrder
            : providers.Select(p => p.Name).ToList();

        foreach (var name in effectiveFallback)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    - {name}");
        }

        return sb.ToString();
    }

    // ── Provider Resolution ────────────────────────────────────────

    /// <summary>Resolve provider name from template key.</summary>
    public static string ResolveProviderName(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "github-copilot-api" => "github-copilot-api",
            "openai" => "openai",
            "anthropic" => "anthropic",
            _ => provider.ToLowerInvariant(),
        };
    }

    /// <summary>Resolve API type from provider key.</summary>
    public static string ResolveApi(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "github-copilot-api" => "github-copilot-api",
            "openai" => "openai-completions",
            "anthropic" => "anthropic-messages",
            _ => "openai-completions",
        };
    }

    /// <summary>Resolve base URL from provider key.</summary>
    public static string? ResolveBaseUrl(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "github-copilot-api" => "https://api.githubcopilot.com",
            _ => null,
        };
    }

    /// <summary>Resolve token type from provider key (and optional auth method).</summary>
    public static string ResolveTokenType(string provider, string? authMethod = null)
    {
        return provider.ToLowerInvariant() switch
        {
            "github-copilot-api" => "oauth",
            "anthropic" when string.Equals(authMethod, "oauth", StringComparison.OrdinalIgnoreCase) => "oauth",
            _ => "bearer",
        };
    }

    // ── Provider Templates ─────────────────────────────────────────

    /// <summary>Get available provider templates for the setup wizard.</summary>
    public static List<ProviderTemplate> GetProviderTemplates()
    {
        return
        [
            new ProviderTemplate
            {
                Id = "github-copilot-api",
                Name = "GitHub Copilot",
                Description = "Use all models in your Copilot Pro+ subscription (Claude, GPT, Gemini, etc.). Authenticates via GitHub OAuth — no API key needed.",
                AuthMethod = "oauth",
            },
            new ProviderTemplate
            {
                Id = "openai",
                Name = "OpenAI",
                Description = "Use OpenAI's API directly. Requires an OpenAI API key.",
                AuthMethod = "apikey",
                ApiKeyLabel = "OpenAI API Key",
                ApiKeyPlaceholder = "sk-...",
            },
            new ProviderTemplate
            {
                Id = "anthropic",
                Name = "Anthropic",
                Description = "Use Anthropic's Claude models. Sign in with Claude Pro/Max or paste an API key.",
                AuthMethod = "apikey",
                SupportsOAuth = true,
                ApiKeyLabel = "Anthropic API Key",
                ApiKeyPlaceholder = "sk-ant-...",
            },
        ];
    }

    // ── Model Fetching ─────────────────────────────────────────────

    /// <summary>
    /// Fetch available models from the provider's API.
    /// All providers use live API calls to get the current model list.
    /// </summary>
    public static async Task<List<AvailableModel>> FetchAvailableModelsAsync(
        string provider,
        string apiKey,
        HttpClient httpClient,
        string? tokenType = null,
        CancellationToken cancellationToken = default)
    {
        return provider.ToLowerInvariant() switch
        {
            "github-copilot-api" => await FetchCopilotApiModelsAsync(apiKey, httpClient, cancellationToken).ConfigureAwait(false),
            "openai" => await FetchOpenAiModelsAsync(apiKey, httpClient, cancellationToken).ConfigureAwait(false),
            "anthropic" => await FetchAnthropicModelsAsync(apiKey, httpClient, tokenType, cancellationToken).ConfigureAwait(false),
            _ => [],
        };
    }

    /// <summary>
    /// Fetch available models from <c>api.githubcopilot.com/models</c>.
    /// Requires a GitHub OAuth token (obtained via the device flow).
    /// </summary>
    internal static async Task<List<AvailableModel>> FetchCopilotApiModelsAsync(
        string oauthToken, HttpClient httpClient, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.githubcopilot.com/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
        request.Headers.UserAgent.ParseAdd("cortex-agent/1.0.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Openai-Intent", "conversation-edits");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var modelsResponse = JsonSerializer.Deserialize<CopilotModelsResponse>(json, JsonCatalogOptions);
        var entries = modelsResponse?.Data ?? [];

    return entries
            .Where(IsCopilotChatModel)
            .Select(e =>
            {
                // Infer publisher from model ID or vendor field
                var publisher = InferPublisher(e.Id ?? "unknown");
                return new AvailableModel
                {
                    Id = e.Id ?? "unknown",
                    Name = e.Name ?? e.Id ?? "unknown",
                    Publisher = publisher,
                    Description = e.ModelPickerDescription,
                    ContextWindow = e.Capabilities?.Limits?.MaxContextWindowTokens ?? 0,
                    MaxOutputTokens = e.Capabilities?.Limits?.MaxOutputTokens ?? 0,
                };
            })
            .OrderBy(m => m.Publisher, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Fetch available GitHub Copilot model IDs from <c>models.dev</c>.
    /// This endpoint requires no authentication, making it suitable for use
    /// before the user has completed the OAuth device flow.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sorted list of model IDs available under the <c>github-copilot</c> provider.</returns>
    public static async Task<List<string>> FetchCopilotModelsFromModelsDevAsync(
        HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        const string ModelsDevUrl = "https://models.dev/api.json";

        var json = await httpClient
            .GetStringAsync(ModelsDevUrl, cancellationToken)
            .ConfigureAwait(false);

        var root = JsonNode.Parse(json)?.AsObject();
        if (root is null || !root.ContainsKey("github-copilot"))
            return [];

        var models = root["github-copilot"]!["models"]!.AsObject();
        return [.. models.Select(m => m.Key).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Fetch models from the OpenAI models API.</summary>
    internal static async Task<List<AvailableModel>> FetchOpenAiModelsAsync(
        string apiKey, HttpClient httpClient, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var listResponse = JsonSerializer.Deserialize<OpenAiModelListResponse>(json, JsonCatalogOptions);
        var entries = listResponse?.Data ?? [];

        var chatPrefixes = new[] { "gpt-", "o1", "o3", "o4", "chatgpt-" };

        return entries
            .Where(e => e.Id is not null && chatPrefixes.Any(p =>
                e.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new AvailableModel
            {
                Id = e.Id!,
                Name = e.Id!,
                Publisher = "OpenAI",
            })
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Fetch models from the Anthropic /v1/models API.</summary>
    internal static async Task<List<AvailableModel>> FetchAnthropicModelsAsync(
        string apiKey, HttpClient httpClient, string? tokenType = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100");

        if (string.Equals(tokenType, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        }
        else
        {
            request.Headers.Add("x-api-key", apiKey);
        }

        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var listResponse = JsonSerializer.Deserialize<AnthropicModelListResponse>(json, JsonCatalogOptions);
        var entries = listResponse?.Data ?? [];

        return entries
            .Where(e => !string.IsNullOrEmpty(e.Id))
            .Select(e => new AvailableModel
            {
                Id = e.Id!,
                Name = e.DisplayName ?? e.Id!,
                Publisher = "Anthropic",
            })
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>Determine if a Copilot API model entry is a chat-capable model.</summary>
    internal static bool IsCopilotChatModel(CopilotModelEntry entry)
    {
        // Filter by capabilities — must support chat
        if (entry.Capabilities is not null)
        {
            var type = entry.Capabilities.Type;
            // Must be a "chat" type model
            if (!string.IsNullOrEmpty(type) &&
                !type.Contains("chat", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Exclude embedding-only or image-generation-only models
        var id = entry.Id ?? "";
        if (id.Contains("embedding", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("dall-e", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("tts", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("whisper", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>Infer publisher name from a Copilot model ID.</summary>
    internal static string InferPublisher(string modelId)
    {
        if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
        {
            return "Anthropic";
        }

        if (modelId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenAI";
        }

        if (modelId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "Google";
        }

        if (modelId.StartsWith("grok", StringComparison.OrdinalIgnoreCase))
        {
            return "xAI";
        }

        return "Unknown";
    }

    private static readonly JsonSerializerOptions JsonCatalogOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}

// ── DTOs ────────────────────────────────────────────────────────────

/// <summary>Setup wizard save request — one provider entry within a <see cref="MultiSetupRequest"/>.</summary>
public sealed class SetupRequest
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// API key or OAuth token. For <c>github-copilot-api</c> this is the
    /// OAuth access token obtained via the device flow.
    /// </summary>
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// GitHub OAuth App client ID (only for <c>github-copilot-api</c>).
    /// If null or empty, the built-in default is used.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    /// <summary>
    /// OAuth refresh token (only for Anthropic OAuth flow).
    /// Stored encrypted in DPAPI; never written to cortex.yml.
    /// </summary>
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    /// <summary>
    /// OAuth access-token expiry as Unix timestamp in milliseconds (0 = not OAuth).
    /// </summary>
    [JsonPropertyName("tokenExpiresAt")]
    public long TokenExpiresAt { get; set; }

    [JsonPropertyName("models")]
    public List<string> Models { get; set; } = [];

    /// <summary>
    /// When <c>true</c>, this provider already exists in the current config.
    /// The backend keeps its existing stored credentials as-is; only the model list is taken
    /// from this request if non-empty.
    /// </summary>
    /// <summary>
    /// Optional model ID for memory-related tasks (cheaper model to reduce costs).
    /// Null or empty = fall back to the default model.
    /// </summary>
    [JsonPropertyName("memoryModel")]
    public string? MemoryModel { get; set; }

    [JsonPropertyName("isExisting")]
    public bool IsExisting { get; set; }
}

/// <summary>
/// Multi-provider setup save request — replaces the single-provider <see cref="SetupRequest"/>
/// as the payload for <c>POST /api/setup/save</c>.
/// </summary>
public sealed class MultiSetupRequest
{
    /// <summary>All providers to configure (existing ones kept + new ones added).</summary>
    [JsonPropertyName("providers")]
    public List<SetupRequest> Providers { get; set; } = [];

    /// <summary>
    /// Desired fallback order (provider template IDs / names, first = primary).
    /// Matches the drag-to-reorder result from the review step.
    /// </summary>
    [JsonPropertyName("fallbackOrder")]
    public List<string> FallbackOrder { get; set; } = [];
}

/// <summary>Fetch models request.</summary>
public sealed class FetchModelsRequest
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>API key or OAuth token for authentication.</summary>
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Token type hint: <c>"oauth"</c> for Anthropic OAuth, otherwise null / omitted.
    /// </summary>
    [JsonPropertyName("tokenType")]
    public string? TokenType { get; set; }
}

/// <summary>A model available from a provider.</summary>
public sealed class AvailableModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Total context window in tokens (0 = unknown).</summary>
    [JsonPropertyName("contextWindow")]
    public int ContextWindow { get; set; }

    /// <summary>Maximum output tokens per completion (0 = unknown).</summary>
    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; set; }
}

/// <summary>Provider template shown in the setup wizard.</summary>
public sealed class ProviderTemplate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Primary authentication method: <c>"oauth"</c> (GitHub device flow),
    /// <c>"oauth_pkce"</c> (Anthropic authorization-code + PKCE), or <c>"apikey"</c> (paste a key).
    /// </summary>
    [JsonPropertyName("authMethod")]
    public string AuthMethod { get; set; } = "apikey";

    /// <summary>
    /// When <c>true</c>, the provider also supports OAuth login (in addition to API key).
    /// The setup wizard shows an auth-method toggle for the user to choose.
    /// </summary>
    [JsonPropertyName("supportsOAuth")]
    public bool SupportsOAuth { get; set; }

    [JsonPropertyName("apiKeyLabel")]
    public string ApiKeyLabel { get; set; } = "API Key";

    [JsonPropertyName("apiKeyPlaceholder")]
    public string ApiKeyPlaceholder { get; set; } = string.Empty;
}

// ── Copilot OAuth DTOs ─────────────────────────────────────────────

/// <summary>Response from GitHub's <c>/login/device/code</c> endpoint.</summary>
internal sealed class GitHubDeviceCodeResponse
{
    public string? DeviceCode { get; set; }
    public string? UserCode { get; set; }
    public string? VerificationUri { get; set; }
    public int ExpiresIn { get; set; }
    public int Interval { get; set; }
}

/// <summary>Response from GitHub's <c>/login/oauth/access_token</c> endpoint.</summary>
internal sealed class GitHubAccessTokenResponse
{
    public string? AccessToken { get; set; }
    public string? TokenType { get; set; }
    public string? Scope { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }
    public int? Interval { get; set; }
}

/// <summary>Result of initiating the Copilot OAuth device flow.</summary>
public sealed class CopilotDeviceFlowResponse
{
    [JsonPropertyName("deviceCode")]
    public required string DeviceCode { get; init; }

    [JsonPropertyName("userCode")]
    public required string UserCode { get; init; }

    [JsonPropertyName("verificationUri")]
    public required string VerificationUri { get; init; }

    [JsonPropertyName("expiresInSeconds")]
    public int ExpiresInSeconds { get; init; }

    [JsonPropertyName("pollingIntervalSeconds")]
    public int PollingIntervalSeconds { get; init; }
}

/// <summary>Result of polling the Copilot OAuth token endpoint.</summary>
public sealed class CopilotPollResult
{
    /// <summary>
    /// Status: <c>"success"</c>, <c>"pending"</c>, <c>"expired"</c>, <c>"denied"</c>, or <c>"failed"</c>.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>OAuth access token (only set when <see cref="Status"/> is <c>"success"</c>).</summary>
    [JsonPropertyName("accessToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccessToken { get; init; }

    /// <summary>Suggested retry delay in seconds (only set on <c>"slow_down"</c>).</summary>
    [JsonPropertyName("retryAfterSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RetryAfterSeconds { get; init; }
}

/// <summary>Request to poll the Copilot OAuth device flow.</summary>
public sealed class CopilotPollRequest
{
    [JsonPropertyName("deviceCode")]
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>
    /// GitHub OAuth App client ID. Must match the one used to initiate the flow.
    /// If null or empty, the built-in default is used.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }
}

/// <summary>Request to initiate the Copilot OAuth device flow.</summary>
public sealed class CopilotAuthRequest
{
    /// <summary>
    /// GitHub OAuth App client ID. If null or empty, the built-in default is used.
    /// Users can register their own OAuth App at https://github.com/settings/developers.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }
}

// ── Copilot /models API DTOs ───────────────────────────────────────

/// <summary>Response from <c>api.githubcopilot.com/models</c>.</summary>
internal sealed class CopilotModelsResponse
{
    public List<CopilotModelEntry>? Data { get; set; }
}

/// <summary>A single model entry from the Copilot /models endpoint.</summary>
internal sealed class CopilotModelEntry
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Vendor { get; set; }
    public string? ModelPickerDescription { get; set; }
    public CopilotModelCapabilities? Capabilities { get; set; }
}

/// <summary>Capabilities section of a Copilot model entry.</summary>
internal sealed class CopilotModelCapabilities
{
    public string? Family { get; set; }
    public string? Type { get; set; }
    public CopilotModelLimits? Limits { get; set; }
}

/// <summary>Token limits reported by the Copilot /models endpoint.</summary>
internal sealed class CopilotModelLimits
{
    public int? MaxContextWindowTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public int? MaxPromptTokens { get; set; }
}

// ── Other Provider DTOs ────────────────────────────────────────────

/// <summary>OpenAI model entry.</summary>
public sealed class OpenAiModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

/// <summary>OpenAI /v1/models response wrapper.</summary>
public sealed class OpenAiModelListResponse
{
    [JsonPropertyName("data")]
    public List<OpenAiModelEntry>? Data { get; set; }
}

/// <summary>Anthropic /v1/models response wrapper.</summary>
public sealed class AnthropicModelListResponse
{
    [JsonPropertyName("data")]
    public List<AnthropicModelEntry>? Data { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }
}

/// <summary>Anthropic model entry.</summary>
public sealed class AnthropicModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

// ── Anthropic OAuth DTOs ───────────────────────────────────────────

/// <summary>
/// Response from <c>console.anthropic.com/v1/oauth/token</c>
/// for both authorization-code exchange and token-refresh flows.
/// </summary>
// AnthropicOAuthTokenResponse lives in Cortex.Contained.Contracts.Auth

/// <summary>Response from <c>POST /api/setup/anthropic-auth</c>.</summary>
public sealed class AnthropicAuthInitiateResponse
{
    /// <summary>The URL the user must open in their browser.</summary>
    [JsonPropertyName("authUrl")]
    public required string AuthUrl { get; init; }

    /// <summary>
    /// PKCE code verifier — must be kept server-side (in the caller's session)
    /// and sent back when exchanging the code.
    /// </summary>
    [JsonPropertyName("codeVerifier")]
    public required string CodeVerifier { get; init; }
}

/// <summary>Request body for <c>POST /api/setup/anthropic-exchange</c>.</summary>
public sealed class AnthropicExchangeRequest
{
    /// <summary>Authorization code pasted by the user from the Anthropic callback page.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>PKCE code verifier generated in the previous step.</summary>
    [JsonPropertyName("codeVerifier")]
    public string CodeVerifier { get; set; } = string.Empty;
}

/// <summary>Request to poll for device code approval.</summary>
public sealed class AnthropicDevicePollRequest
{
    [JsonPropertyName("deviceCode")]
    public string DeviceCode { get; set; } = string.Empty;
}

/// <summary>Request to save a setup token from `claude setup-token`.</summary>
public sealed class AnthropicSetupTokenRequest
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

/// <summary>Settings update request from the settings page.</summary>
public sealed class SettingsUpdateRequest
{
    [JsonPropertyName("fallbackOrder")]
    public List<string>? FallbackOrder { get; set; }

    /// <summary>
    /// Per-provider model list updates. Key = provider name, value = list of model IDs.
    /// Processed before default/memory model updates so new models are available for selection.
    /// </summary>
    [JsonPropertyName("providerModels")]
    public Dictionary<string, List<string>>? ProviderModels { get; set; }

    /// <summary>
    /// Per-provider default model updates. Key = provider name, value = model ID.
    /// </summary>
    [JsonPropertyName("providerDefaultModels")]
    public Dictionary<string, string>? ProviderDefaultModels { get; set; }

    /// <summary>
    /// Per-provider memory model updates. Key = provider name, value = model ID (empty string to clear).
    /// </summary>
    [JsonPropertyName("providerMemoryModels")]
    public Dictionary<string, string>? ProviderMemoryModels { get; set; }

    /// <summary>
    /// Maximum tool-call rounds for sub-agents (0 = default 200). Null = no change.
    /// </summary>
    [JsonPropertyName("maxSubagentRounds")]
    public int? MaxSubagentRounds { get; set; }
}

/// <summary>Request to refresh available models for an already-configured provider.</summary>
public sealed class RefreshModelsRequest
{
    [JsonPropertyName("providerName")]
    public string ProviderName { get; set; } = string.Empty;
}

/// <summary>Channel update request from the settings page.</summary>
public sealed class ChannelUpdateRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("settings")]
    public Dictionary<string, string>? Settings { get; set; }
}

/// <summary>TTS voice change request from the settings page.</summary>
public sealed class VoiceChangeRequest
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }
}

/// <summary>STT language change request from the settings page.</summary>
public sealed class SttLanguageChangeRequest
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }
}

/// <summary>TTS engine change request from the settings page.</summary>
public sealed class TtsEngineChangeRequest
{
    [JsonPropertyName("engine")]
    public string? Engine { get; set; }
}

/// <summary>Master/STT/TTS enable-toggle request from the settings page.</summary>
public sealed class SpeechTogglesRequest
{
    [JsonPropertyName("speechEnabled")]
    public bool? SpeechEnabled { get; set; }

    [JsonPropertyName("sttEnabled")]
    public bool? SttEnabled { get; set; }

    [JsonPropertyName("ttsEnabled")]
    public bool? TtsEnabled { get; set; }
}

/// <summary>Language voice configuration save request.</summary>
public sealed class LanguageConfigRequest
{
    [JsonPropertyName("defaultLanguage")]
    public string? DefaultLanguage { get; set; }

    [JsonPropertyName("languages")]
    public Dictionary<string, LanguageVoiceEntry>? Languages { get; set; }
}

/// <summary>Voice references for a single language.</summary>
public sealed class LanguageVoiceEntry
{
    [JsonPropertyName("maleVoice")]
    public required string MaleVoice { get; set; }

    [JsonPropertyName("femaleVoice")]
    public required string FemaleVoice { get; set; }
}

/// <summary>Password request for auth setup and login.</summary>
public sealed class PasswordRequest
{
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>Change password request.</summary>
public sealed class ChangePasswordRequest
{
    [JsonPropertyName("currentPassword")]
    public string CurrentPassword { get; set; } = string.Empty;

    [JsonPropertyName("newPassword")]
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>Body for PUT /api/memory/{memoryId} (memoryId comes from the route).</summary>
public sealed class MemoryUpdateBody
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}
