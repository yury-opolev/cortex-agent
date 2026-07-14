using Cortex.Contained.Bridge.Mcp.Auth;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// An MCP server reached over remote HTTP (streamable-HTTP/SSE). Auth is resolved on the host and
/// attached here; the agent never sees it. Static auth (none/apiKey) rides fixed
/// <see cref="headers"/>; OAuth rides an <see cref="IMcpBearerSource"/> that attaches a freshly-valid
/// bearer per request and transparently refreshes-and-retries on a <c>401</c>.
/// </summary>
public sealed class HttpMcpServerConnection : McpServerConnectionBase
{
    private readonly Uri endpoint;
    private readonly IReadOnlyDictionary<string, string> headers;
    private readonly IMcpBearerSource? bearerSource;

    public HttpMcpServerConnection(
        string serverKey,
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyCollection<string> toolAllowList,
        ILogger<HttpMcpServerConnection> logger,
        IReadOnlyCollection<string> mutationToolAllowList,
        int callTimeoutSeconds = McpServerConfig.DefaultCallTimeoutSeconds,
        int maxResultBytes = McpResultMapper.DefaultMaxResultBytes)
        : base(serverKey, toolAllowList, mutationToolAllowList, logger, callTimeoutSeconds, maxResultBytes)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        this.endpoint = endpoint;
        this.headers = headers ?? new Dictionary<string, string>();
        this.bearerSource = null;
    }

    public HttpMcpServerConnection(
        string serverKey,
        Uri endpoint,
        IMcpBearerSource bearerSource,
        IReadOnlyCollection<string> toolAllowList,
        ILogger<HttpMcpServerConnection> logger,
        IReadOnlyCollection<string> mutationToolAllowList,
        int callTimeoutSeconds = McpServerConfig.DefaultCallTimeoutSeconds,
        int maxResultBytes = McpResultMapper.DefaultMaxResultBytes)
        : base(serverKey, toolAllowList, mutationToolAllowList, logger, callTimeoutSeconds, maxResultBytes)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(bearerSource);
        this.endpoint = endpoint;
        this.headers = new Dictionary<string, string>();
        this.bearerSource = bearerSource;
    }

    protected override IClientTransport CreateTransport()
    {
        var options = new HttpClientTransportOptions
        {
            Name = this.ServerKey,
            Endpoint = this.endpoint,
        };

        // OAuth: a refreshing HttpClient injects the bearer + retries on 401 (host-side).
        if (this.bearerSource is not null)
        {
            var httpClient = new HttpClient(new McpOAuthRefreshHandler(this.bearerSource) { InnerHandler = new HttpClientHandler() });
            return new HttpClientTransport(options, httpClient, loggerFactory: null, ownsHttpClient: true);
        }

        if (this.headers.Count > 0)
        {
            options.AdditionalHeaders = this.headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        }

        return new HttpClientTransport(options);
    }
}
