using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Scheduler;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests;

public class ExportImportMapperTests
{
    [Fact]
    public void MapMessageToExport_MapsAllFields()
    {
        var record = new MessageRecord
        {
            Id = 42,
            UserId = "user-123",
            ChannelId = "webchat-default",
            Role = "assistant",
            Content = "Hello!",
            Timestamp = new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero),
            MessageId = "msg-abc",
            Category = MessageCategory.System,
        };

        var export = ExportImportMapper.MapMessageToExport(record);

        Assert.Equal("msg-abc", export.MessageId);
        Assert.Equal("user-123", export.UserId);
        Assert.Equal("webchat-default", export.ChannelId);
        Assert.Equal("assistant", export.Role);
        Assert.Equal("Hello!", export.Content);
        Assert.Equal(record.Timestamp, export.Timestamp);
        Assert.Equal(MessageCategory.System, export.Category);
    }

    [Fact]
    public void MapExportToMessageRecord_MapsAllFields()
    {
        var entry = new ExportMessageEntry
        {
            MessageId = "msg-xyz",
            UserId = "user-456",
            ChannelId = "discord-dm",
            Role = "user",
            Content = "Hi there",
            Timestamp = new DateTimeOffset(2026, 3, 21, 8, 30, 0, TimeSpan.Zero),
            Category = MessageCategory.Internal,
        };

        var record = ExportImportMapper.MapExportToMessageRecord(entry);

        Assert.Equal("user-456", record.UserId);
        Assert.Equal("discord-dm", record.ChannelId);
        Assert.Equal("user", record.Role);
        Assert.Equal("Hi there", record.Content);
        Assert.Equal(entry.Timestamp, record.Timestamp);
        Assert.Equal("msg-xyz", record.MessageId);
        Assert.Equal(MessageCategory.Internal, record.Category);
    }

    [Fact]
    public void MapMessageToExport_RoundTrip()
    {
        var original = new MessageRecord
        {
            Id = 1,
            UserId = "user-rt",
            ChannelId = "ch-rt",
            Role = "user",
            Content = "Round trip test",
            Timestamp = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero),
            MessageId = "rt-msg",
            Category = MessageCategory.Proactive,
        };

        var export = ExportImportMapper.MapMessageToExport(original);
        var roundTripped = ExportImportMapper.MapExportToMessageRecord(export);

        Assert.Equal(original.UserId, roundTripped.UserId);
        Assert.Equal(original.ChannelId, roundTripped.ChannelId);
        Assert.Equal(original.Role, roundTripped.Role);
        Assert.Equal(original.Content, roundTripped.Content);
        Assert.Equal(original.Timestamp, roundTripped.Timestamp);
        Assert.Equal(original.MessageId, roundTripped.MessageId);
        Assert.Equal(original.Category, roundTripped.Category);
    }

    [Fact]
    public void MapTaskToDto_MapsAllFields()
    {
        var task = new ScheduledTask
        {
            Id = "task-001",
            Description = "Daily report",
            MessageText = "Generate report",
            ScheduledAtUtc = new DateTimeOffset(2026, 3, 20, 2, 0, 0, TimeSpan.Zero),
            CronExpression = "0 2 * * *",
            MaxExecutions = 10,
            CreatedAtUtc = new DateTimeOffset(2026, 3, 19, 12, 0, 0, TimeSpan.Zero),
            Status = ScheduledTaskStatus.Pending,
            LastExecutedAtUtc = new DateTimeOffset(2026, 3, 20, 2, 0, 0, TimeSpan.Zero),
            NextExecutionUtc = new DateTimeOffset(2026, 3, 21, 2, 0, 0, TimeSpan.Zero),
            ExecutionCount = 3,
            ChannelId = "webchat-default",
        };

        var dto = ExportImportMapper.MapTaskToDto(task);

        Assert.Equal("task-001", dto.Id);
        Assert.Equal("Daily report", dto.Description);
        Assert.Equal("Generate report", dto.MessageText);
        Assert.Equal(task.ScheduledAtUtc, dto.ScheduledAtUtc);
        Assert.Equal("0 2 * * *", dto.CronExpression);
        Assert.Equal(10, dto.MaxExecutions);
        Assert.Equal(task.CreatedAtUtc, dto.CreatedAtUtc);
        Assert.Equal("pending", dto.Status);
        Assert.Equal(task.LastExecutedAtUtc, dto.LastExecutedAtUtc);
        Assert.Equal(task.NextExecutionUtc, dto.NextExecutionUtc);
        Assert.Equal(3, dto.ExecutionCount);
        Assert.Equal("webchat-default", dto.ChannelId);
    }

    [Fact]
    public void MapDtoToTask_MapsAllFields()
    {
        var dto = new ScheduledTaskDto
        {
            Id = "task-002",
            Description = "Weekly cleanup",
            MessageText = "Run cleanup",
            ScheduledAtUtc = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero),
            CronExpression = "0 0 * * 0",
            MaxExecutions = 52,
            CreatedAtUtc = new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero),
            Status = "running",
            LastExecutedAtUtc = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero),
            NextExecutionUtc = new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero),
            ExecutionCount = 1,
            ChannelId = "discord-dm",
        };

        var task = ExportImportMapper.MapDtoToTask(dto);

        Assert.Equal("task-002", task.Id);
        Assert.Equal("Weekly cleanup", task.Description);
        Assert.Equal("Run cleanup", task.MessageText);
        Assert.Equal(dto.ScheduledAtUtc, task.ScheduledAtUtc);
        Assert.Equal("0 0 * * 0", task.CronExpression);
        Assert.Equal(52, task.MaxExecutions);
        Assert.Equal(dto.CreatedAtUtc, task.CreatedAtUtc);
        Assert.Equal(ScheduledTaskStatus.Running, task.Status);
        Assert.Equal(dto.LastExecutedAtUtc, task.LastExecutedAtUtc);
        Assert.Equal(dto.NextExecutionUtc, task.NextExecutionUtc);
        Assert.Equal(1, task.ExecutionCount);
        Assert.Equal("discord-dm", task.ChannelId);
    }

    [Fact]
    public void MapTaskToDto_RoundTrip()
    {
        var original = new ScheduledTask
        {
            Id = "task-rt",
            Description = "Round trip",
            MessageText = "Test message",
            ScheduledAtUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            CronExpression = null,
            MaxExecutions = null,
            CreatedAtUtc = new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero),
            Status = ScheduledTaskStatus.Completed,
            LastExecutedAtUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            NextExecutionUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            ExecutionCount = 1,
            ChannelId = null,
        };

        var dto = ExportImportMapper.MapTaskToDto(original);
        var roundTripped = ExportImportMapper.MapDtoToTask(dto);

        Assert.Equal(original.Id, roundTripped.Id);
        Assert.Equal(original.Description, roundTripped.Description);
        Assert.Equal(original.MessageText, roundTripped.MessageText);
        Assert.Equal(original.ScheduledAtUtc, roundTripped.ScheduledAtUtc);
        Assert.Equal(original.CronExpression, roundTripped.CronExpression);
        Assert.Equal(original.MaxExecutions, roundTripped.MaxExecutions);
        Assert.Equal(original.CreatedAtUtc, roundTripped.CreatedAtUtc);
        Assert.Equal(original.Status, roundTripped.Status);
        Assert.Equal(original.LastExecutedAtUtc, roundTripped.LastExecutedAtUtc);
        Assert.Equal(original.NextExecutionUtc, roundTripped.NextExecutionUtc);
        Assert.Equal(original.ExecutionCount, roundTripped.ExecutionCount);
        Assert.Equal(original.ChannelId, roundTripped.ChannelId);
    }

    [Fact]
    public void MapDtoToTask_NullableFields()
    {
        var dto = new ScheduledTaskDto
        {
            Id = "task-null",
            Description = "Nullables",
            MessageText = "Test",
            Status = "pending",
            CronExpression = null,
            MaxExecutions = null,
            LastExecutedAtUtc = null,
            ChannelId = null,
        };

        var task = ExportImportMapper.MapDtoToTask(dto);

        Assert.Null(task.CronExpression);
        Assert.Null(task.MaxExecutions);
        Assert.Null(task.LastExecutedAtUtc);
        Assert.Null(task.ChannelId);
    }

    [Fact]
    public void MapDtoToTask_UnknownStatus_DefaultsToPending()
    {
        var dto = new ScheduledTaskDto
        {
            Id = "task-unknown",
            Description = "Unknown status",
            MessageText = "Test",
            Status = "some_unknown_status",
        };

        var task = ExportImportMapper.MapDtoToTask(dto);

        Assert.Equal(ScheduledTaskStatus.Pending, task.Status);
    }
}
