using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Creates or replaces a named todo list. Uses markdown checkbox format.
/// Main agent can have multiple named lists; subagents get one list max.
/// </summary>
public sealed partial class TodosWriteTool : IAgentTool
{
    private readonly TodoStoreResolver resolver;
    private readonly ILogger<TodosWriteTool> logger;

    public TodosWriteTool(TodoStoreResolver resolver, ILogger<TodosWriteTool> logger)
    {
        this.resolver = resolver;
        this.logger = logger;
    }

    public string Name => "todos_write";

    public string Description =>
        "Create or update a named todo list for tracking multi-step work. " +
        "Use markdown checkboxes: - [ ] pending, - [-] in progress, - [x] completed, - [~] skipped. " +
        "Use for tasks with 3+ steps, especially when delegating to subagents. " +
        "Each call replaces the entire list.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "Name of the todo list (e.g. 'API migration'). Required for multiple lists."
            },
            "todos": {
              "type": "string",
              "description": "Markdown checkbox list. Example:\\n- [ ] Research\\n- [-] Build (in progress)\\n- [x] Test (done)"
            }
          },
          "required": ["todos"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string name;
        string todos;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            name = root.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString() ?? "default"
                : "default";

            todos = root.GetProperty("todos").GetString() ?? string.Empty;
        }
#pragma warning disable CA1031 // Bad arguments should not crash the agent
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid arguments: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "default";
        }

        if (string.IsNullOrWhiteSpace(todos))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: todos (markdown checkbox list)"));
        }

        var items = TodoParser.Parse(todos);
        if (items.Count == 0)
        {
            return Task.FromResult(AgentToolResult.Fail("No valid checkbox items found. Use format: - [ ] Description"));
        }

        var store = this.resolver.Resolve(context.ConversationId);
        store.Write(context.ConversationId, name, todos);

        var summary = TodoParser.Summarize(name, items);
        this.LogTodosWritten(name, context.ConversationId, items.Count);

        return Task.FromResult(AgentToolResult.Ok($"Todo list \"{name}\" updated: {summary.DoneCount}/{summary.TotalCount} done."));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[todos_write] \"{Name}\" for {ConversationId}: {ItemCount} items")]
    private partial void LogTodosWritten(string name, string conversationId, int itemCount);
}
