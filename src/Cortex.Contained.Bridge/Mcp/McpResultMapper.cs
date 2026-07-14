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
    /// <summary>
    /// Default UTF-8 byte cap applied when a caller does not supply one — mirrors
    /// <see cref="Cortex.Contained.Contracts.Config.McpServerConfig.MaxResultBytes"/>'s default.
    /// </summary>
    public const int DefaultMaxResultBytes = 50 * 1024;

    /// <summary>Deterministic marker appended when a result was cut off at the byte limit.</summary>
    public const string TruncationMarker = "\n[MCP result truncated: exceeded the configured size limit]";

    /// <summary>
    /// Flattens a sequence of content blocks to a single string for the agent tool result.
    /// Builds INCREMENTALLY and STOPS as soon as the next block would push the UTF-8 byte count
    /// past <paramref name="maxResultBytes"/>, appending <see cref="TruncationMarker"/> — so an
    /// oversized MCP result is bounded before it ever crosses the Bridge→Agent SignalR hub.
    /// </summary>
    public static string FlattenContent(IEnumerable<ContentBlock>? content, int maxResultBytes = DefaultMaxResultBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxResultBytes, 0);

        if (content is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var usedBytes = 0;
        var truncated = false;

        foreach (var block in content)
        {
            var separator = builder.Length > 0 ? "\n" : string.Empty;
            var text = block switch
            {
                TextContentBlock textBlock => textBlock.Text,
                _ => $"[{block.Type} content]",
            };
            var piece = separator + text;
            var pieceBytes = Encoding.UTF8.GetByteCount(piece);

            if (usedBytes + pieceBytes > maxResultBytes)
            {
                var remaining = maxResultBytes - usedBytes;
                if (remaining > 0)
                {
                    builder.Append(TruncateToUtf8ByteLimit(piece, remaining));
                }

                truncated = true;
                break;
            }

            builder.Append(piece);
            usedBytes += pieceBytes;
        }

        if (truncated)
        {
            builder.Append(TruncationMarker);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Cuts <paramref name="text"/> to fit within <paramref name="maxBytes"/> UTF-8 bytes without
    /// ever splitting a multi-byte character or surrogate pair mid-sequence.
    /// </summary>
    private static string TruncateToUtf8ByteLimit(string text, int maxBytes)
    {
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (used + runeBytes > maxBytes)
            {
                break;
            }

            builder.Append(rune);
            used += runeBytes;
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
    public static McpToolResult ToToolResult(string invocationId, CallToolResult result, int maxResultBytes = DefaultMaxResultBytes)
    {
        ArgumentNullException.ThrowIfNull(result);

        var flattened = FlattenContent(result.Content, maxResultBytes);
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
