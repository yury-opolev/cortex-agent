using System.Text.Json;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Contracts.Tests;

public class ExportImportSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void ExportBundle_Serialization_RoundTrip()
    {
        var bundle = new ExportBundle
        {
            ExportedAt = new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero),
            Memories = new ExportMemoriesPayload
            {
                Items = [new MemoryItem { MemoryId = "mem-1", Content = "Test memory" }],
                TotalCount = 1,
            },
            Messages = new ExportMessagesPayload
            {
                Items =
                [
                    new ExportMessageEntry
                    {
                        UserId = "u1",
                        ChannelId = "ch",
                        Role = "user",
                        Content = "Hello",
                        Timestamp = new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero),
                        Category = MessageCategory.Normal,
                    },
                ],
                TotalCount = 1,
            },
            Tasks = new ExportTasksPayload
            {
                Items =
                [
                    new ScheduledTaskDto
                    {
                        Id = "task-1",
                        Description = "Test task",
                        MessageText = "Do something",
                        Status = "pending",
                    },
                ],
                TotalCount = 1,
            },
        };

        var json = JsonSerializer.Serialize(bundle, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportBundle>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(bundle.Version, deserialized.Version);
        Assert.Equal(bundle.ExportedAt, deserialized.ExportedAt);
        var mem = Assert.Single(deserialized.Memories.Items);
        Assert.Equal("mem-1", mem.MemoryId);
        var msg = Assert.Single(deserialized.Messages.Items);
        Assert.Equal("Hello", msg.Content);
        var task = Assert.Single(deserialized.Tasks.Items);
        Assert.Equal("task-1", task.Id);
    }

    [Fact]
    public void ExportBundle_DefaultVersion_Is1()
    {
        var bundle = new ExportBundle
        {
            Memories = new ExportMemoriesPayload { Items = [] },
            Messages = new ExportMessagesPayload { Items = [] },
            Tasks = new ExportTasksPayload { Items = [] },
        };

        Assert.Equal(1, bundle.Version);

        var json = JsonSerializer.Serialize(bundle, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportBundle>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(1, deserialized.Version);
    }

    [Fact]
    public void ScheduledTaskDto_Serialization_PreservesNulls()
    {
        var dto = new ScheduledTaskDto
        {
            Id = "task-null",
            Description = "Null fields",
            MessageText = "Test",
            Status = "pending",
            CronExpression = null,
            MaxExecutions = null,
            LastExecutedAtUtc = null,
            ChannelId = null,
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ScheduledTaskDto>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.CronExpression);
        Assert.Null(deserialized.MaxExecutions);
        Assert.Null(deserialized.LastExecutedAtUtc);
        Assert.Null(deserialized.ChannelId);
    }

    [Fact]
    public void ExportMessageEntry_Serialization_PreservesCategory()
    {
        var entry = new ExportMessageEntry
        {
            UserId = "u1",
            ChannelId = "ch",
            Role = "assistant",
            Content = "Test",
            Category = MessageCategory.Internal,
        };

        var json = JsonSerializer.Serialize(entry, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportMessageEntry>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageCategory.Internal, deserialized.Category);
    }

    [Fact]
    public void ImportResult_Serialization_RoundTrip()
    {
        var result = new ImportResult
        {
            Success = true,
            MemoriesImported = 10,
            MessagesImported = 50,
            TasksImported = 3,
            Error = null,
        };

        var json = JsonSerializer.Serialize(result, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ImportResult>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.Equal(10, deserialized.MemoriesImported);
        Assert.Equal(50, deserialized.MessagesImported);
        Assert.Equal(3, deserialized.TasksImported);
        Assert.Null(deserialized.Error);
    }
}
