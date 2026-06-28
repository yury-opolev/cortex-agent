using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// An MCP server reached over remote HTTP (streamable-HTTP/SSE). Auth headers (e.g.
/// <c>Authorization: Bearer …</c>) are resolved on the host and attached here; the agent never
/// sees them.
/// </summary>
public sealed class HttpMcpServerConnection : McpServerConnectionBase
{
    private readonly Uri endpoint;
    private readonly IReadOnlyDictionary<string, string> headers;

    public HttpMcpServerConnection(
        string serverKey,
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyCollection<string> toolAllowList,
        ILogger<HttpMcpServerConnection> logger)
        : base(serverKey, toolAllowList, logger)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        this.endpoint = endpoint;
        this.headers = headers ?? new Dictionary<string, string>();
    }

    protected override IClientTransport CreateTransport()
    {
        var options = new HttpClientTransportOptions
        {
            Name = this.ServerKey,
            Endpoint = this.endpoint,
        };

        if (this.headers.Count > 0)
        {
            options.AdditionalHeaders = this.headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        }

        return new HttpClientTransport(options);
    }
}
