using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Static (non-OAuth) auth resolution: <c>none</c>, stdio env-secret injection, and http
/// <c>apiKey</c> header injection. OAuth (<see cref="McpAuthMode.OAuth"/>, and HTTP
/// <see cref="McpAuthMode.Auto"/> that 401s) is deferred to a later phase — those return a
/// "needs login" signal here. Secret <em>values</em> are resolved from DPAPI and never logged.
/// </summary>
public sealed partial class McpStaticAuth : IMcpAuthManager
{
    private const string DefaultAuthorizationHeader = "Authorization";

    private readonly IMcpSecretResolver secretResolver;
    private readonly ILogger<McpStaticAuth> logger;

    public McpStaticAuth(IMcpSecretResolver secretResolver, ILogger<McpStaticAuth> logger)
    {
        this.secretResolver = secretResolver;
        this.logger = logger;
    }

    public McpResolvedAuth Resolve(McpServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.Transport == McpTransport.Stdio
            ? this.ResolveStdio(server)
            : this.ResolveHttp(server);
    }

    private McpResolvedAuth ResolveStdio(McpServerConfig server)
    {
        // stdio carries no auth in the protocol — secrets ride env values as ${secret:id} tokens.
        if (server.Env.Count == 0)
        {
            return McpResolvedAuth.None;
        }

        var resolved = new Dictionary<string, string>(server.Env.Count, StringComparer.Ordinal);
        foreach (var (name, rawValue) in server.Env)
        {
            if (McpSecretRef.TryParse(rawValue, out var secretId))
            {
                var secret = this.secretResolver.GetSecret(secretId);
                if (string.IsNullOrEmpty(secret))
                {
                    this.LogMissingSecret(server.Key, name);
                    resolved[name] = string.Empty;
                }
                else
                {
                    this.LogSecretInjected(server.Key, name);
                    resolved[name] = secret;
                }
            }
            else
            {
                resolved[name] = rawValue;
            }
        }

        return new McpResolvedAuth { EnvironmentVariables = resolved };
    }

    private McpResolvedAuth ResolveHttp(McpServerConfig server)
    {
        switch (server.Auth)
        {
            case McpAuthMode.None:
            case McpAuthMode.Auto:
                // Phase 2: Auto attempts an unauthenticated connection (public). OAuth discovery is a later phase.
                return McpResolvedAuth.None;

            case McpAuthMode.OAuth:
                this.LogOAuthNotConfigured(server.Key);
                return McpResolvedAuth.RequiresLogin("oauth not yet configured");

            case McpAuthMode.ApiKey:
                return this.ResolveHttpApiKey(server);

            default:
                return McpResolvedAuth.None;
        }
    }

    private McpResolvedAuth ResolveHttpApiKey(McpServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.SecretRef))
        {
            this.LogMissingSecretRef(server.Key);
            return McpResolvedAuth.RequiresLogin("api key not configured (no secretRef)");
        }

        // SECURITY: never transmit a credential in cleartext to a non-local host.
        if (McpUrlSecurity.IsInsecureForCredentials(server.Url))
        {
            this.LogInsecureUrlForCredentials(server.Key);
            return McpResolvedAuth.RequiresLogin("refusing to send api key over plaintext http to a non-local host; use https");
        }

        var token = this.secretResolver.GetSecret(server.SecretRef);
        if (string.IsNullOrEmpty(token))
        {
            this.LogMissingSecret(server.Key, server.SecretRef);
            return McpResolvedAuth.RequiresLogin("api key secret not found");
        }

        var headerName = string.IsNullOrWhiteSpace(server.ApiKeyHeader)
            ? DefaultAuthorizationHeader
            : server.ApiKeyHeader;

        // The default Authorization header carries a Bearer scheme; a custom header carries the raw token.
        var headerValue = string.Equals(headerName, DefaultAuthorizationHeader, StringComparison.OrdinalIgnoreCase)
            ? $"Bearer {token}"
            : token;

        this.LogApiKeyHeaderAttached(server.Key, headerName);
        return new McpResolvedAuth
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { [headerName] = headerValue },
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}': injected env secret for '{EnvName}'")]
    private partial void LogSecretInjected(string serverKey, string envName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}': secret for env '{EnvName}' not found; injecting empty")]
    private partial void LogMissingSecret(string serverKey, string envName);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}': attached api key header '{HeaderName}'")]
    private partial void LogApiKeyHeaderAttached(string serverKey, string headerName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}': apiKey auth selected but no secretRef configured")]
    private partial void LogMissingSecretRef(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}': OAuth not yet configured — needs login")]
    private partial void LogOAuthNotConfigured(string serverKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}': refusing to attach api key over plaintext http to a non-local host (use https)")]
    private partial void LogInsecureUrlForCredentials(string serverKey);
}
