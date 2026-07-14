namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Builds the agent-facing failure text for an MCP tool call. Deliberately carries no exception
/// detail: a raw <c>ex.Message</c> can contain the endpoint URL (possibly with inline credentials),
/// stack fragments, or other host-side information that must never reach the credential-free agent.
/// The full error is logged host-side; the agent only sees this generic message.
/// </summary>
public static class McpErrorSanitizer
{
    /// <summary>Generic, secret-free failure message for a tool call on a server.</summary>
    public static string ToolFailure(string serverKey, string toolName)
    {
        return $"MCP tool '{toolName}' on server '{serverKey}' failed.";
    }

    /// <summary>
    /// Generic, secret-free transport-failure message for the admin-facing
    /// <see cref="McpServerConnectionBase.LastError"/> / <see cref="McpServerView.LastError"/>
    /// field. Carries only the exception TYPE, never <c>ex.Message</c> — a raw message can embed
    /// endpoint URLs (possibly with inline credentials), stack fragments, or fragments of an
    /// untrusted MCP process's own output.
    /// </summary>
    public static string TransportFailure(string serverKey, string toolName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return $"MCP server '{serverKey}' transport failed during '{toolName}' ({exception.GetType().Name}).";
    }

    /// <summary>
    /// Generic, secret-free connect-failure message for the admin-facing
    /// <see cref="McpServerConnectionBase.LastError"/> / <see cref="McpServerView.LastError"/>
    /// field. Carries only the exception TYPE, never <c>ex.Message</c> — a connect failure (e.g. a
    /// misconfigured HTTP/stdio endpoint) can embed a connection-string secret.
    /// </summary>
    public static string ConnectFailure(string serverKey, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return $"MCP server '{serverKey}' failed to connect ({exception.GetType().Name}).";
    }
}
