using System.Globalization;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Scheduler;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SchedulerToolTests : IDisposable
{
    private static readonly ToolExecutionContext _context = new()
    {
        ConversationId = "conv-test",
        ChannelId = "webchat-default",
    };

    private readonly string _tempDir;
    private readonly SchedulerService _scheduler;
    private readonly ScheduleTaskTool _scheduleTaskTool;
    private readonly ListTasksTool _listTasksTool;
    private readonly ActiveChannelStore _activeChannelStore;

    public SchedulerToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cortex-scheduler-tool-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var messageChannel = new AgentMessageChannel();
        _scheduler = new SchedulerService(
            messageChannel, _tempDir, NullLogger<SchedulerService>.Instance);
        _activeChannelStore = new ActiveChannelStore();
        _scheduleTaskTool = new ScheduleTaskTool(_scheduler, _activeChannelStore);
        _listTasksTool = new ListTasksTool(_scheduler);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ── ScheduleTaskTool Tests ───────────────────────────────────────────

    [Fact]
    public void ScheduleTaskTool_Name_IsCorrect()
    {
        Assert.Equal("schedule_task", _scheduleTaskTool.Name);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_WithDelayMinutes_Success()
    {
        var args = """
        {
            "action": "create",
            "description": "Remind about meeting",
            "message": "Meeting in 5 minutes!",
            "delay_minutes": 30
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Task scheduled", result.Content);
        Assert.Single(_scheduler.GetActive());
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_WithScheduledAt_Success()
    {
        var futureTime = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var args = $$"""
        {
            "action": "create",
            "description": "Evening check-in",
            "message": "How was your day?",
            "scheduled_at": "{{futureTime}}"
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Task scheduled", result.Content);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_Recurring_WithCron_Success()
    {
        var args = """
        {
            "action": "create",
            "description": "Hourly status check",
            "message": "Status check",
            "delay_minutes": 5,
            "cron": "0 * * * *"
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("cron: 0 * * * *", result.Content);

        var task = _scheduler.GetActive()[0];
        Assert.Equal("0 * * * *", task.CronExpression);
        Assert.True(task.IsRecurring);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_MissingDescription_ReturnsError()
    {
        var args = """
        {
            "action": "create",
            "message": "Hello",
            "delay_minutes": 10
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("description", result.Error);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_MissingTimeSpec_ReturnsError()
    {
        var args = """
        {
            "action": "create",
            "description": "Missing time",
            "message": "Hello"
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("scheduled_at", result.Error);
    }

    [Fact]
    public async Task ScheduleTaskTool_Cancel_Success()
    {
        // First create a task
        var createArgs = """
        {
            "action": "create",
            "description": "To be cancelled",
            "message": "Cancelled",
            "delay_minutes": 10
        }
        """;
        await _scheduleTaskTool.ExecuteAsync(createArgs, _context, CancellationToken.None);

        var taskId = _scheduler.GetActive()[0].Id;

        var cancelArgs = $$"""{"action": "cancel", "task_id": "{{taskId}}"}""";
        var result = await _scheduleTaskTool.ExecuteAsync(cancelArgs, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("cancelled", result.Content);
        Assert.Empty(_scheduler.GetActive());
    }

    [Fact]
    public async Task ScheduleTaskTool_Cancel_NonExistent_ReturnsError()
    {
        var args = """{"action": "cancel", "task_id": "nope"}""";
        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task ScheduleTaskTool_Get_ReturnsTaskDetails()
    {
        var createArgs = """
        {
            "action": "create",
            "description": "Check weather",
            "message": "What's the weather?",
            "delay_minutes": 15
        }
        """;
        await _scheduleTaskTool.ExecuteAsync(createArgs, _context, CancellationToken.None);
        var taskId = _scheduler.GetActive()[0].Id;

        var getArgs = $$"""{"action": "get", "task_id": "{{taskId}}"}""";
        var result = await _scheduleTaskTool.ExecuteAsync(getArgs, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Check weather", result.Content);
        Assert.Contains("pending", result.Content);
        Assert.Contains("Channel: (webchat fallback)", result.Content); // No explicit channel
    }

    [Fact]
    public async Task ScheduleTaskTool_Get_WithChannel_ShowsChannelId()
    {
        var createArgs = """
        {
            "action": "create",
            "description": "Voice reminder",
            "message": "Time to talk",
            "delay_minutes": 10,
            "channel": "voice"
        }
        """;
        await _scheduleTaskTool.ExecuteAsync(createArgs, _context, CancellationToken.None);
        var taskId = _scheduler.GetActive()[0].Id;

        var getArgs = $$"""{"action": "get", "task_id": "{{taskId}}"}""";
        var result = await _scheduleTaskTool.ExecuteAsync(getArgs, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Channel: voice-default", result.Content);
    }

    [Fact]
    public async Task ScheduleTaskTool_UnknownAction_ReturnsError()
    {
        var args = """{"action": "explode"}""";
        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown action", result.Error);
    }

    [Fact]
    public async Task ScheduleTaskTool_MissingAction_ReturnsError()
    {
        var args = """{"description": "test"}""";
        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("action", result.Error);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_DefaultsWork()
    {
        // No channel needed — defaults to null (last-active on delivery)
        var args = """
        {
            "action": "create",
            "description": "Context channel task",
            "message": "Hello from context",
            "delay_minutes": 10
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Task scheduled", result.Content);

        var task = _scheduler.GetActive()[0];
        Assert.Equal("Context channel task", task.Description);
        Assert.Null(task.ChannelId); // No explicit channel → null
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_WithChannel_ResolvesAndStores()
    {
        var args = """
        {
            "action": "create",
            "description": "Discord reminder",
            "message": "Time to check Discord",
            "delay_minutes": 30,
            "channel": "discord"
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Task scheduled", result.Content);

        var task = _scheduler.GetActive()[0];
        Assert.Equal("discord-dm", task.ChannelId); // "discord" resolves to "discord-dm"
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_WithFullChannelName_ResolvesCorrectly()
    {
        var args = """
        {
            "action": "create",
            "description": "WebChat reminder",
            "message": "Check webchat",
            "delay_minutes": 15,
            "channel": "webchat-default"
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);

        var task = _scheduler.GetActive()[0];
        Assert.Equal("webchat-default", task.ChannelId);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_WithInvalidChannel_ReturnsError()
    {
        var args = """
        {
            "action": "create",
            "description": "Bad channel",
            "message": "Hello",
            "delay_minutes": 10,
            "channel": "telegram"
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown channel", result.Error);
        Assert.Contains("telegram", result.Error);
    }

    // ── Active Channel Validation ────────────────────────────────────────

    [Fact]
    public async Task ScheduleTaskTool_Create_ChannelNotActive_ReturnsError()
    {
        _activeChannelStore.Set(["webchat-default"]);

        var args = """
        {
            "action": "create",
            "description": "Discord task",
            "message": "Hello discord",
            "delay_minutes": 10,
            "channel": "discord"
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not currently active", result.Error);
        Assert.Contains("discord", result.Error);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_ChannelIsActive_Succeeds()
    {
        _activeChannelStore.Set(["webchat-default", "discord-dm"]);

        var args = """
        {
            "action": "create",
            "description": "Discord reminder",
            "message": "Hello discord",
            "delay_minutes": 10,
            "channel": "discord"
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Task scheduled", result.Content);

        var task = _scheduler.GetActive()[0];
        Assert.Equal("discord-dm", task.ChannelId);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_NoActiveChannelsSet_AllowsAnyChannel()
    {
        // Default empty store — graceful degradation, all channels allowed
        var args = """
        {
            "action": "create",
            "description": "Voice task",
            "message": "Hello voice",
            "delay_minutes": 10,
            "channel": "voice"
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
    }

    // ── Dynamic Description / ParametersSchema ──────────────────────────

    [Fact]
    public void ScheduleTaskTool_Description_NoActiveChannels_ShowsAllInfo()
    {
        var desc = _scheduleTaskTool.Description;

        Assert.Contains("Schedule a task", desc);
        Assert.DoesNotContain("Available channels:", desc); // No active channels → generic description
    }

    [Fact]
    public void ScheduleTaskTool_Description_WithActiveChannels_ShowsAvailableChannels()
    {
        _activeChannelStore.Set(["webchat-default"]);

        var desc = _scheduleTaskTool.Description;

        Assert.Contains("Available channels:", desc);
        Assert.Contains("webchat", desc);
    }

    [Fact]
    public void ScheduleTaskTool_ParametersSchema_WithActiveChannels_IncludesActiveChannelNames()
    {
        _activeChannelStore.Set(["discord-dm", "discord-guild"]);

        var schema = _scheduleTaskTool.ParametersSchema;

        Assert.Contains("discord", schema);
        Assert.Contains("discord-dm", schema);
        Assert.Contains("discord-guild", schema);
        Assert.DoesNotContain("webchat", schema);
        Assert.DoesNotContain("voice", schema);
    }

    [Fact]
    public void ScheduleTaskTool_ParametersSchema_DynamicallyUpdatesWhenActiveChannelsChange()
    {
        _activeChannelStore.Set(["webchat-default"]);
        var schema1 = _scheduleTaskTool.ParametersSchema;
        Assert.Contains("webchat", schema1);
        Assert.DoesNotContain("discord", schema1);

        _activeChannelStore.Set(["discord-dm"]);
        var schema2 = _scheduleTaskTool.ParametersSchema;
        Assert.Contains("discord", schema2);
        Assert.DoesNotContain("webchat", schema2);
    }

    // ── ListTasksTool Tests ──────────────────────────────────────────────

    [Fact]
    public void ListTasksTool_Name_IsCorrect()
    {
        Assert.Equal("list_tasks", _listTasksTool.Name);
    }

    [Fact]
    public async Task ListTasksTool_NoTasks_ReturnsEmpty()
    {
        var result = await _listTasksTool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("No active tasks", result.Content);
    }

    [Fact]
    public async Task ListTasksTool_WithTasks_ReturnsSummary()
    {
        _scheduler.Schedule(new ScheduledTask
        {
            Id = "t-1",
            Description = "Task one",
            MessageText = "Hello",
            ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        _scheduler.Schedule(new ScheduledTask
        {
            Id = "t-2",
            Description = "Task two",
            MessageText = "World",
            ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(20),
        });

        var result = await _listTasksTool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("2 active task(s)", result.Content);
        Assert.Contains("Task one", result.Content);
        Assert.Contains("Task two", result.Content);
    }

    [Fact]
    public async Task ListTasksTool_DefaultsToActive()
    {
        _scheduler.Schedule(new ScheduledTask
        {
            Id = "t-active",
            Description = "Active",
            MessageText = "Active",
            ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        var completed = new ScheduledTask
        {
            Id = "t-done",
            Description = "Done",
            MessageText = "Done",
            ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            Status = ScheduledTaskStatus.Completed,
        };
        _scheduler.Schedule(completed);

        // No filter specified — always returns active only
        var result = await _listTasksTool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("1 active task(s)", result.Content);
        Assert.Contains("Active", result.Content);
        Assert.DoesNotContain("Done", result.Content);
    }

    // ── Cron-specific Tool Tests ─────────────────────────────────────────

    [Fact]
    public async Task ScheduleTaskTool_Create_CronWithMaxExecutions_Success()
    {
        var args = """
        {
            "action": "create",
            "description": "Limited recurring check",
            "message": "Check status",
            "delay_minutes": 5,
            "cron": "*/30 * * * *",
            "max_executions": 10
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("cron: */30 * * * *", result.Content);
        Assert.Contains("max runs: 10", result.Content);

        var task = _scheduler.GetActive()[0];
        Assert.Equal("*/30 * * * *", task.CronExpression);
        Assert.Equal(10, task.MaxExecutions);
        Assert.True(task.IsRecurring);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_InvalidCron_ReturnsError()
    {
        var args = """
        {
            "action": "create",
            "description": "Bad cron",
            "message": "Won't work",
            "delay_minutes": 5,
            "cron": "not a cron expression"
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Invalid cron expression", result.Error);
        Assert.Contains("5-field format", result.Error);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_ZeroMaxExecutions_ReturnsError()
    {
        var args = """
        {
            "action": "create",
            "description": "Zero max",
            "message": "Won't work",
            "delay_minutes": 5,
            "cron": "0 * * * *",
            "max_executions": 0
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("max_executions must be a positive integer", result.Error);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_NegativeMaxExecutions_ReturnsError()
    {
        var args = """
        {
            "action": "create",
            "description": "Negative max",
            "message": "Won't work",
            "delay_minutes": 5,
            "cron": "0 * * * *",
            "max_executions": -5
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("max_executions must be a positive integer", result.Error);
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_OneShot_HasNoCron()
    {
        var args = """
        {
            "action": "create",
            "description": "One-shot task",
            "message": "Do this once",
            "delay_minutes": 10
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("(one-shot)", result.Content);
        Assert.DoesNotContain("cron:", result.Content);

        var task = _scheduler.GetActive()[0];
        Assert.Null(task.CronExpression);
        Assert.False(task.IsRecurring);
    }

    [Fact]
    public async Task ScheduleTaskTool_Get_ShowsCronAndMaxExecutions()
    {
        var createArgs = """
        {
            "action": "create",
            "description": "Daily report",
            "message": "Generate report",
            "delay_minutes": 5,
            "cron": "0 9 * * *",
            "max_executions": 30
        }
        """;
        await _scheduleTaskTool.ExecuteAsync(createArgs, _context, CancellationToken.None);
        var taskId = _scheduler.GetActive()[0].Id;

        var getArgs = $$"""{"action": "get", "task_id": "{{taskId}}"}""";
        var result = await _scheduleTaskTool.ExecuteAsync(getArgs, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Daily report", result.Content);
        Assert.Contains("cron: 0 9 * * *", result.Content);
        Assert.Contains("Executions: 0 / 30 max", result.Content);
    }

    [Fact]
    public async Task ScheduleTaskTool_Get_OneShot_ShowsOneShotRecurrence()
    {
        var createArgs = """
        {
            "action": "create",
            "description": "Single reminder",
            "message": "Remind me",
            "delay_minutes": 10
        }
        """;
        await _scheduleTaskTool.ExecuteAsync(createArgs, _context, CancellationToken.None);
        var taskId = _scheduler.GetActive()[0].Id;

        var getArgs = $$"""{"action": "get", "task_id": "{{taskId}}"}""";
        var result = await _scheduleTaskTool.ExecuteAsync(getArgs, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Recurrence: one-shot", result.Content);
        Assert.DoesNotContain("max", result.Content); // No max for one-shot
    }

    [Fact]
    public async Task ScheduleTaskTool_Create_EmptyCron_TreatedAsOneShot()
    {
        var args = """
        {
            "action": "create",
            "description": "Empty cron task",
            "message": "One-shot with empty cron",
            "delay_minutes": 10,
            "cron": ""
        }
        """;

        var result = await _scheduleTaskTool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("(one-shot)", result.Content);

        var task = _scheduler.GetActive()[0];
        Assert.Null(task.CronExpression);
        Assert.False(task.IsRecurring);
    }

    [Fact]
    public async Task ListTasksTool_ShowsCronForRecurringTask()
    {
        _scheduler.Schedule(new ScheduledTask
        {
            Id = "t-cron",
            Description = "Recurring cron task",
            MessageText = "Check",
            ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            CronExpression = "*/15 * * * *",
        });

        var result = await _listTasksTool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("cron: */15 * * * *", result.Content);
    }

    [Fact]
    public async Task ListTasksTool_ShowsOneShotForNonRecurringTask()
    {
        _scheduler.Schedule(new ScheduledTask
        {
            Id = "t-once",
            Description = "One-shot task",
            MessageText = "Fire once",
            ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        var result = await _listTasksTool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("one-shot", result.Content);
        Assert.DoesNotContain("cron:", result.Content);
    }

    [Fact]
    public async Task ListTasksTool_ShowsMaxRunsWhenSet()
    {
        _scheduler.Schedule(new ScheduledTask
        {
            Id = "t-limited",
            Description = "Limited task",
            MessageText = "Check",
            ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            CronExpression = "0 * * * *",
            MaxExecutions = 5,
        });

        var result = await _listTasksTool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Max runs: 5", result.Content);
    }

    [Fact]
    public async Task ListTasksTool_OmitsMaxRunsWhenNotSet()
    {
        _scheduler.Schedule(new ScheduledTask
        {
            Id = "t-unlimited",
            Description = "Unlimited task",
            MessageText = "Check",
            ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            CronExpression = "0 * * * *",
        });

        var result = await _listTasksTool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain("Max runs", result.Content);
    }
}
