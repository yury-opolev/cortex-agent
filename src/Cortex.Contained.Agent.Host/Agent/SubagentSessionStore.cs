using System.Text.Json;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Persists subagent task state in SQLite. Supports CRUD operations,
/// crash recovery, and tiered retention cleanup.
/// </summary>
public sealed partial class SubagentSessionStore : SqliteStoreBase
{
    private readonly Lock syncLock = new();
    private readonly ILogger<SubagentSessionStore> logger;
    private readonly Timer cleanupTimer;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Cleanup runs every 6 hours.</summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    /// <summary>Full message history purged after 24 hours.</summary>
    private static readonly TimeSpan MessageRetention = TimeSpan.FromHours(24);

    /// <summary>Entire record purged after 7 days.</summary>
    private static readonly TimeSpan RecordRetention = TimeSpan.FromDays(7);

    /// <summary>Current schema version. Bump when adding migrations.</summary>
    private const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Initialises the subagent session store, opening the SQLite database at <paramref name="stateRoot"/>/subagents/subagents.db.
    /// </summary>
    public SubagentSessionStore(string stateRoot, ILogger<SubagentSessionStore> logger)
        : base(PrepareDatabasePath(stateRoot, "subagents", "subagents.db"), enableWalMode: true)
    {
        this.logger = logger;
        this.EnsureSchema();

        var resetCount = this.ResetRunningTasks();
        if (resetCount > 0)
        {
            this.LogCrashRecovery(resetCount);
        }

        this.cleanupTimer = new Timer(this.OnCleanupTick, null, CleanupInterval, CleanupInterval);
    }

    // ── Schema ───────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        var version = GetSchemaVersion();
        if (version < CurrentSchemaVersion)
        {
            ExecuteNonQuery("DROP TABLE IF EXISTS subagent_tasks");
            ExecuteNonQuery("DROP INDEX IF EXISTS idx_subagent_active");
            ExecuteNonQuery("DROP INDEX IF EXISTS idx_subagent_parent");

            ExecuteNonQuery("""
                CREATE TABLE subagent_tasks (
                    task_id              TEXT PRIMARY KEY,
                    parent_conversation  TEXT NOT NULL,
                    parent_channel       TEXT NOT NULL,
                    description          TEXT NOT NULL,
                    prompt               TEXT NOT NULL,
                    state                TEXT NOT NULL DEFAULT 'queued',
                    messages_json        TEXT NOT NULL DEFAULT '[]',
                    result               TEXT,
                    eval_response        TEXT,
                    created_at           TEXT NOT NULL,
                    completed_at         TEXT,
                    rounds               INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX idx_subagent_active
                    ON subagent_tasks (state)
                    WHERE state IN ('queued', 'running', 'revising');

                CREATE INDEX idx_subagent_parent
                    ON subagent_tasks (parent_conversation);
                """);

            this.SetSchemaVersion(CurrentSchemaVersion);
        }
    }

    // ── CRUD ─────────────────────────────────────────────────────────────

    /// <summary>Insert a new subagent task.</summary>
    public void Create(SubagentTask task)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO subagent_tasks
                    (task_id, parent_conversation, parent_channel, description, prompt,
                     state, messages_json, result, eval_response, created_at, completed_at, rounds)
                VALUES
                    ($taskId, $parentConversation, $parentChannel, $description, $prompt,
                     $state, $messagesJson, $result, $evalResponse, $createdAt, $completedAt, $rounds)
                """;
            BindParameters(cmd, task);
            cmd.ExecuteNonQuery();
        }

        this.LogTaskCreated(task.TaskId, task.Description);
    }

    /// <summary>Get a task by ID, or null if not found.</summary>
    public SubagentTask? GetById(string taskId)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM subagent_tasks WHERE task_id = $taskId";
            cmd.Parameters.AddWithValue("$taskId", taskId);
            var tasks = ReadTasks(cmd);
            return tasks.Count > 0 ? tasks[0] : null;
        }
    }

    /// <summary>Get all active tasks (queued, running, or revising).</summary>
    public IReadOnlyList<SubagentTask> GetActive()
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM subagent_tasks
                WHERE state IN ('queued', 'running', 'revising')
                ORDER BY created_at
                """;
            return ReadTasks(cmd);
        }
    }

    /// <summary>Get all tasks for a specific parent conversation, newest first.</summary>
    public IReadOnlyList<SubagentTask> GetByParent(string parentConversation)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM subagent_tasks
                WHERE parent_conversation = $parentConversation
                ORDER BY created_at DESC
                """;
            cmd.Parameters.AddWithValue("$parentConversation", parentConversation);
            return ReadTasks(cmd);
        }
    }

    /// <summary>Get the oldest queued task, or null if none.</summary>
    public SubagentTask? GetOldestQueued()
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM subagent_tasks
                WHERE state = 'queued'
                ORDER BY created_at
                LIMIT 1
                """;
            var tasks = ReadTasks(cmd);
            return tasks.Count > 0 ? tasks[0] : null;
        }
    }

    /// <summary>Update a task's state and optional result fields.</summary>
    public void UpdateState(string taskId, SubagentTaskState state, string? result = null, string? evalResponse = null)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE subagent_tasks
                SET state = $state,
                    result = COALESCE($result, result),
                    eval_response = COALESCE($evalResponse, eval_response),
                    completed_at = CASE WHEN $state IN ('completed', 'failed', 'cancelled') THEN $now ELSE completed_at END
                WHERE task_id = $taskId
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$state", state.ToStorageValue());
            cmd.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$evalResponse", (object?)evalResponse ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", FormatDto(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }

        var stateValue = state.ToStorageValue();
        this.LogTaskStateChanged(taskId, stateValue);
    }

    /// <summary>Persist the subagent's current message history and round count.</summary>
    public void UpdateMessages(string taskId, IReadOnlyList<LlmMessage> messages, int rounds)
    {
        var json = SerializeMessages(messages);

        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE subagent_tasks
                SET messages_json = $messagesJson, rounds = $rounds
                WHERE task_id = $taskId
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$messagesJson", json);
            cmd.Parameters.AddWithValue("$rounds", rounds);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Update a task's parent conversation/channel. Used when the user moves a
    /// conversation across channels via <c>transfer_session</c> — without
    /// repointing, the subagent's completion trigger would land in the
    /// original (now-abandoned) conversation. No-op if the task id is unknown.
    /// </summary>
    public void RepointParent(string taskId, string newParentConversation, string newParentChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newParentConversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(newParentChannel);

        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE subagent_tasks
                SET parent_conversation = $parentConversation,
                    parent_channel = $parentChannel
                WHERE task_id = $taskId
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$parentConversation", newParentConversation);
            cmd.Parameters.AddWithValue("$parentChannel", newParentChannel);
            var affected = cmd.ExecuteNonQuery();
            if (affected > 0)
            {
                this.LogTaskParentRepointed(taskId, newParentConversation, newParentChannel);
            }
        }
    }

    // ── Crash recovery ───────────────────────────────────────────────────

    /// <summary>
    /// Mark running/revising tasks as failed on startup.
    /// In-memory runner state is lost after a restart, so these cannot be resumed.
    /// Queued tasks are left as-is — they are picked up by <see cref="StartQueuedTasks"/>.
    /// </summary>
    public int ResetRunningTasks()
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE subagent_tasks
                SET state = 'failed', completed_at = $now
                WHERE state IN ('running', 'revising')
                """;
            cmd.Parameters.AddWithValue("$now", FormatDto(DateTimeOffset.UtcNow));
            return cmd.ExecuteNonQuery();
        }
    }

    // ── Cleanup ──────────────────────────────────────────────────────────

    private void OnCleanupTick(object? state)
    {
        try
        {
            var purged = Cleanup();
            if (purged > 0)
            {
                this.LogCleanupCompleted(purged);
            }
        }
#pragma warning disable CA1031 // Cleanup must not crash the timer
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogCleanupFailed(ex.Message);
        }
    }

    /// <summary>
    /// Tiered retention cleanup:
    /// 1. Tasks completed &gt; 24h ago: clear messages_json (keep summary).
    /// 2. Tasks completed &gt; 7d ago: delete entirely.
    /// Returns total number of records affected.
    /// </summary>
    public int Cleanup()
    {
        var messageCutoff = DateTimeOffset.UtcNow - MessageRetention;
        var recordCutoff = DateTimeOffset.UtcNow - RecordRetention;

        lock (this.syncLock)
        {
            using var cmd1 = this.Connection.CreateCommand();
            cmd1.CommandText = """
                UPDATE subagent_tasks
                SET messages_json = '[]'
                WHERE state IN ('completed', 'failed', 'cancelled')
                  AND completed_at IS NOT NULL
                  AND completed_at <= $cutoff
                  AND messages_json != '[]'
                """;
            cmd1.Parameters.AddWithValue("$cutoff", FormatDto(messageCutoff));
            var messagesCleared = cmd1.ExecuteNonQuery();

            using var cmd2 = this.Connection.CreateCommand();
            cmd2.CommandText = """
                DELETE FROM subagent_tasks
                WHERE state IN ('completed', 'failed', 'cancelled')
                  AND completed_at IS NOT NULL
                  AND completed_at <= $cutoff
                """;
            cmd2.Parameters.AddWithValue("$cutoff", FormatDto(recordCutoff));
            var recordsDeleted = cmd2.ExecuteNonQuery();

            return messagesCleared + recordsDeleted;
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        this.cleanupTimer.Dispose();
        base.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void BindParameters(SqliteCommand cmd, SubagentTask task)
    {
        cmd.Parameters.AddWithValue("$taskId", task.TaskId);
        cmd.Parameters.AddWithValue("$parentConversation", task.ParentConversation);
        cmd.Parameters.AddWithValue("$parentChannel", task.ParentChannel);
        cmd.Parameters.AddWithValue("$description", task.Description);
        cmd.Parameters.AddWithValue("$prompt", task.Prompt);
        cmd.Parameters.AddWithValue("$state", task.State.ToStorageValue());
        cmd.Parameters.AddWithValue("$messagesJson", SerializeMessages(task.Messages));
        cmd.Parameters.AddWithValue("$result", (object?)task.Result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$evalResponse", (object?)task.EvalResponse ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", FormatDto(task.CreatedAt));
        cmd.Parameters.AddWithValue("$completedAt",
            task.CompletedAt.HasValue ? FormatDto(task.CompletedAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$rounds", task.Rounds);
    }

    private static List<SubagentTask> ReadTasks(SqliteCommand cmd)
    {
        var tasks = new List<SubagentTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tasks.Add(new SubagentTask
            {
                TaskId = reader.GetString(reader.GetOrdinal("task_id")),
                ParentConversation = reader.GetString(reader.GetOrdinal("parent_conversation")),
                ParentChannel = reader.GetString(reader.GetOrdinal("parent_channel")),
                Description = reader.GetString(reader.GetOrdinal("description")),
                Prompt = reader.GetString(reader.GetOrdinal("prompt")),
                State = SubagentTaskStateExtensions.Parse(reader.GetString(reader.GetOrdinal("state"))),
                Messages = DeserializeMessages(reader.GetString(reader.GetOrdinal("messages_json"))),
                Result = reader.IsDBNull(reader.GetOrdinal("result"))
                    ? null : reader.GetString(reader.GetOrdinal("result")),
                EvalResponse = reader.IsDBNull(reader.GetOrdinal("eval_response"))
                    ? null : reader.GetString(reader.GetOrdinal("eval_response")),
                CreatedAt = ParseDto(reader.GetString(reader.GetOrdinal("created_at"))),
                CompletedAt = reader.IsDBNull(reader.GetOrdinal("completed_at"))
                    ? null : ParseDto(reader.GetString(reader.GetOrdinal("completed_at"))),
                Rounds = reader.GetInt32(reader.GetOrdinal("rounds")),
            });
        }

        return tasks;
    }

    private static string SerializeMessages(IReadOnlyList<LlmMessage> messages)
        => JsonSerializer.Serialize(messages, s_jsonOptions);

    private static List<LlmMessage> DeserializeMessages(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<LlmMessage>>(json, s_jsonOptions) ?? [];
    }

    private static string FormatDto(DateTimeOffset dto)
        => SqliteDateTimeText.Format(dto);

    private static DateTimeOffset ParseDto(string s)
        => SqliteDateTimeText.Parse(s);

    // ── Logging ──────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-store] Task {TaskId} created: {Description}")]
    private partial void LogTaskCreated(string taskId, string description);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-store] Task {TaskId} state changed to {State}")]
    private partial void LogTaskStateChanged(string taskId, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-store] Crash recovery: reset {Count} orphaned tasks to failed")]
    private partial void LogCrashRecovery(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-store] Cleanup completed: {Count} records affected")]
    private partial void LogCleanupCompleted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-store] Cleanup failed: {ErrorMessage}")]
    private partial void LogCleanupFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-store] Task {TaskId} repointed to parent {ParentConversation} / channel {ParentChannel}")]
    private partial void LogTaskParentRepointed(string taskId, string parentConversation, string parentChannel);
}
