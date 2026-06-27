using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Scheduler;

/// <summary>
/// Persists scheduled tasks in a SQLite database.
/// </summary>
internal sealed class SqliteTaskStore : SqliteStoreBase
{
    private readonly Lock syncLock = new();

    /// <summary>Tasks completed/cancelled longer ago than this are purged.</summary>
    private static readonly TimeSpan CleanupAge = TimeSpan.FromDays(7);

    /// <summary>Current schema version. Bump when adding migrations.</summary>
    private const int CurrentSchemaVersion = 3;

    /// <summary>
    /// Initialises the task store, opening the SQLite database at <paramref name="dataPath"/>/scheduler/tasks.db.
    /// </summary>
    public SqliteTaskStore(string dataPath)
        : base(PrepareDatabasePath(dataPath, "scheduler", "tasks.db"), enableWalMode: false)
    {
        this.EnsureSchema();
    }

    private void EnsureSchema()
    {
        var version = GetSchemaVersion();
        if (version < CurrentSchemaVersion)
        {
            // Drop and recreate — old tasks are expendable.
            ExecuteNonQuery("DROP TABLE IF EXISTS tasks");
            ExecuteNonQuery("DROP INDEX IF EXISTS idx_tasks_active");

            ExecuteNonQuery("""
                CREATE TABLE tasks (
                    id                  TEXT PRIMARY KEY,
                    description         TEXT NOT NULL,
                    message_text        TEXT NOT NULL,
                    scheduled_at_utc    TEXT NOT NULL,
                    cron_expression     TEXT,
                    max_executions      INTEGER,
                    created_at_utc      TEXT NOT NULL,
                    status              TEXT NOT NULL DEFAULT 'pending',
                    last_executed_at_utc TEXT,
                    next_execution_utc  TEXT NOT NULL,
                    execution_count     INTEGER NOT NULL DEFAULT 0,
                    channel_id          TEXT
                );

                CREATE INDEX idx_tasks_active
                    ON tasks (status, next_execution_utc)
                    WHERE status IN ('pending', 'running');
                """);

            this.SetSchemaVersion(CurrentSchemaVersion);
        }
    }

    /// <summary>Insert or replace a task.</summary>
    public void Upsert(ScheduledTask task)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO tasks
                    (id, description, message_text, scheduled_at_utc,
                     cron_expression, max_executions,
                     created_at_utc, status, last_executed_at_utc,
                     next_execution_utc, execution_count, channel_id)
                VALUES
                    ($id, $description, $messageText, $scheduledAtUtc,
                     $cronExpression, $maxExecutions,
                     $createdAtUtc, $status, $lastExecutedAtUtc,
                     $nextExecutionUtc, $executionCount, $channelId)
                """;
            BindTaskParameters(cmd, task);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Get all active (pending or running) tasks.</summary>
    public List<ScheduledTask> GetActive()
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM tasks WHERE status IN ('pending', 'running') ORDER BY next_execution_utc";
            return ReadTasks(cmd);
        }
    }

    /// <summary>Get all tasks due for execution (pending and past their next execution time).</summary>
    public List<ScheduledTask> GetDue(DateTimeOffset now)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM tasks
                WHERE status = 'pending' AND next_execution_utc <= $now
                ORDER BY next_execution_utc
                """;
            cmd.Parameters.AddWithValue("$now", FormatDto(now));
            return ReadTasks(cmd);
        }
    }

    /// <summary>Get all tasks (including completed/cancelled), ordered by next execution.</summary>
    public List<ScheduledTask> GetAll()
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM tasks ORDER BY next_execution_utc";
            return ReadTasks(cmd);
        }
    }

    /// <summary>Get a task by ID, or null if not found.</summary>
    public ScheduledTask? GetById(string taskId)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM tasks WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", taskId);
            var tasks = ReadTasks(cmd);
            return tasks.Count > 0 ? tasks[0] : null;
        }
    }

    /// <summary>Mark a task as cancelled.</summary>
    public bool Cancel(string taskId)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "UPDATE tasks SET status = 'cancelled' WHERE id = $id AND status NOT IN ('cancelled', 'completed')";
            cmd.Parameters.AddWithValue("$id", taskId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Reset any tasks stuck in 'running' status back to 'pending'.
    /// Called on startup to recover from a crash mid-execution.
    /// </summary>
    public int ResetRunningTasks()
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "UPDATE tasks SET status = 'pending' WHERE status = 'running'";
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Delete tasks that are completed or cancelled and whose last execution
    /// (or creation time, for never-executed tasks) is older than <see cref="CleanupAge"/>.
    /// Returns the number of tasks purged.
    /// </summary>
    public int Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - CleanupAge;

        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM tasks
                WHERE status IN ('completed', 'cancelled')
                  AND COALESCE(last_executed_at_utc, created_at_utc) <= $cutoff
                """;
            cmd.Parameters.AddWithValue("$cutoff", FormatDto(cutoff));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Delete all tasks. Returns the number of tasks deleted.</summary>
    public int DeleteAll()
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM tasks";
            return cmd.ExecuteNonQuery();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void BindTaskParameters(SqliteCommand cmd, ScheduledTask task)
    {
        cmd.Parameters.AddWithValue("$id", task.Id);
        cmd.Parameters.AddWithValue("$description", task.Description);
        cmd.Parameters.AddWithValue("$messageText", task.MessageText);
        cmd.Parameters.AddWithValue("$scheduledAtUtc", FormatDto(task.ScheduledAtUtc));
        cmd.Parameters.AddWithValue("$cronExpression", (object?)task.CronExpression ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maxExecutions", task.MaxExecutions.HasValue ? task.MaxExecutions.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAtUtc", FormatDto(task.CreatedAtUtc));
        cmd.Parameters.AddWithValue("$status", task.Status.ToStorageValue());
        cmd.Parameters.AddWithValue("$lastExecutedAtUtc",
            task.LastExecutedAtUtc.HasValue ? FormatDto(task.LastExecutedAtUtc.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$nextExecutionUtc", FormatDto(task.NextExecutionUtc));
        cmd.Parameters.AddWithValue("$executionCount", task.ExecutionCount);
        cmd.Parameters.AddWithValue("$channelId", (object?)task.ChannelId ?? DBNull.Value);
    }

    private static List<ScheduledTask> ReadTasks(SqliteCommand cmd)
    {
        var tasks = new List<ScheduledTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tasks.Add(new ScheduledTask
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                Description = reader.GetString(reader.GetOrdinal("description")),
                MessageText = reader.GetString(reader.GetOrdinal("message_text")),
                ScheduledAtUtc = ParseDto(reader.GetString(reader.GetOrdinal("scheduled_at_utc"))),
                CronExpression = reader.IsDBNull(reader.GetOrdinal("cron_expression"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("cron_expression")),
                MaxExecutions = reader.IsDBNull(reader.GetOrdinal("max_executions"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("max_executions")),
                CreatedAtUtc = ParseDto(reader.GetString(reader.GetOrdinal("created_at_utc"))),
                Status = ScheduledTaskStatusExtensions.Parse(reader.GetString(reader.GetOrdinal("status"))),
                LastExecutedAtUtc = reader.IsDBNull(reader.GetOrdinal("last_executed_at_utc"))
                    ? null
                    : ParseDto(reader.GetString(reader.GetOrdinal("last_executed_at_utc"))),
                NextExecutionUtc = ParseDto(reader.GetString(reader.GetOrdinal("next_execution_utc"))),
                ExecutionCount = reader.GetInt32(reader.GetOrdinal("execution_count")),
                ChannelId = reader.IsDBNull(reader.GetOrdinal("channel_id"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("channel_id")),
            });
        }

        return tasks;
    }

    private static string FormatDto(DateTimeOffset dto)
        => SqliteDateTimeText.Format(dto);

    private static DateTimeOffset ParseDto(string s)
        => SqliteDateTimeText.Parse(s);
}

/// <summary>
/// Extension methods for <see cref="ScheduledTaskStatus"/> serialization to/from SQLite.
/// </summary>
public static class ScheduledTaskStatusExtensions
{
    public static string ToStorageValue(this ScheduledTaskStatus status) => status switch
    {
        ScheduledTaskStatus.Pending => "pending",
        ScheduledTaskStatus.Running => "running",
        ScheduledTaskStatus.Completed => "completed",
        ScheduledTaskStatus.Cancelled => "cancelled",
        _ => "pending",
    };

    public static ScheduledTaskStatus Parse(string value) => value switch
    {
        "pending" => ScheduledTaskStatus.Pending,
        "running" => ScheduledTaskStatus.Running,
        "completed" => ScheduledTaskStatus.Completed,
        "cancelled" => ScheduledTaskStatus.Cancelled,
        _ => ScheduledTaskStatus.Pending,
    };
}
