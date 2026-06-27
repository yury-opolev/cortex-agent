namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Full export bundle containing all agent data: memories, messages, and tasks.
/// </summary>
public sealed record ExportBundle
{
    /// <summary>Schema version for forward compatibility.</summary>
    public int Version { get; init; } = 1;

    /// <summary>When this export was created.</summary>
    public DateTimeOffset ExportedAt { get; init; }

    /// <summary>Exported memories.</summary>
    public required ExportMemoriesPayload Memories { get; init; }

    /// <summary>Exported messages.</summary>
    public required ExportMessagesPayload Messages { get; init; }

    /// <summary>Exported scheduled tasks.</summary>
    public required ExportTasksPayload Tasks { get; init; }
}

/// <summary>Exported memories payload.</summary>
public sealed record ExportMemoriesPayload
{
    public required IReadOnlyList<MemoryItem> Items { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>Exported messages payload.</summary>
public sealed record ExportMessagesPayload
{
    public required IReadOnlyList<ExportMessageEntry> Items { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>Exported tasks payload.</summary>
public sealed record ExportTasksPayload
{
    public required IReadOnlyList<ScheduledTaskDto> Items { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>
/// A message entry for export/import. Includes UserId which MessageEntryDto lacks,
/// needed by SaveMessageAsync on import.
/// </summary>
public sealed record ExportMessageEntry
{
    public string? MessageId { get; init; }
    public required string UserId { get; init; }
    public required string ChannelId { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public MessageCategory Category { get; init; }
}

/// <summary>
/// Clean DTO mirror of ScheduledTask (which lives in Agent.Host only).
/// Status stored as string for JSON readability.
/// </summary>
public sealed record ScheduledTaskDto
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required string MessageText { get; init; }
    public DateTimeOffset ScheduledAtUtc { get; init; }
    public string? CronExpression { get; init; }
    public int? MaxExecutions { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? LastExecutedAtUtc { get; init; }
    public DateTimeOffset NextExecutionUtc { get; init; }
    public int ExecutionCount { get; init; }
    public string? ChannelId { get; init; }
}

/// <summary>Result of an import operation.</summary>
public sealed record ImportResult
{
    public bool Success { get; init; }
    public int MemoriesImported { get; init; }
    public int MessagesImported { get; init; }
    public int TasksImported { get; init; }
    public string? Error { get; init; }
}

/// <summary>Request to import memories.</summary>
public sealed record ImportMemoriesRequest
{
    public required IReadOnlyList<MemoryCreateRequest> Items { get; init; }
    public bool ClearExisting { get; init; } = true;
}

/// <summary>Request to import messages.</summary>
public sealed record ImportMessagesRequest
{
    public required IReadOnlyList<ExportMessageEntry> Items { get; init; }
    public bool ClearExisting { get; init; } = true;
}

/// <summary>Request to import tasks.</summary>
public sealed record ImportTasksRequest
{
    public required IReadOnlyList<ScheduledTaskDto> Items { get; init; }
    public bool ClearExisting { get; init; } = true;
}
