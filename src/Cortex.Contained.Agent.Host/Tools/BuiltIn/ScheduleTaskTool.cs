using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Scheduler;
using CronosExpression = Cronos.CronExpression;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Allows the LLM to schedule tasks for future execution. When a task fires,
/// its message is injected into the agent's processing queue as a deferred
/// input message. The agent processes it through the LLM with full context
/// and tool access, then sends the response to the user via the Bridge.
/// Supports one-shot and recurring (cron-based) tasks.
/// </summary>
internal sealed class ScheduleTaskTool : IAgentTool
{
    private readonly SchedulerService scheduler;
    private readonly ActiveChannelStore activeChannelStore;

    public ScheduleTaskTool(SchedulerService scheduler, ActiveChannelStore activeChannelStore)
    {
        this.scheduler = scheduler;
        this.activeChannelStore = activeChannelStore;
    }

    public string Name => "schedule_task";

    public string Description
    {
        get
        {
            var activeChannels = this.activeChannelStore.Get();
            var channelList = ChannelNameResolver.GetValidChannelNames(activeChannels);

            return activeChannels.Count > 0
                ? "Schedule a task to execute at a future time. When the task fires, its message " +
                  "is processed through the LLM in a fresh isolated session. " +
                  "Task output is NOT delivered to the user automatically — you must use send_message explicitly to notify them. " +
                  "Supports one-shot and recurring (cron-based) tasks. " +
                  "Actions: 'create' (schedule new task), 'cancel' (cancel a task), 'get' (get task details). " +
                  $"Available channels: {channelList}."
                : "Schedule a task to execute at a future time. When the task fires, its message " +
                  "is processed through the LLM in a fresh isolated session. " +
                  "Task output is NOT delivered to the user automatically — you must use send_message explicitly to notify them. " +
                  "Supports one-shot and recurring (cron-based) tasks. " +
                  "Actions: 'create' (schedule new task), 'cancel' (cancel a task), 'get' (get task details).";
        }
    }

    public string ParametersSchema
    {
        get
        {
            var activeChannels = this.activeChannelStore.Get();
            var channelList = ChannelNameResolver.GetValidChannelNames(activeChannels);

            return $$"""
                {
                  "type": "object",
                  "properties": {
                    "action": {
                      "type": "string",
                      "enum": ["create", "cancel", "get"],
                      "description": "The action to perform"
                    },
                    "description": {
                      "type": "string",
                      "description": "Human-readable description of the task (for 'create')"
                    },
                    "message": {
                      "type": "string",
                      "description": "The message/instruction text sent to the LLM when the task fires (for 'create')"
                    },
                    "scheduled_at": {
                      "type": "string",
                      "description": "ISO 8601 datetime for the first execution (for 'create'). e.g. '2026-03-01T15:00:00Z'"
                    },
                    "delay_minutes": {
                      "type": "number",
                      "description": "Alternative to scheduled_at: delay in minutes from now (for 'create')"
                    },
                    "cron": {
                      "type": "string",
                      "description": "Standard 5-field cron expression for recurring tasks (min hour dom month dow). Examples: '30 9 * * *' = daily 9:30 AM UTC, '*/15 * * * *' = every 15 min, '0 */2 * * *' = every 2 hours, '0 9 * * 1' = Mondays 9 AM. Omit for one-shot tasks."
                    },
                    "max_executions": {
                      "type": "integer",
                      "description": "Maximum number of times to run (only for recurring tasks). Omit for unlimited."
                    },
                    "channel": {
                      "type": "string",
                      "description": "Channel the task should use with send_message to deliver results: {{channelList}}. Include this channel name in the message text."
                    },
                    "task_id": {
                      "type": "string",
                      "description": "Task ID (for 'cancel' and 'get')"
                    }
                  },
                  "required": ["action"]
                }
                """;
        }
    }

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("action", out var actionElement))
            {
                return Task.FromResult(AgentToolResult.Fail("Missing required parameter: action"));
            }

            var action = actionElement.GetString() ?? string.Empty;

            return action switch
            {
                "create" => HandleCreate(root),
                "cancel" => HandleCancel(root),
                "get" => HandleGet(root),
                _ => Task.FromResult(AgentToolResult.Fail($"Unknown action: '{action}'. Valid actions: create, cancel, get")),
            };
        }
        catch (JsonException ex)
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}"));
        }
    }

    private Task<AgentToolResult> HandleCreate(JsonElement root)
    {
        // Required fields
        if (!root.TryGetProperty("description", out var descElement) ||
            string.IsNullOrWhiteSpace(descElement.GetString()))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: description"));
        }

        if (!root.TryGetProperty("message", out var messageElement) ||
            string.IsNullOrWhiteSpace(messageElement.GetString()))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: message"));
        }

        // Determine schedule time
        DateTimeOffset scheduledAt;
        if (root.TryGetProperty("scheduled_at", out var scheduledAtElement))
        {
            var dtString = scheduledAtElement.GetString() ?? string.Empty;
            if (!DateTimeOffset.TryParse(dtString, CultureInfo.InvariantCulture, DateTimeStyles.None, out scheduledAt))
            {
                return Task.FromResult(AgentToolResult.Fail($"Invalid scheduled_at format: '{dtString}'. Use ISO 8601 (e.g. '2026-03-01T15:00:00Z')."));
            }
        }
        else if (root.TryGetProperty("delay_minutes", out var delayElement) &&
                 delayElement.TryGetDouble(out var delayMinutes))
        {
            if (delayMinutes <= 0)
            {
                return Task.FromResult(AgentToolResult.Fail("delay_minutes must be positive"));
            }

            scheduledAt = DateTimeOffset.UtcNow.AddMinutes(delayMinutes);
        }
        else
        {
            return Task.FromResult(AgentToolResult.Fail("Must provide either 'scheduled_at' (ISO 8601) or 'delay_minutes'"));
        }

        // Optional cron expression for recurrence (standard 5-field: min hour dom month dow)
        string? cronExpression = null;
        if (root.TryGetProperty("cron", out var cronElement))
        {
            cronExpression = cronElement.GetString();
            if (!string.IsNullOrWhiteSpace(cronExpression))
            {
                // Validate the cron expression
                try
                {
                    CronosExpression.Parse(cronExpression);
                }
#pragma warning disable CA1031 // Catch general exception from Cronos parsing to return user-friendly error
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    return Task.FromResult(AgentToolResult.Fail($"Invalid cron expression '{cronExpression}': {ex.Message}. Use standard 5-field format: min hour dom month dow (e.g. '30 9 * * *' for daily 9:30 AM)."));
                }
            }
            else
            {
                cronExpression = null;
            }
        }

        // Optional max executions (only meaningful with cron)
        int? maxExecutions = null;
        if (root.TryGetProperty("max_executions", out var maxExecElement) &&
            maxExecElement.TryGetInt32(out var maxExecValue))
        {
            if (maxExecValue <= 0)
            {
                return Task.FromResult(AgentToolResult.Fail("max_executions must be a positive integer"));
            }

            maxExecutions = maxExecValue;
        }

        // Optional channel resolution with active channel validation
        var activeChannels = this.activeChannelStore.Get();
        string? channelId = null;
        if (root.TryGetProperty("channel", out var channelElement))
        {
            var channelName = channelElement.GetString();
            if (!string.IsNullOrWhiteSpace(channelName))
            {
                if (!ChannelNameResolver.TryResolve(channelName, out channelId))
                {
                    return Task.FromResult(AgentToolResult.Fail($"Unknown channel '{channelName}'. Available channels: {ChannelNameResolver.GetValidChannelNames(activeChannels)}"));
                }

                // Check if the resolved channel is actually active
                if (!ChannelNameResolver.IsChannelActive(channelId, activeChannels))
                {
                    return Task.FromResult(AgentToolResult.Fail($"Channel '{channelName}' is not currently active. Available channels: {ChannelNameResolver.GetValidChannelNames(activeChannels)}"));
                }
            }
        }

        var task = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Description = descElement.GetString()!,
            MessageText = messageElement.GetString()!,
            ScheduledAtUtc = scheduledAt,
            CronExpression = cronExpression,
            MaxExecutions = maxExecutions,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ChannelId = channelId,
        };

        this.scheduler.Schedule(task);

        var recurrenceInfo = cronExpression is not null
            ? string.Create(CultureInfo.InvariantCulture, $", cron: {cronExpression}")
            : " (one-shot)";

        var maxExecInfo = maxExecutions.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $", max runs: {maxExecutions.Value}")
            : string.Empty;

        return Task.FromResult(AgentToolResult.Ok(string.Create(CultureInfo.InvariantCulture,
            $"Task scheduled: id={task.Id}, fires at {task.NextExecutionUtc:yyyy-MM-dd HH:mm:ss UTC}{recurrenceInfo}{maxExecInfo}")));
    }

    private Task<AgentToolResult> HandleCancel(JsonElement root)
    {
        if (!root.TryGetProperty("task_id", out var taskIdElement) ||
            string.IsNullOrWhiteSpace(taskIdElement.GetString()))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: task_id"));
        }

        var taskId = taskIdElement.GetString()!;
        var cancelled = this.scheduler.Cancel(taskId);

        return Task.FromResult(cancelled
            ? AgentToolResult.Ok($"Task {taskId} cancelled.")
            : AgentToolResult.Fail($"Task '{taskId}' not found."));
    }

    private Task<AgentToolResult> HandleGet(JsonElement root)
    {
        if (!root.TryGetProperty("task_id", out var taskIdElement) ||
            string.IsNullOrWhiteSpace(taskIdElement.GetString()))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: task_id"));
        }

        var taskId = taskIdElement.GetString()!;
        var task = this.scheduler.GetTask(taskId);

        if (task is null)
        {
            return Task.FromResult(AgentToolResult.Fail($"Task '{taskId}' not found."));
        }

        var status = task.Status.ToStorageValue();
        var recurrence = task.IsRecurring
            ? $"cron: {task.CronExpression}"
            : "one-shot";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Task {task.Id}:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Description: {task.Description}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Message: {task.MessageText}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Status: {status}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Channel: {task.ChannelId ?? "(webchat fallback)"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Scheduled: {task.ScheduledAtUtc:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Next execution: {task.NextExecutionUtc:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Recurrence: {recurrence}");
        sb.Append(CultureInfo.InvariantCulture, $"  Executions: {task.ExecutionCount}");

        if (task.MaxExecutions.HasValue)
        {
            sb.Append(CultureInfo.InvariantCulture, $" / {task.MaxExecutions.Value} max");
        }

        if (task.LastExecutedAtUtc.HasValue)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  Last run: {task.LastExecutedAtUtc.Value:yyyy-MM-dd HH:mm:ss UTC}");
        }

        var content = sb.ToString();

        return Task.FromResult(AgentToolResult.Ok(content));
    }
}
