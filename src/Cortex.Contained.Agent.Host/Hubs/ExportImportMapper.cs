using Cortex.Contained.Agent.Host.Scheduler;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary>
/// Static mapper between internal domain types and export/import DTOs.
/// Keeps mapping logic out of AgentHub for testability.
/// </summary>
public static class ExportImportMapper
{
    public static ExportMessageEntry MapMessageToExport(MessageRecord record)
    {
        return new ExportMessageEntry
        {
            MessageId = record.MessageId,
            UserId = record.UserId,
            ChannelId = record.ChannelId,
            Role = record.Role,
            Content = record.Content,
            Timestamp = record.Timestamp,
            Category = record.Category,
        };
    }

    public static MessageRecord MapExportToMessageRecord(ExportMessageEntry entry)
    {
        return new MessageRecord
        {
            UserId = entry.UserId,
            ChannelId = entry.ChannelId,
            Role = entry.Role,
            Content = entry.Content,
            Timestamp = entry.Timestamp,
            MessageId = entry.MessageId,
            Category = entry.Category,
        };
    }

    public static ScheduledTaskDto MapTaskToDto(ScheduledTask task)
    {
        return new ScheduledTaskDto
        {
            Id = task.Id,
            Description = task.Description,
            MessageText = task.MessageText,
            ScheduledAtUtc = task.ScheduledAtUtc,
            CronExpression = task.CronExpression,
            MaxExecutions = task.MaxExecutions,
            CreatedAtUtc = task.CreatedAtUtc,
            Status = task.Status.ToStorageValue(),
            LastExecutedAtUtc = task.LastExecutedAtUtc,
            NextExecutionUtc = task.NextExecutionUtc,
            ExecutionCount = task.ExecutionCount,
            ChannelId = task.ChannelId,
        };
    }

    public static ScheduledTask MapDtoToTask(ScheduledTaskDto dto)
    {
        return new ScheduledTask
        {
            Id = dto.Id,
            Description = dto.Description,
            MessageText = dto.MessageText,
            ScheduledAtUtc = dto.ScheduledAtUtc,
            CronExpression = dto.CronExpression,
            MaxExecutions = dto.MaxExecutions,
            CreatedAtUtc = dto.CreatedAtUtc,
            Status = ScheduledTaskStatusExtensions.Parse(dto.Status),
            LastExecutedAtUtc = dto.LastExecutedAtUtc,
            NextExecutionUtc = dto.NextExecutionUtc,
            ExecutionCount = dto.ExecutionCount,
            ChannelId = dto.ChannelId,
        };
    }
}
