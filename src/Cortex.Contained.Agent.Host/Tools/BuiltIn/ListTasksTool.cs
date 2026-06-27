using System.Globalization;
using System.Text;
using Cortex.Contained.Agent.Host.Scheduler;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Lists all active scheduled tasks. Provides a summary of pending tasks
/// for the LLM to inspect.
/// </summary>
internal sealed class ListTasksTool : IAgentTool
{
    private readonly SchedulerService scheduler;

    public ListTasksTool(SchedulerService scheduler)
    {
        this.scheduler = scheduler;
    }

    public string Name => "list_tasks";

    public string Description =>
        "List all active scheduled tasks with their details.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {}
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var activeTasks = this.scheduler.GetActive();

        if (activeTasks.Count == 0)
        {
            return Task.FromResult(AgentToolResult.Ok("No active tasks found."));
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Found {activeTasks.Count} active task(s):");
        sb.AppendLine();

        foreach (var task in activeTasks)
        {
            var recurrence = task.IsRecurring
                ? $"cron: {task.CronExpression}"
                : "one-shot";

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  [{task.Id}] {task.Description}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    Next: {task.NextExecutionUtc:yyyy-MM-dd HH:mm:ss UTC} | {recurrence} | Runs: {task.ExecutionCount}");

            if (task.MaxExecutions.HasValue)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    Max runs: {task.MaxExecutions.Value}");
            }

            sb.AppendLine();
        }

        return Task.FromResult(AgentToolResult.Ok(sb.ToString().TrimEnd()));
    }
}
