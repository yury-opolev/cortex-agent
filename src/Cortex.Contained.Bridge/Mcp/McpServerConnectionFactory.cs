using Cortex.Contained.Bridge.Mcp.Auth;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Production <see cref="IMcpServerConnectionFactory"/>: resolves host-side auth via
/// <see cref="IMcpAuthManager"/> and builds the matching stdio/http connection. Servers needing an
/// interactive login (or missing required transport fields) yield null and are skipped.
/// </summary>
public sealed partial class McpServerConnectionFactory : IMcpServerConnectionFactory
{
    private readonly IMcpAuthManager authManager;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<McpServerConnectionFactory> logger;

    public McpServerConnectionFactory(
        IMcpAuthManager authManager,
        ILoggerFactory loggerFactory,
        ILogger<McpServerConnectionFactory> logger)
    {
        this.authManager = authManager;
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

        var auth = this.authManager.Resolve(server);
        if (auth.NeedsAuth)
        {
            this.LogNeedsAuth(server.Key, auth.NeedsAuthReason ?? "unknown");
            return null;
        }

        return server.Transport == McpTransport.Stdio
            ? this.CreateStdio(server, auth)
            : this.CreateHttp(server, auth);
    }

    private StdioMcpServerConnection? CreateStdio(McpServerConfig server, McpResolvedAuth auth)
    {
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

    private HttpMcpServerConnection? CreateHttp(McpServerConfig server, McpResolvedAuth auth)
    {
        if (string.IsNullOrWhiteSpace(server.Url) || !Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint))
        {
            this.LogInvalidConfig(server.Key, "http transport requires a valid absolute url");
            return null;
        }

        return new HttpMcpServerConnection(
            server.Key.ToLowerInvariant(),
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
