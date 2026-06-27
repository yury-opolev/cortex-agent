using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Deletes a named todo list.
/// </summary>
public sealed partial class TodosDeleteTool : IAgentTool
{
    private readonly TodoStoreResolver resolver;
    private readonly ILogger<TodosDeleteTool> logger;

    public TodosDeleteTool(TodoStoreResolver resolver, ILogger<TodosDeleteTool> logger)
    {
        this.resolver = resolver;
        this.logger = logger;
    }

    public string Name => "todos_delete";

    public string Description =>
        "Delete a todo list by name. Use when a plan is complete and no longer needed.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "Name of the todo list to delete"
            }
          },
          "required": ["name"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string name;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            name = doc.RootElement.GetProperty("name").GetString() ?? string.Empty;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid arguments: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: name"));
        }

        var store = this.resolver.Resolve(context.ConversationId);
        var deleted = store.Delete(context.ConversationId, name);

        if (deleted)
        {
            this.LogTodosDeleted(name, context.ConversationId);
            return Task.FromResult(AgentToolResult.Ok($"Todo list \"{name}\" deleted."));
        }

        return Task.FromResult(AgentToolResult.Fail($"No todo list found with name \"{name}\"."));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[todos_delete] \"{Name}\" for {ConversationId}")]
    private partial void LogTodosDeleted(string name, string conversationId);
}
