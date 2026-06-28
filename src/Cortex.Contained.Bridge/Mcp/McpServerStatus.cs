namespace Cortex.Contained.Bridge.Mcp;

/// <summary>Live status of a single MCP server connection.</summary>
public enum McpServerStatus
{
    /// <summary>Not yet connected (or disabled).</summary>
    Disconnected,

    /// <summary>Handshake in progress.</summary>
    Connecting,

    /// <summary>Connected; tools listed and callable.</summary>
    Connected,

    /// <summary>Connection or handshake failed.</summary>
    Error,

    /// <summary>Reachable but requires user authorization before use (OAuth not yet completed).</summary>
    NeedsLogin,
}
