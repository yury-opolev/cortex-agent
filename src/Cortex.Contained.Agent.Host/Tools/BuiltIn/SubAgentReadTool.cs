using System.Globalization;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Reads the status and result of a specific subagent task.
/// </summary>
public sealed partial class SubAgentReadTool : IAgentTool
{
    private readonly SubagentSessionStore store;
    private readonly InMemoryTodoStore? todoStore;
    private readonly ILogger<SubAgentReadTool> logger;

    public SubAgentReadTool(SubagentSessionStore store, ILogger<SubAgentReadTool> logger, InMemoryTodoStore? todoStore = null)
    {
        this.store = store;
        this.logger = logger;
        this.todoStore = todoStore;
    }

    public string Name => "sub_agent_read";

    public string Description =>
        "Read the status and result of a background subagent task. " +
        "Returns the current state, description, and result (if completed). " +
        "Active tasks are also shown in the system prompt.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "task_id": {
              "type": "string",
              "description": "The task ID to read status and result for"
            }
          },
          "required": ["task_id"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string taskId;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            taskId = doc.RootElement.GetProperty("task_id").GetString() ?? string.Empty;
        }
#pragma warning disable CA1031 // Bad arguments should not crash the agent
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid arguments: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: task_id"));
        }

        var task = this.store.GetById(taskId);
        if (task is null)
        {
            this.LogTaskNotFound(taskId);
            return Task.FromResult(AgentToolResult.Fail($"No subagent task found with ID '{taskId}'."));
        }

        var stateValue = task.State.ToStorageValue();
        this.LogTaskRead(taskId, stateValue);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Task ID: {task.TaskId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Description: {task.Description}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"State: {task.State.ToStorageValue()}");

        var elapsed = (DateTimeOffset.UtcNow - task.CreatedAt).TotalMinutes;
        sb.AppendLine(CultureInfo.InvariantCulture, $"Created: {elapsed:F0} minutes ago");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Rounds: {task.Rounds}");

        if (task.CompletedAt.HasValue)
        {
            var completedAgo = (DateTimeOffset.UtcNow - task.CompletedAt.Value).TotalMinutes;
            sb.AppendLine(CultureInfo.InvariantCulture, $"Completed: {completedAgo:F0} minutes ago");
        }

        if (task.Result is not null)
        {
            sb.AppendLine();
            sb.AppendLine("--- Result ---");
            sb.AppendLine(task.Result);
        }

        if (task.EvalResponse is not null)
        {
            sb.AppendLine();
            sb.AppendLine("--- Eval Response (what was sent to user) ---");
            sb.AppendLine(task.EvalResponse);
        }

        // Include subagent's todo list if available
        if (this.todoStore is not null)
        {
            var subagentConvId = $"subagent-{task.TaskId}";
            var todos = this.todoStore.ReadAll(subagentConvId);
            if (todos.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("--- Task Progress ---");
                foreach (var list in todos)
                {
                    var summary = TodoParser.Summarize(list.Name, list.Items);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"({summary.DoneCount}/{summary.TotalCount} done)");
                    sb.AppendLine(list.Markdown);
                }
            }
        }

        return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "[sub_agent_read] Read task {TaskId}: {State}")]
    private partial void LogTaskRead(string taskId, string state);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[sub_agent_read] Task not found: {TaskId}")]
    private partial void LogTaskNotFound(string taskId);
}
