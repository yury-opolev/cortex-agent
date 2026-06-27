using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Reads the agent's self-notes — operational knowledge the agent has written
/// for itself (rules, tips, learned patterns). These are injected into the
/// system prompt on every turn.
/// </summary>
internal sealed class SelfNotesReadTool : IAgentTool
{
    private readonly SelfNotesStore store;

    public SelfNotesReadTool(SelfNotesStore store)
    {
        this.store = store;
    }

    public string Name => "self_notes_read";

    public string Description =>
        "Read your self-notes — operational knowledge you've written for yourself (rules, tips, learned patterns). " +
        "These notes are always included in your system prompt, so reading them shows you exactly what you see at the start of every conversation.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {},
          "required": []
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var content = this.store.Read();
        return Task.FromResult(AgentToolResult.Ok(content));
    }
}
