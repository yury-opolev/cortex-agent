using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Writes the agent's self-notes — fundamental operating principles.
/// Content is injected into the system prompt on every turn,
/// so changes take effect immediately on the next message.
/// Self-notes should be stable and rarely changed.
/// </summary>
internal sealed class SelfNotesWriteTool : IAgentTool
{
    private readonly SelfNotesStore store;

    public SelfNotesWriteTool(SelfNotesStore store)
    {
        this.store = store;
    }

    public string Name => "self_notes_write";

    public string Description =>
        "Write your self-notes — fundamental operating principles that govern how you work across all conversations. " +
        "Keep this concise and stable. Do NOT put user preferences, specific procedures, tool recipes, or concrete facts here — " +
        "those belong in memory (memory_ingest) or skills (file_write to skills/<name>/SKILL.md). Self-notes should rarely change. " +
        $"Maximum {SelfNotesStore.MaxCharacters} characters. Overwrites previous content entirely.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "content": {
              "type": "string",
              "description": "The full self-notes content to save. Overwrites previous content."
            }
          },
          "required": ["content"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("content", out var contentProp))
        {
            return Task.FromResult(new AgentToolResult
            {
                Success = false,
                Content = "Missing required parameter: content",
                Error = "Missing required parameter: content",
            });
        }

        var content = contentProp.GetString() ?? string.Empty;

        if (content.Length > SelfNotesStore.MaxCharacters)
        {
            return Task.FromResult(new AgentToolResult
            {
                Success = false,
                Content = $"Content too long ({content.Length} chars). Maximum is {SelfNotesStore.MaxCharacters} chars (~2000 tokens). Be more concise.",
                Error = $"Content too long ({content.Length} chars). Maximum is {SelfNotesStore.MaxCharacters} chars.",
            });
        }

        var success = this.store.Write(content);

        return Task.FromResult(new AgentToolResult
        {
            Success = success,
            Content = success
                ? "Self-notes updated. Changes take effect on the next message."
                : "Failed to write self-notes.",
            Error = success ? null : "Failed to write self-notes.",
        });
    }
}
