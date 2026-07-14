using System.Text;
using Cortex.Contained.Contracts.Hub;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Pure mapping from the SDK's <see cref="CallToolResult"/> content blocks to the agent-facing
/// <see cref="McpToolResult"/>. Text blocks are concatenated; non-text blocks are described by a
/// short placeholder (image/audio/binary mapping to the agent's media path is future work).
/// </summary>
public static class McpResultMapper
{
    /// <summary>Flattens a sequence of content blocks to a single string for the agent tool result.</summary>
    public static string FlattenContent(IEnumerable<ContentBlock>? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var block in content)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            switch (block)
            {
                case TextContentBlock text:
                    builder.Append(text.Text);
                    break;
                default:
                    builder.Append('[').Append(block.Type).Append(" content]");
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Maps a <see cref="CallToolResult"/> to a <see cref="McpToolResult"/> answering
    /// <paramref name="invocationId"/>. An MCP-level tool error (<see cref="CallToolResult.IsError"/>)
    /// is a DEFINITIVE failure — the server received the call and reported the error itself — and
    /// becomes a structured <see cref="McpFailureKind.Tool"/> failure carrying the flattened
    /// content as the error text; a success carries the flattened content.
    /// </summary>
    public static McpToolResult ToToolResult(string invocationId, CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var flattened = FlattenContent(result.Content);
        if (result.IsError == true)
        {
            return McpToolResult.Fail(
                invocationId,
                McpFailureKind.Tool,
                flattened.Length > 0 ? flattened : "MCP tool reported an error.");
        }

        return McpToolResult.Ok(invocationId, flattened);
    }

    /// <summary>
    /// Maps an <see cref="McpException"/> thrown AFTER a <c>tools/call</c> was dispatched to the
    /// agent-facing <see cref="McpToolResult"/> answering <paramref name="invocationId"/>.
    /// <para>
    /// SAFETY: a <see cref="McpProtocolException"/> is the SDK's carrier for a JSON-RPC error
    /// <em>response</em> from the server — the request reached the server and was rejected at the
    /// protocol layer (unknown tool, invalid params, method not found, internal error) BEFORE the
    /// tool executed, so the side effect definitively did NOT occur. That is a definitive
    /// <see cref="McpFailureKind.Tool"/> failure the agent may deliberately retry.
    /// </para>
    /// <para>
    /// EVERY OTHER <see cref="McpException"/> (an HTTP session terminated / expired, a POST that
    /// completed without a reply, an invalid response type, or any other transport/protocol-level
    /// fault) is AMBIGUOUS: the request already left the Bridge and MAY have executed. It maps to
    /// <see cref="McpToolOutcome.OutcomeUnknown"/> so it is never auto-retried — re-issuing a
    /// mutating call could double-execute its side effect.
    /// </para>
    /// </summary>
    public static McpToolResult FromCallException(
        string invocationId, string serverKey, string toolName, McpException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is McpProtocolException)
        {
            return McpToolResult.Fail(
                invocationId, McpFailureKind.Tool, McpErrorSanitizer.ToolFailure(serverKey, toolName));
        }

        return McpToolResult.Unknown(
            invocationId,
            McpFailureKind.Transport,
            $"MCP server '{serverKey}' fault mid-call for '{toolName}'; the invocation may still have executed.");
    }
}
