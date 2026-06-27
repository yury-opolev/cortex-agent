using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Read or update the context bootstrap file (context-bootstrap.md).
/// This file is loaded into the system prompt at the start of every conversation
/// and contains essential context about the user. Stored outside the sandbox
/// so it is only accessible through this tool, not via file_read/file_write.
/// </summary>
internal sealed class ContextBootstrapTool : IAgentTool
{
    private const int MaxContentLength = 2000;

    private readonly string filePath;

    public ContextBootstrapTool(string filePath)
    {
        this.filePath = Path.GetFullPath(filePath);
    }

    public string Name => "context_bootstrap_update";

    public string Description =>
        "Update the context bootstrap. Its current content is always visible in the \"Context bootstrap\" section " +
        "of your system prompt. It is loaded at every conversation start so you always know who you are talking to. " +
        "This should contain only stable, rarely-changing essentials: user name, role, location, and key preferences. " +
        "Do NOT put frequently changing information here — that belongs in long-term memory. " +
        "You can include sample memory search queries as references to help yourself find deeper context " +
        "(e.g. \"search 'user work projects' for details\"). " +
        "Only update when core user identity changes that contradict existing bootstrap data. " +
        "Keep it minimal (under 2000 characters).";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "content": {
              "type": "string",
              "description": "New content for the bootstrap context. Replaces the current content entirely."
            }
          },
          "required": ["content"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("content", out var contentElement))
            {
                return AgentToolResult.Fail("Missing required parameter: content");
            }

            var newContent = contentElement.GetString()?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(newContent))
            {
                return AgentToolResult.Fail("Content cannot be empty. The bootstrap must always contain at least the user's basic identity.");
            }

            if (newContent.Length > MaxContentLength)
            {
                return AgentToolResult.Fail($"Content too long ({newContent.Length} characters). Maximum is {MaxContentLength} characters. Summarize and try again.");
            }

            var dir = Path.GetDirectoryName(this.filePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(this.filePath, newContent, cancellationToken).ConfigureAwait(false);
            return AgentToolResult.Ok($"Context bootstrap updated ({newContent.Length} characters).");
        }
        catch (JsonException ex)
        {
            return AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}");
        }
    }
}
