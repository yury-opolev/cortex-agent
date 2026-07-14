using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Host-side OAuth 2.1 orchestration for HTTP MCP servers: 401 → protected-resource metadata →
/// authorization-server metadata discovery, Dynamic Client Registration (RFC 7591), PKCE
/// authorization-code flow via the system browser + loopback callback, DPAPI token storage, and
/// refresh-on-expiry. Tokens, codes, verifiers, and secrets are never logged — telemetry carries
/// only the server key, the step, and the outcome.
/// </summary>
public sealed partial class McpOAuthManager : IMcpOAuthManager
{
    private const string HttpClientName = "mcp-oauth";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory httpClientFactory;
    private readonly McpTokenStore tokenStore;
    private readonly McpOAuthOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<McpOAuthManager> logger;

    // state -> pending authorization (single-use; removed on completion/expiry).
    private readonly ConcurrentDictionary<string, PendingAuthorization> pending = new(StringComparer.Ordinal);

    // serverKey -> refresh lock so concurrent invokes don't double-spend a rotating refresh token.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new(StringComparer.Ordinal);

    public McpOAuthManager(
        IHttpClientFactory httpClientFactory,
        McpTokenStore tokenStore,
        McpOAuthOptions options,
        TimeProvider timeProvider,
        ILogger<McpOAuthManager> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.tokenStore = tokenStore;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public bool HasTokens(McpServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return this.tokenStore.Get(server.Key.ToLowerInvariant()) is not null;
    }

    public void ClearTokens(string serverKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        this.tokenStore.Clear(serverKey.ToLowerInvariant());
    }

    public async Task<McpOAuthStart> BuildAuthorizationUrlAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var serverKey = server.Key.ToLowerInvariant();

        // Drop abandoned pending flows past their TTL so their in-memory PKCE verifier + client
        // secret don't linger until process exit.
        this.SweepExpiredPending();

        if (string.IsNullOrWhiteSpace(server.Url))
        {
            throw new InvalidOperationException($"MCP server '{serverKey}' has no URL to discover OAuth against.");
        }

        using var http = this.httpClientFactory.CreateClient(HttpClientName);

        var endpoints = await DiscoverEndpointsAsync(http, server.Url, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"MCP server '{serverKey}': could not discover OAuth metadata.");

        // Reuse an already-registered client across reconnects; otherwise register one via DCR.
        var existing = this.tokenStore.Get(serverKey);
        var credentials = existing is not null
            ? new McpClientCredentials { ClientId = existing.ClientId, ClientSecret = existing.ClientSecret }
            : await this.RegisterClientAsync(http, endpoints, cancellationToken).ConfigureAwait(false);

        if (credentials is null)
        {
            throw new InvalidOperationException($"MCP server '{serverKey}': no OAuth client (DCR unavailable and none configured).");
        }

        var pkce = McpPkce.Generate();
        var state = GenerateState();
        var scope = endpoints.ScopesSupported.Count > 0 ? string.Join(' ', endpoints.ScopesSupported) : null;

        this.pending[state] = new PendingAuthorization
        {
            ServerKey = serverKey,
            Verifier = pkce.Verifier,
            TokenEndpoint = endpoints.TokenEndpoint,
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret,
            Scope = scope,
            CreatedAt = this.timeProvider.GetUtcNow(),
        };

        var url = BuildAuthorizationUrl(endpoints.AuthorizationEndpoint, credentials.ClientId, this.options.RedirectUri, state, pkce.Challenge, scope, server.Url);
        this.LogAuthorizationStarted(serverKey);
        return new McpOAuthStart { AuthorizationUrl = url, State = state };
    }

    public async Task<McpOAuthCompletion> CompleteAsync(string state, string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code))
        {
            return McpOAuthCompletion.Fail("missing state or code");
        }

        // Single-use: remove regardless of validity so a state can never be replayed.
        if (!this.pending.TryRemove(state, out var request))
        {
            this.LogStateRejected("unknown");
            return McpOAuthCompletion.Fail("unknown or already-used state");
        }

        if (this.timeProvider.GetUtcNow() - request.CreatedAt > this.options.PendingTtl)
        {
            this.LogStateRejected("expired");
            return McpOAuthCompletion.Fail("authorization request expired");
        }

        try
        {
            using var http = this.httpClientFactory.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(BuildCodeExchangeForm(request, code, this.options.RedirectUri));
            var token = await PostTokenAsync(http, request.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);

            this.tokenStore.Save(request.ServerKey, new McpOAuthTokens
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresAtMs = this.ComputeExpiry(token.ExpiresInSeconds),
                ClientId = request.ClientId,
                ClientSecret = request.ClientSecret,
                TokenEndpoint = request.TokenEndpoint,
                Scope = token.Scope ?? request.Scope,
            });

            this.LogExchangeSucceeded(request.ServerKey);
            return McpOAuthCompletion.Ok(request.ServerKey);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            this.LogExchangeFailed(request.ServerKey, SafeError(ex));
            return McpOAuthCompletion.Fail("token exchange failed");
        }
    }

    public async Task<string?> GetAccessTokenAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var serverKey = server.Key.ToLowerInvariant();

        var tokens = this.tokenStore.Get(serverKey);
        if (tokens is null)
        {
            return null;
        }

        var nowMs = this.timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (!tokens.IsExpired(nowMs, (long)this.options.RefreshSkew.TotalMilliseconds))
        {
            return tokens.AccessToken;
        }

        return await this.RefreshAccessTokenAsync(serverKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> RefreshAccessTokenAsync(string serverKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        serverKey = serverKey.ToLowerInvariant();

        var gate = this.refreshLocks.GetOrAdd(serverKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tokens = this.tokenStore.Get(serverKey);
            if (tokens is null || string.IsNullOrEmpty(tokens.RefreshToken))
            {
                return null;
            }

            // Another caller may have refreshed while we waited on the lock.
            var nowMs = this.timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (!tokens.IsExpired(nowMs, (long)this.options.RefreshSkew.TotalMilliseconds))
            {
                return tokens.AccessToken;
            }

            using var http = this.httpClientFactory.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(BuildRefreshForm(tokens));
            var token = await PostTokenAsync(http, tokens.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);

            var refreshed = tokens with
            {
                AccessToken = token.AccessToken,
                RefreshToken = string.IsNullOrEmpty(token.RefreshToken) ? tokens.RefreshToken : token.RefreshToken,
                ExpiresAtMs = this.ComputeExpiry(token.ExpiresInSeconds),
                Scope = token.Scope ?? tokens.Scope,
            };
            this.tokenStore.Save(serverKey, refreshed);
            this.LogRefreshSucceeded(serverKey);
            return refreshed.AccessToken;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            this.LogRefreshFailed(serverKey, SafeError(ex));
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<McpAuthServerEndpoints?> DiscoverEndpointsAsync(HttpClient http, string serverUrl, CancellationToken cancellationToken)
    {
        // 1) Probe the resource for a 401 carrying the protected-resource metadata URL.
        var resourceMetadataUrl = await ProbeResourceMetadataUrlAsync(http, serverUrl, cancellationToken).ConfigureAwait(false)
            ?? CombineWellKnown(serverUrl, "/.well-known/oauth-protected-resource");

        // 2) Resolve the authorization server from protected-resource metadata.
        var prmJson = resourceMetadataUrl is not null
            ? await GetStringOrNullAsync(http, resourceMetadataUrl, cancellationToken).ConfigureAwait(false)
            : null;
        string? authServer = null;
        if (prmJson is not null)
        {
            var servers = McpOAuthMetadata.ParseAuthorizationServers(prmJson);
            if (servers.Count > 0)
            {
                authServer = servers[0];
            }
        }

        authServer ??= OriginOf(serverUrl);
        if (authServer is null)
        {
            return null;
        }

        // 3) Fetch AS metadata (oauth-authorization-server, then OIDC fallback).
        foreach (var wellKnown in (string[])["/.well-known/oauth-authorization-server", "/.well-known/openid-configuration"])
        {
            var metadataUrl = CombineWellKnown(authServer, wellKnown);
            if (metadataUrl is null)
            {
                continue;
            }

            var asJson = await GetStringOrNullAsync(http, metadataUrl, cancellationToken).ConfigureAwait(false);
            var endpoints = asJson is not null ? McpOAuthMetadata.ParseAuthorizationServerMetadata(asJson) : null;
            if (endpoints is not null)
            {
                return endpoints;
            }
        }

        return null;
    }

    private static async Task<string?> ProbeResourceMetadataUrlAsync(HttpClient http, string serverUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, serverUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            {
                return null;
            }

            var header = response.Headers.TryGetValues("WWW-Authenticate", out var values)
                ? string.Join(", ", values)
                : null;
            return McpOAuthMetadata.ParseResourceMetadataUrl(header);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<McpClientCredentials?> RegisterClientAsync(HttpClient http, McpAuthServerEndpoints endpoints, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoints.RegistrationEndpoint)
            || !McpUrlSecurity.IsAllowedOAuthEndpoint(endpoints.RegistrationEndpoint))
        {
            return null;
        }

        var scope = endpoints.ScopesSupported.Count > 0 ? string.Join(' ', endpoints.ScopesSupported) : null;
        var requestJson = McpDynamicClientRegistration.BuildRequestJson(this.options.ClientName, this.options.RedirectUri, scope);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri(endpoints.RegistrationEndpoint), content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return McpDynamicClientRegistration.ParseResponse(json);
    }

    private static async Task<TokenResponse> PostTokenAsync(HttpClient http, string tokenEndpoint, HttpContent content, CancellationToken cancellationToken)
    {
        // SECURITY: never POST the auth code / PKCE verifier / client secret to a non-https
        // (or non-loopback) endpoint — a server-nominated plaintext/attacker token endpoint
        // would otherwise exfiltrate them.
        if (!McpUrlSecurity.IsAllowedOAuthEndpoint(tokenEndpoint))
        {
            throw new InvalidOperationException("token endpoint must use https");
        }

        using var response = await http.PostAsync(new Uri(tokenEndpoint), content, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"token endpoint returned HTTP {(int)response.StatusCode}");
        }

        var parsed = ParseTokenResponse(json);
        if (string.IsNullOrEmpty(parsed.AccessToken))
        {
            throw new InvalidOperationException("token endpoint returned no access_token");
        }

        return parsed;
    }

    private static async Task<string?> GetStringOrNullAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        // SECURITY: discovery URLs come from server-controlled responses — never fetch a non-https
        // (or non-loopback) one. Blocks SSRF to internal/cleartext hosts (e.g. cloud metadata IPs).
        if (!McpUrlSecurity.IsAllowedOAuthEndpoint(url))
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private long ComputeExpiry(long expiresInSeconds)
    {
        return expiresInSeconds > 0
            ? this.timeProvider.GetUtcNow().ToUnixTimeMilliseconds() + (expiresInSeconds * 1000L)
            : 0L;
    }

    private static Dictionary<string, string> BuildCodeExchangeForm(PendingAuthorization request, string code, string redirectUri)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = request.ClientId,
            ["code_verifier"] = request.Verifier,
        };
        if (!string.IsNullOrEmpty(request.ClientSecret))
        {
            form["client_secret"] = request.ClientSecret;
        }

        return form;
    }

    private static Dictionary<string, string> BuildRefreshForm(McpOAuthTokens tokens)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken!,
            ["client_id"] = tokens.ClientId,
        };
        if (!string.IsNullOrEmpty(tokens.ClientSecret))
        {
            form["client_secret"] = tokens.ClientSecret;
        }

        if (!string.IsNullOrEmpty(tokens.Scope))
        {
            form["scope"] = tokens.Scope;
        }

        return form;
    }

    private static string BuildAuthorizationUrl(
        string authorizationEndpoint, string clientId, string redirectUri, string state, string codeChallenge, string? scope, string resource)
    {
        var query = new StringBuilder();
        Append(query, "response_type", "code");
        Append(query, "client_id", clientId);
        Append(query, "redirect_uri", redirectUri);
        Append(query, "state", state);
        Append(query, "code_challenge", codeChallenge);
        Append(query, "code_challenge_method", "S256");
        if (!string.IsNullOrEmpty(scope))
        {
            Append(query, "scope", scope);
        }

        Append(query, "resource", resource);

        var separator = authorizationEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{authorizationEndpoint}{separator}{query}";

        static void Append(StringBuilder builder, string key, string value)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
    }

    private static string GenerateState()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private void SweepExpiredPending()
    {
        var cutoff = this.timeProvider.GetUtcNow() - this.options.PendingTtl;
        foreach (var entry in this.pending)
        {
            if (entry.Value.CreatedAt < cutoff)
            {
                this.pending.TryRemove(entry.Key, out _);
            }
        }
    }

    // SECURITY: content-free — only the exception TYPE, consistent with the Bridge-side MCP
    // redaction guarantee (docs/security.md). A JsonException from a malformed (but 2xx)
    // token-endpoint body can echo a snippet of the response (i.e. the token); an
    // HttpRequestException/InvalidOperationException can embed the token endpoint URL. Never log
    // ex.Message for any of these — the exception type is enough to diagnose.
    private static string SafeError(Exception ex) => ex.GetType().Name;

    private static string? OriginOf(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    private static string? CombineWellKnown(string? baseUrl, string wellKnownPath)
    {
        var origin = OriginOf(baseUrl ?? string.Empty);
        return origin is null ? null : origin + wellKnownPath;
    }

    private static TokenResponse ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new TokenResponse
        {
            AccessToken = GetString(root, "access_token") ?? string.Empty,
            RefreshToken = GetString(root, "refresh_token"),
            Scope = GetString(root, "scope"),
            ExpiresInSeconds = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt64(out var seconds) ? seconds : 0L,
        };
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}': OAuth authorization started")]
    private partial void LogAuthorizationStarted(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}': OAuth code exchanged")]
    private partial void LogExchangeSucceeded(string serverKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}': OAuth code exchange failed: {Error}")]
    private partial void LogExchangeFailed(string serverKey, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}': OAuth token refreshed")]
    private partial void LogRefreshSucceeded(string serverKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}': OAuth token refresh failed: {Error}")]
    private partial void LogRefreshFailed(string serverKey, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP OAuth callback rejected: {Reason} state")]
    private partial void LogStateRejected(string reason);

    /// <summary>A pending authorization correlated by its single-use state token.</summary>
    private sealed record PendingAuthorization
    {
        public required string ServerKey { get; init; }

        public required string Verifier { get; init; }

        public required string TokenEndpoint { get; init; }

        public required string ClientId { get; init; }

        public string? ClientSecret { get; init; }

        public string? Scope { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>The relevant fields of an OAuth token-endpoint response.</summary>
    private sealed record TokenResponse
    {
        public required string AccessToken { get; init; }

        public string? RefreshToken { get; init; }

        public string? Scope { get; init; }

        public long ExpiresInSeconds { get; init; }
    }
}
