using Cortex.Contained.Agent.Host.Scheduler;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class SqliteTaskStoreOriginTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "cortex-origin-migration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Version3Upgrade_PreservesExistingTasksAndPersistsNewOrigin()
    {
        Directory.CreateDirectory(Path.Combine(this.path, "scheduler"));
        using (var connection = new SqliteConnection(this.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tasks (
                    id TEXT PRIMARY KEY, description TEXT NOT NULL, message_text TEXT NOT NULL,
                    scheduled_at_utc TEXT NOT NULL, cron_expression TEXT, max_executions INTEGER,
                    created_at_utc TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending',
                    last_executed_at_utc TEXT, next_execution_utc TEXT NOT NULL,
                    execution_count INTEGER NOT NULL DEFAULT 0, channel_id TEXT);
                CREATE INDEX idx_tasks_active ON tasks (status, next_execution_utc)
                    WHERE status IN ('pending', 'running');
                INSERT INTO tasks (id, description, message_text, scheduled_at_utc, created_at_utc,
                    next_execution_utc, channel_id, cron_expression, execution_count)
                VALUES ('existing', 'Existing reminder', 'Keep me', '2026-10-03T12:00:00.0000000Z',
                    '2026-10-03T11:00:00.0000000Z', '2026-10-03T12:00:00.0000000Z', 'discord-dm', '0 * * * *', 3);
                PRAGMA user_version = 3;
                """;
            command.ExecuteNonQuery();
        }

        using (var store = new SqliteTaskStore(this.path))
        {
            var existing = store.GetById("existing");
            Assert.NotNull(existing);
            Assert.Equal("Keep me", existing.MessageText);
            Assert.Null(existing.OriginChannelId);
            Assert.Equal("discord-dm", existing.ChannelId);
            Assert.Equal("0 * * * *", existing.CronExpression);
            Assert.Equal(3, existing.ExecutionCount);
            Assert.Equal(ScheduledTaskStatus.Pending, existing.Status);
            Assert.DoesNotContain("Origin channel:", SchedulerService.BuildEnrichedMessageText(existing), StringComparison.Ordinal);

            var created = new ScheduledTask
            {
                Id = "new",
                Description = "Rest cue",
                MessageText = "Next set",
                ScheduledAtUtc = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
                NextExecutionUtc = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
                OriginChannelId = "discord-voice",
                ChannelId = "webchat-default",
            };
            store.Upsert(created);
        }

        using var reopened = new SqliteTaskStore(this.path);
        Assert.Equal(2, reopened.GetActive().Count);
        var loaded = reopened.GetById("new");
        Assert.NotNull(loaded);
        Assert.Equal("webchat-default", loaded.ChannelId);
        Assert.Equal("discord-voice", loaded.OriginChannelId);
        Assert.Contains("Origin channel: discord-voice", SchedulerService.BuildEnrichedMessageText(loaded), StringComparison.Ordinal);
    }

    private string ConnectionString => $"Data Source={Path.Combine(this.path, "scheduler", "tasks.db")}";

    public void Dispose()
    {
        using var connection = new SqliteConnection(this.ConnectionString);
        SqliteConnection.ClearPool(connection);
        if (Directory.Exists(this.path))
        {
            Directory.Delete(this.path, recursive: true);
        }
    }
}
