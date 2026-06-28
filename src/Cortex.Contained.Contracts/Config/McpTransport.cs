namespace Cortex.Contained.Contracts.Config;

/// <summary>Transport used to reach an MCP server.</summary>
public enum McpTransport
{
    /// <summary>Local child process speaking MCP over stdio.</summary>
    Stdio,

    /// <summary>Remote HTTP/SSE (streamable-HTTP) endpoint.</summary>
    Http,
}
