using System.Text;
using Cortex.Contained.Contracts.Hub;
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
    /// Maps a <see cref="CallToolResult"/> to a <see cref="McpToolResult"/>. An MCP-level tool
    /// error (<see cref="CallToolResult.IsError"/>) becomes a structured failure carrying the
    /// flattened content as the error text; a success carries the flattened content.
    /// </summary>
    public static McpToolResult ToToolResult(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var flattened = FlattenContent(result.Content);
        if (result.IsError == true)
        {
            return McpToolResult.Fail(flattened.Length > 0 ? flattened : "MCP tool reported an error.");
        }

        return McpToolResult.Ok(flattened);
    }
}
