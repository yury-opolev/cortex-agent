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
}
