using System.Globalization;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Reminders;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Schedules short-lived timers on the CURRENT conversation. A timer carries an intent, which the
/// model acts on when it fires, with the live conversation in view.
/// </summary>
internal sealed class SessionTimerTool : IAgentTool
{
    private readonly SessionTimerService timers;

    public SessionTimerTool(SessionTimerService timers)
    {
        this.timers = timers;
    }

    public string Name => "session_timer";

    public string Description =>
        "Set a timer on this conversation that fires an INTENT back to you after a delay " +
        $"({SessionTimerService.MinDelaySeconds}-{SessionTimerService.MaxDelaySeconds}s). " +
        "When it fires you are re-invoked in this same conversation and decide what to say or do " +
        "then — so the intent should describe the goal ('call the next round and remind them to " +
        "keep their guard up'), not the exact words. You can also take actions, not just speak. " +
        "Actions: 'create', 'list' (shows pending timers and any that recently fired — use this " +
        "before assuming what is running), 'cancel'. A timer that has already fired cannot be " +
        "cancelled: its intent has been delivered to you, so simply choose not to act on it. " +
        "Use `schedule_task` instead for clock-time or recurring work; timers are session-scoped " +
        "and do not survive a restart.";

    public string ParametersSchema => $$"""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["create", "list", "cancel"],
              "description": "The action to perform."
            },
            "delay_seconds": {
              "type": "integer",
              "description": "For 'create': delay before firing. Range {{SessionTimerService.MinDelaySeconds}}-{{SessionTimerService.MaxDelaySeconds}}."
            },
            "intent": {
              "type": "string",
              "description": "For 'create': what should happen when the timer fires, as an instruction to yourself. Evaluated against the live conversation at fire time, so prefer intent over exact wording."
            },
            "description": {
              "type": "string",
              "description": "For 'create': optional short label shown when listing timers (e.g. 'round 2 start')."
            },
            "timer_id": {
              "type": "string",
              "description": "For 'cancel': the id returned by 'create' or shown by 'list'."
            }
          },
          "required": ["action"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("action", out var actionElement) || actionElement.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult(AgentToolResult.Fail("Missing required parameter: action"));
            }

            return actionElement.GetString() switch
            {
                "create" => this.HandleCreate(root, context),
                "list" => this.HandleList(context),
                "cancel" => this.HandleCancel(root, context),
                var other => Task.FromResult(AgentToolResult.Fail(
                    $"Unknown action: '{other}'. Valid actions: create, list, cancel")),
            };
        }
        catch (JsonException ex)
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}"));
        }
        catch (ArgumentException ex)
        {
            // The tool is the trust boundary: an unusable conversation or channel id must come back
            // as a tool failure the model can read, not an exception out of the tool loop.
            return Task.FromResult(AgentToolResult.Fail(ex.Message));
        }
    }

    private Task<AgentToolResult> HandleCreate(JsonElement root, ToolExecutionContext context)
    {
        if (!root.TryGetProperty("delay_seconds", out var delayElement)
            || delayElement.ValueKind != JsonValueKind.Number
            || !delayElement.TryGetInt32(out var delaySeconds))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing or invalid required parameter: delay_seconds (integer)."));
        }

        if (!root.TryGetProperty("intent", out var intentElement)
            || intentElement.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(AgentToolResult.Fail("Missing or invalid required parameter: intent (non-empty string)."));
        }

        var intent = intentElement.GetString();
        if (string.IsNullOrWhiteSpace(intent))
        {
            return Task.FromResult(AgentToolResult.Fail("intent cannot be empty or whitespace."));
        }

        string? description = null;
        if (root.TryGetProperty("description", out var descElement) && descElement.ValueKind == JsonValueKind.String)
        {
            var raw = descElement.GetString();
            description = string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        try
        {
            var id = this.timers.Schedule(
                context.ConversationId,
                context.ChannelId,
                delaySeconds,
                intent,
                description);

            return Task.FromResult(AgentToolResult.Ok(string.Create(
                CultureInfo.InvariantCulture,
                $"Timer {id} set — fires in {delaySeconds}s. Cancel with session_timer(action='cancel', timer_id='{id}').")));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Task.FromResult(AgentToolResult.Fail(
                $"delay_seconds must be between {SessionTimerService.MinDelaySeconds} and {SessionTimerService.MaxDelaySeconds} (got {delaySeconds})."));
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(AgentToolResult.Fail("Timers are shutting down; the timer was not set."));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(AgentToolResult.Fail(ex.Message));
        }
    }

    private Task<AgentToolResult> HandleList(ToolExecutionContext context)
    {
        var listed = this.timers.List(context.ConversationId);
        if (listed.Count == 0)
        {
            return Task.FromResult(AgentToolResult.Ok("No timers on this conversation."));
        }

        var pendingCount = 0;
        foreach (var timer in listed)
        {
            if (timer.Status == SessionTimerStatus.Pending)
            {
                pendingCount++;
            }
        }

        var firedCount = listed.Count - pendingCount;

        // Split the counts: a bare total leaves the agent unable to work out how much room it has
        // left, because fired timers are listed but no longer hold a slot.
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{pendingCount} pending");
        if (firedCount > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $", {firedCount} already fired");
        }

        sb.AppendLine(CultureInfo.InvariantCulture,
            $" (only pending timers count against the limit of {SessionTimerService.PerConversationCap}):");

        foreach (var timer in listed)
        {
            var state = timer.Status == SessionTimerStatus.Fired
                ? $"ALREADY FIRED ~{timer.SecondsSinceFired}s ago — cannot be cancelled"
                : $"fires in {timer.SecondsRemaining}s";

            sb.Append(CultureInfo.InvariantCulture, $"  {timer.Id} — {state}");
            if (!string.IsNullOrWhiteSpace(timer.Description))
            {
                sb.Append(CultureInfo.InvariantCulture, $" ({timer.Description})");
            }

            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"    intent: {timer.Intent}");
        }

        return Task.FromResult(AgentToolResult.Ok(sb.ToString().TrimEnd()));
    }

    private Task<AgentToolResult> HandleCancel(JsonElement root, ToolExecutionContext context)
    {
        if (!root.TryGetProperty("timer_id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: timer_id"));
        }

        var timerId = idElement.GetString()!;
        return Task.FromResult(this.timers.Cancel(context.ConversationId, timerId) switch
        {
            SessionTimerCancelOutcome.Cancelled => AgentToolResult.Ok($"Timer {timerId} cancelled."),
            SessionTimerCancelOutcome.AlreadyFired => AgentToolResult.Fail(
                $"Timer {timerId} has already fired and cannot be cancelled — you have its intent "
                + "already. If it is no longer relevant, simply do not act on it."),
            _ => AgentToolResult.Fail(
                $"Timer '{timerId}' not found. Use session_timer(action='list') to see what is running."),
        });
    }
}
