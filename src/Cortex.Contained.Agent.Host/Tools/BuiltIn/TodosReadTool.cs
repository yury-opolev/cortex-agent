using System.Globalization;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Reads one or all todo lists for the current conversation.
/// </summary>
public sealed partial class TodosReadTool : IAgentTool
{
    private readonly TodoStoreResolver resolver;
    private readonly ILogger<TodosReadTool> logger;

    public TodosReadTool(TodoStoreResolver resolver, ILogger<TodosReadTool> logger)
    {
        this.resolver = resolver;
        this.logger = logger;
    }

    public string Name => "todos_read";

    public string Description =>
        "Read todo lists. Without a name: returns all lists with summaries. " +
        "With a name: returns the full list with all items and their status.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "Name of a specific list to read. Omit to list all."
            }
          }
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string? name = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("name", out var nameEl))
            {
                name = nameEl.GetString();
            }
        }
#pragma warning disable CA1031
        catch { /* empty args = read all */ }
#pragma warning restore CA1031

        var store = this.resolver.Resolve(context.ConversationId);

        if (!string.IsNullOrWhiteSpace(name))
        {
            var list = store.Read(context.ConversationId, name);
            if (list is null)
            {
                this.LogTodosNotFound(name, context.ConversationId);
                return Task.FromResult(AgentToolResult.Fail($"No todo list found with name \"{name}\"."));
            }

            this.LogTodosRead(name, context.ConversationId);
            return Task.FromResult(AgentToolResult.Ok(FormatList(list)));
        }

        // Read all
        var lists = store.ReadAll(context.ConversationId);
        if (lists.Count == 0)
        {
            this.LogTodosReadAllEmpty(context.ConversationId);
            return Task.FromResult(AgentToolResult.Ok("No todo lists found."));
        }

        var sb = new StringBuilder();
        foreach (var list in lists)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
            sb.Append(FormatList(list));
        }

        this.LogTodosReadAll(context.ConversationId, lists.Count);
        return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
    }

    private static string FormatList(TodoList list)
    {
        var sb = new StringBuilder();
        var summary = TodoParser.Summarize(list.Name, list.Items);
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {list.Name} ({summary.DoneCount}/{summary.TotalCount} done)");
        sb.Append(list.Markdown);
        return sb.ToString();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "[todos_read] Read \"{Name}\" for {ConversationId}")]
    private partial void LogTodosRead(string name, string conversationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[todos_read] Not found: \"{Name}\" for {ConversationId}")]
    private partial void LogTodosNotFound(string name, string conversationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[todos_read] Read all for {ConversationId}: {Count} lists")]
    private partial void LogTodosReadAll(string conversationId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[todos_read] No lists for {ConversationId}")]
    private partial void LogTodosReadAllEmpty(string conversationId);
}
