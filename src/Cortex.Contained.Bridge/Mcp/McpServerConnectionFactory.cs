using Cortex.Contained.Bridge.Mcp.Auth;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Production <see cref="IMcpServerConnectionFactory"/>: resolves host-side auth and builds the
/// matching stdio/http connection. Static auth (none/apiKey/stdio env) flows through
/// <see cref="IMcpAuthManager"/>; HTTP OAuth flows through <see cref="IMcpOAuthManager"/> — a server
/// with valid/refreshable tokens gets a refreshing bearer connection, one without yields null
/// (needs interactive login). Servers needing login or missing transport fields are skipped.
/// </summary>
public sealed partial class McpServerConnectionFactory : IMcpServerConnectionFactory
{
    private readonly IMcpAuthManager authManager;
    private readonly IMcpOAuthManager? oauthManager;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<McpServerConnectionFactory> logger;

    public McpServerConnectionFactory(
        IMcpAuthManager authManager,
        ILoggerFactory loggerFactory,
        ILogger<McpServerConnectionFactory> logger)
        : this(authManager, oauthManager: null, loggerFactory, logger)
    {
    }

    public McpServerConnectionFactory(
        IMcpAuthManager authManager,
        IMcpOAuthManager? oauthManager,
        ILoggerFactory loggerFactory,
        ILogger<McpServerConnectionFactory> logger)
    {
        this.authManager = authManager;
        this.oauthManager = oauthManager;
        this.loggerFactory = loggerFactory;
        this.logger = logger;
    }

    public IMcpServerConnection? TryCreate(McpServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (!McpToolNamer.IsValidServerKey(server.Key.ToLowerInvariant()))
        {
            this.LogInvalidKey(server.Key);
            return null;
        }

        return server.Transport == McpTransport.Stdio
            ? this.CreateStdio(server)
            : this.CreateHttp(server);
    }

    private StdioMcpServerConnection? CreateStdio(McpServerConfig server)
    {
        var auth = this.authManager.Resolve(server);
        if (auth.NeedsAuth)
        {
            this.LogNeedsAuth(server.Key, auth.NeedsAuthReason ?? "unknown");
            return null;
        }

        if (string.IsNullOrWhiteSpace(server.Command))
        {
            this.LogInvalidConfig(server.Key, "stdio transport requires a command");
            return null;
        }

        return new StdioMcpServerConnection(
            server.Key.ToLowerInvariant(),
            server.Command,
            server.Args,
            auth.EnvironmentVariables,
            server.ToolAllowList,
            this.loggerFactory.CreateLogger<StdioMcpServerConnection>());
    }

    private HttpMcpServerConnection? CreateHttp(McpServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.Url) || !Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint))
        {
            this.LogInvalidConfig(server.Key, "http transport requires a valid absolute url");
            return null;
        }

        var serverKey = server.Key.ToLowerInvariant();

        // OAuth path: explicit oauth, or auto once the user has connected it (tokens present).
        var usesOAuth = this.oauthManager is not null
            && (server.Auth == McpAuthMode.OAuth || (server.Auth == McpAuthMode.Auto && this.oauthManager.HasTokens(server)));
        if (usesOAuth)
        {
            // SECURITY: never send an OAuth bearer token in cleartext to a non-local host
            // (mirror the static apiKey path's guard).
            if (Auth.McpUrlSecurity.IsInsecureForCredentials(server.Url))
            {
                this.LogNeedsAuth(server.Key, "refusing to send oauth token over plaintext http to a non-local host; use https");
                return null;
            }

            if (!this.oauthManager!.HasTokens(server))
            {
                // OAuth selected but not yet connected — surface a real "connect" signal.
                this.LogNeedsAuth(server.Key, "oauth: connect this server to authorize");
                return null;
            }

            var bearerSource = new McpOAuthBearerSource(this.oauthManager, server);
            return new HttpMcpServerConnection(
                serverKey,
                endpoint,
                bearerSource,
                server.ToolAllowList,
                this.loggerFactory.CreateLogger<HttpMcpServerConnection>());
        }

        // Static path: none / apiKey / auto-public.
        var auth = this.authManager.Resolve(server);
        if (auth.NeedsAuth)
        {
            this.LogNeedsAuth(server.Key, auth.NeedsAuthReason ?? "unknown");
            return null;
        }

        return new HttpMcpServerConnection(
            serverKey,
            endpoint,
            auth.Headers,
            server.ToolAllowList,
            this.loggerFactory.CreateLogger<HttpMcpServerConnection>());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}' has an invalid key — skipping")]
    private partial void LogInvalidKey(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}' needs interactive login ({Reason}) — skipping for now")]
    private partial void LogNeedsAuth(string serverKey, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}' has invalid config — skipping: {Reason}")]
    private partial void LogInvalidConfig(string serverKey, string reason);
}
