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
    private const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Initialises the subagent session store, opening the SQLite database at <paramref name="stateRoot"/>/subagents/subagents.db.
    /// </summary>
    public SubagentSessionStore(string stateRoot, ILogger<SubagentSessionStore> logger)
        : base(PrepareDatabasePath(stateRoot, "subagents", "subagents.db"), enableWalMode: true)
    {
        this.logger = logger;
        this.EnsureSchema();

        var recoveredCount = this.RecoverInterruptedWork();
        if (recoveredCount > 0)
        {
            this.LogCrashRecovery(recoveredCount);
        }

        this.cleanupTimer = new Timer(this.OnCleanupTick, null, CleanupInterval, CleanupInterval);
    }

    // ── Schema ───────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        var version = GetSchemaVersion();
        if (version == 0)
        {
            this.CreateVersion2Schema();
            return;
        }

        if (version == 1)
        {
            this.MigrateVersion1ToVersion2();
            this.SetSchemaVersion(CurrentSchemaVersion);
            return;
        }

        if (version != CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported subagent schema version {version}.");
        }
    }

    /// <summary>
    /// Creates the complete v2 schema on a fresh (version 0) database. Crash-idempotent:
    /// every CREATE uses IF NOT EXISTS and the create + <c>user_version</c> stamp run in one
    /// transaction, so a crash mid-bootstrap on a fresh DB re-runs cleanly instead of
    /// crash-looping on "table already exists".
    /// </summary>
    private void CreateVersion2Schema()
    {
        using var transaction = this.Connection.BeginTransaction();
        using var cmd = this.Connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS subagent_tasks (
                task_id                 TEXT PRIMARY KEY,
                parent_conversation     TEXT NOT NULL,
                parent_channel          TEXT NOT NULL,
                description             TEXT NOT NULL,
                prompt                  TEXT NOT NULL,
                state                   TEXT NOT NULL DEFAULT 'queued',
                messages_json           TEXT NOT NULL DEFAULT '[]',
                result                  TEXT,
                eval_response           TEXT,
                created_at              TEXT NOT NULL,
                completed_at            TEXT,
                rounds                  INTEGER NOT NULL DEFAULT 0,
                run_mode                TEXT NOT NULL DEFAULT 'new',
                skill_name              TEXT,
                notification_state      TEXT NOT NULL DEFAULT 'none',
                notification_attempts   INTEGER NOT NULL DEFAULT 0,
                notification_updated_at TEXT,
                started_at              TEXT,
                last_progress_at        TEXT NOT NULL,
                restart_count           INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_subagent_active
                ON subagent_tasks (state)
                WHERE state IN ('queued', 'running', 'revising');

            CREATE INDEX IF NOT EXISTS idx_subagent_parent
                ON subagent_tasks (parent_conversation);

            CREATE INDEX IF NOT EXISTS idx_subagent_queue
                ON subagent_tasks(state, created_at)
                WHERE state = 'queued';

            CREATE INDEX IF NOT EXISTS idx_subagent_notifications
                ON subagent_tasks(notification_state, completed_at)
                WHERE notification_state IN ('pending', 'enqueued');

            PRAGMA user_version = 2;
            """;
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Migrates a v1 database to v2 non-destructively: additive ALTER TABLE statements,
    /// backfill of the new columns, and requeue of interrupted work — all in one transaction.
    /// Terminal tasks are marked delivered so historical results are never re-announced.
    /// </summary>
    private void MigrateVersion1ToVersion2()
    {
        using var transaction = this.Connection.BeginTransaction();
        using var cmd = this.Connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            ALTER TABLE subagent_tasks ADD COLUMN run_mode TEXT NOT NULL DEFAULT 'new';
            ALTER TABLE subagent_tasks ADD COLUMN skill_name TEXT;
            ALTER TABLE subagent_tasks ADD COLUMN notification_state TEXT NOT NULL DEFAULT 'none';
            ALTER TABLE subagent_tasks ADD COLUMN notification_attempts INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE subagent_tasks ADD COLUMN notification_updated_at TEXT;
            ALTER TABLE subagent_tasks ADD COLUMN started_at TEXT;
            ALTER TABLE subagent_tasks ADD COLUMN last_progress_at TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000Z';
            ALTER TABLE subagent_tasks ADD COLUMN restart_count INTEGER NOT NULL DEFAULT 0;

            UPDATE subagent_tasks
            SET last_progress_at = created_at;

            UPDATE subagent_tasks
            SET run_mode = CASE
                WHEN state = 'revising' OR messages_json <> '[]' THEN 'resume'
                ELSE 'new'
            END;

            UPDATE subagent_tasks
            SET state = 'queued',
                run_mode = CASE WHEN messages_json <> '[]' THEN 'resume' ELSE run_mode END,
                completed_at = NULL
            WHERE state IN ('running', 'revising');

            UPDATE subagent_tasks
            SET notification_state = 'delivered'
            WHERE state IN ('completed', 'failed', 'cancelled');

            CREATE INDEX IF NOT EXISTS idx_subagent_queue
                ON subagent_tasks(state, created_at)
                WHERE state = 'queued';

            CREATE INDEX IF NOT EXISTS idx_subagent_notifications
                ON subagent_tasks(notification_state, completed_at)
                WHERE notification_state IN ('pending', 'enqueued');

            PRAGMA user_version = 2;
            """;
        cmd.ExecuteNonQuery();
        transaction.Commit();

        this.LogSchemaMigrated(1, CurrentSchemaVersion);
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
                     state, messages_json, result, eval_response, created_at, completed_at, rounds,
                     run_mode, skill_name, notification_state, notification_attempts,
                     notification_updated_at, started_at, last_progress_at, restart_count)
                VALUES
                    ($taskId, $parentConversation, $parentChannel, $description, $prompt,
                     $state, $messagesJson, $result, $evalResponse, $createdAt, $completedAt, $rounds,
                     $runMode, $skillName, $notificationState, $notificationAttempts,
                     $notificationUpdatedAt, $startedAt, $lastProgressAt, $restartCount)
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

    /// <summary>
    /// Get the most-recently-created tasks (newest first), bounded by <paramref name="limit"/>.
    /// When <paramref name="includeTerminal"/> is false, only active (queued/running/revising)
    /// tasks are returned. Used by <c>SubagentObservabilityService</c> to page the generic
    /// operational-observability worker list; unlike <see cref="GetActive"/> this is bounded
    /// and can include terminal history.
    /// </summary>
    public IReadOnlyList<SubagentTask> GetRecent(int limit, bool includeTerminal)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = includeTerminal
                ? """
                    SELECT * FROM subagent_tasks
                    ORDER BY created_at DESC
                    LIMIT $limit
                    """
                : """
                    SELECT * FROM subagent_tasks
                    WHERE state IN ('queued', 'running', 'revising')
                    ORDER BY created_at DESC
                    LIMIT $limit
                    """;
            cmd.Parameters.AddWithValue("$limit", limit);
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

    /// <summary>
    /// Atomically claim the oldest queued task: transition it to Running and return it
    /// (now Running), or null if none are queued. The SELECT and the UPDATE happen under
    /// a single lock so two concurrent dequeue callers can never claim the same task.
    /// </summary>
    public SubagentTask? TryClaimOldestQueued()
    {
        lock (this.syncLock)
        {
            using var selectCmd = this.Connection.CreateCommand();
            selectCmd.CommandText = """
                SELECT * FROM subagent_tasks
                WHERE state = 'queued'
                ORDER BY created_at
                LIMIT 1
                """;
            var tasks = ReadTasks(selectCmd);
            if (tasks.Count == 0)
            {
                return null;
            }

            var task = tasks[0];

            var now = DateTimeOffset.UtcNow;
            using var updateCmd = this.Connection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE subagent_tasks
                SET state = 'running',
                    started_at = COALESCE(started_at, $now),
                    last_progress_at = $now
                WHERE task_id = $taskId
                """;
            updateCmd.Parameters.AddWithValue("$taskId", task.TaskId);
            updateCmd.Parameters.AddWithValue("$now", FormatDto(now));
            updateCmd.ExecuteNonQuery();

            task.State = SubagentTaskState.Running;
            task.StartedAt ??= now;
            task.LastProgressAt = now;
            this.LogTaskStateChanged(task.TaskId, "running");
            return task;
        }
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
    /// Requeue interrupted work after a restart instead of failing it:
    /// running/revising tasks go back to queued (resuming from persisted history when
    /// any exists), and notifications that were claimed but never confirmed delivered
    /// are released back to pending so the result is re-announced.
    /// Returns the total number of tasks requeued plus notifications released.
    /// </summary>
    public int RecoverInterruptedWork()
    {
        lock (this.syncLock)
        {
            var now = FormatDto(DateTimeOffset.UtcNow);

            using var transaction = this.Connection.BeginTransaction();

            using var requeueCmd = this.Connection.CreateCommand();
            requeueCmd.Transaction = transaction;
            requeueCmd.CommandText = """
                UPDATE subagent_tasks
                SET state = 'queued',
                    run_mode = CASE
                        WHEN state = 'revising' OR messages_json <> '[]' THEN 'resume'
                        ELSE run_mode
                    END,
                    completed_at = NULL,
                    restart_count = restart_count + 1,
                    last_progress_at = $now
                WHERE state IN ('running', 'revising')
                """;
            requeueCmd.Parameters.AddWithValue("$now", now);
            var requeued = requeueCmd.ExecuteNonQuery();

            using var releaseCmd = this.Connection.CreateCommand();
            releaseCmd.Transaction = transaction;
            releaseCmd.CommandText = """
                UPDATE subagent_tasks
                SET notification_state = 'pending',
                    notification_updated_at = $now
                WHERE notification_state = 'enqueued'
                """;
            releaseCmd.Parameters.AddWithValue("$now", now);
            var released = releaseCmd.ExecuteNonQuery();

            transaction.Commit();
            return requeued + released;
        }
    }

    /// <summary>
    /// Append a user message to the task's history and queue it for a resume run.
    /// Clears the terminal markers (completed_at, notification state) in the same
    /// transaction so the task re-enters the dispatch queue cleanly.
    /// Returns false when the task id is unknown.
    /// </summary>
    public bool TryQueueResume(string taskId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(message);

        lock (this.syncLock)
        {
            using var transaction = this.Connection.BeginTransaction();

            using var selectCmd = this.Connection.CreateCommand();
            selectCmd.Transaction = transaction;
            selectCmd.CommandText = "SELECT messages_json FROM subagent_tasks WHERE task_id = $taskId";
            selectCmd.Parameters.AddWithValue("$taskId", taskId);
            var messagesJson = selectCmd.ExecuteScalar() as string;
            if (messagesJson is null)
            {
                return false;
            }

            var messages = DeserializeMessages(messagesJson);
            messages.Add(new LlmMessage { Role = "user", Content = message });

            using var updateCmd = this.Connection.CreateCommand();
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = """
                UPDATE subagent_tasks
                SET messages_json = $messagesJson,
                    state = 'queued',
                    run_mode = 'resume',
                    completed_at = NULL,
                    notification_state = 'none',
                    notification_attempts = 0,
                    notification_updated_at = NULL,
                    last_progress_at = $now
                WHERE task_id = $taskId
                """;
            updateCmd.Parameters.AddWithValue("$taskId", taskId);
            updateCmd.Parameters.AddWithValue("$messagesJson", SerializeMessages(messages));
            updateCmd.Parameters.AddWithValue("$now", FormatDto(DateTimeOffset.UtcNow));
            updateCmd.ExecuteNonQuery();

            transaction.Commit();
        }

        this.LogTaskStateChanged(taskId, "queued (resume)");
        return true;
    }

    /// <summary>
    /// Record a terminal result and mark its notification pending — but only when the
    /// task is not already terminal. A failed or cancelled task can never be
    /// overwritten as completed (and vice versa); the first terminal write wins.
    /// Returns false when the task is unknown or already terminal.
    /// </summary>
    public bool TrySetTerminalResult(string taskId, SubagentExecutionResult executionResult)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(executionResult);
        if (executionResult.TerminalState is not (SubagentTaskState.Completed
            or SubagentTaskState.Failed
            or SubagentTaskState.Cancelled))
        {
            throw new ArgumentException(
                $"State '{executionResult.TerminalState}' is not terminal.", nameof(executionResult));
        }

        var terminalStateValue = executionResult.TerminalState.ToStorageValue();

        int affected;
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE subagent_tasks
                SET state = $state,
                    result = $result,
                    completed_at = $now,
                    last_progress_at = $now,
                    notification_state = 'pending',
                    notification_updated_at = $now
                WHERE task_id = $taskId
                  AND state NOT IN ('completed', 'failed', 'cancelled')
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$state", terminalStateValue);
            cmd.Parameters.AddWithValue("$result", executionResult.Result);
            cmd.Parameters.AddWithValue("$now", FormatDto(DateTimeOffset.UtcNow));
            affected = cmd.ExecuteNonQuery();
        }

        if (affected > 0)
        {
            this.LogTaskStateChanged(taskId, terminalStateValue);
        }

        return affected > 0;
    }

    /// <summary>
    /// Put a task back into the dispatch queue (e.g. when a claimed task could not
    /// actually start). Clears completed_at and counts the restart. A task that already
    /// reached a terminal state (completed/failed/cancelled) is never resurrected —
    /// same guard as <see cref="TrySetTerminalResult"/>, so a user-cancelled task whose
    /// runner unwinds during host shutdown stays cancelled. Returns false when the task
    /// is unknown or already terminal.
    /// </summary>
    public bool Requeue(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        int affected;
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE subagent_tasks
                SET state = 'queued',
                    completed_at = NULL,
                    restart_count = restart_count + 1,
                    last_progress_at = $now
                WHERE task_id = $taskId
                  AND state NOT IN ('completed', 'failed', 'cancelled')
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$now", FormatDto(DateTimeOffset.UtcNow));
            affected = cmd.ExecuteNonQuery();
        }

        if (affected > 0)
        {
            this.LogTaskStateChanged(taskId, "queued");
        }

        return affected > 0;
    }

    // ── Notification delivery ────────────────────────────────────────────

    /// <summary>Base delay before the second (and later) redelivery retry of a failing notification.</summary>
    private static readonly TimeSpan NotificationRetryBaseDelay = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound on the exponential redelivery backoff.</summary>
    private static readonly TimeSpan NotificationRetryMaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Atomically claim the oldest ELIGIBLE pending terminal-result notification: transition it
    /// to enqueued, increment the attempt counter, and return the task — or null when nothing is
    /// pending or every pending notification is still inside its redelivery backoff window.
    /// SELECT and UPDATE run in one transaction under the lock so two delivery workers can never
    /// claim the same notification.
    ///
    /// Bounded backoff (I2): the first delivery and the first retry are immediate, but each further
    /// attempt of a persistently-failing notification is skipped until an exponentially growing,
    /// capped delay has elapsed since its last attempt — so a parent turn that keeps failing (LLM
    /// rate-limit/quota) can never hot-loop paid redeliveries. See <see cref="IsNotificationRetryDue"/>.
    /// </summary>
    public SubagentTask? TryClaimOldestPendingNotification()
    {
        lock (this.syncLock)
        {
            using var transaction = this.Connection.BeginTransaction();

            using var selectCmd = this.Connection.CreateCommand();
            selectCmd.Transaction = transaction;
            selectCmd.CommandText = """
                SELECT * FROM subagent_tasks
                WHERE notification_state = 'pending'
                ORDER BY completed_at, created_at
                """;
            var now = DateTimeOffset.UtcNow;
            var pending = ReadTasks(selectCmd);
            var task = pending.FirstOrDefault(t => IsNotificationRetryDue(t, now));
            if (task is null)
            {
                return null;
            }

            using var updateCmd = this.Connection.CreateCommand();
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = """
                UPDATE subagent_tasks
                SET notification_state = 'enqueued',
                    notification_attempts = notification_attempts + 1,
                    notification_updated_at = $now
                WHERE task_id = $taskId
                """;
            updateCmd.Parameters.AddWithValue("$taskId", task.TaskId);
            updateCmd.Parameters.AddWithValue("$now", FormatDto(now));
            updateCmd.ExecuteNonQuery();

            transaction.Commit();

            task.NotificationState = SubagentNotificationState.Enqueued;
            task.NotificationAttempts += 1;
            task.NotificationUpdatedAt = now;
            return task;
        }
    }

    /// <summary>
    /// Whether enough time has elapsed since a pending notification's last attempt for it to be
    /// (re)claimed. The first delivery (<c>attempts == 0</c>) and the first retry
    /// (<c>attempts == 1</c>) are always due — a single transient failure redelivers immediately.
    /// From the second retry on (<c>attempts &gt;= 2</c>) the notification is throttled by an
    /// exponential, capped backoff — <c>min(base·2^(attempts-2), max)</c> since
    /// <c>notification_updated_at</c> — so a persistently-failing parent turn is spread out instead
    /// of hot-looped. A missing timestamp is treated as immediately due.
    /// </summary>
    private static bool IsNotificationRetryDue(SubagentTask task, DateTimeOffset now)
    {
        if (task.NotificationAttempts <= 1 || task.NotificationUpdatedAt is null)
        {
            return true;
        }

        // attempts == 2 → base, == 3 → base·2, == 4 → base·4, … capped at max.
        var exponent = Math.Min(task.NotificationAttempts - 2, 20);
        var scaledTicks = NotificationRetryBaseDelay.Ticks * (1L << exponent);
        var delay = TimeSpan.FromTicks(Math.Min(scaledTicks, NotificationRetryMaxDelay.Ticks));
        return now - task.NotificationUpdatedAt.Value >= delay;
    }

    /// <summary>
    /// Confirm a claimed (or still pending) notification was delivered to the parent
    /// conversation. Returns false when the task is unknown or already delivered.
    /// </summary>
    public bool MarkNotificationDelivered(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE subagent_tasks
                SET notification_state = 'delivered',
                    notification_updated_at = $now
                WHERE task_id = $taskId
                  AND notification_state IN ('pending', 'enqueued')
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$now", FormatDto(DateTimeOffset.UtcNow));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Release a claimed notification back to pending after a failed delivery attempt
    /// so it can be claimed again. Returns false when the task is not enqueued.
    /// </summary>
    public bool ReleaseNotification(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE subagent_tasks
                SET notification_state = 'pending',
                    notification_updated_at = $now
                WHERE task_id = $taskId
                  AND notification_state = 'enqueued'
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$now", FormatDto(DateTimeOffset.UtcNow));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ── Progress & transfer queries ──────────────────────────────────────

    /// <summary>Record that the task made observable progress (stall detection input).</summary>
    public void TouchProgress(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE subagent_tasks
                SET last_progress_at = $now
                WHERE task_id = $taskId
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$now", FormatDto(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Tasks whose parent pointer is still meaningful for a cross-channel session
    /// transfer: everything active, plus terminal tasks whose result has not been
    /// delivered yet (their completion notification still has to land somewhere).
    /// </summary>
    public IReadOnlyList<SubagentTask> GetTransferableTasks()
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM subagent_tasks
                WHERE state IN ('queued', 'running', 'revising')
                   OR notification_state IN ('pending', 'enqueued')
                ORDER BY created_at
                """;
            return ReadTasks(cmd);
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
                  AND notification_state IN ('none', 'delivered')
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
                  AND notification_state IN ('none', 'delivered')
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
        cmd.Parameters.AddWithValue("$runMode", task.RunMode.ToStorageValue());
        cmd.Parameters.AddWithValue("$skillName", (object?)task.SkillName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$notificationState", task.NotificationState.ToStorageValue());
        cmd.Parameters.AddWithValue("$notificationAttempts", task.NotificationAttempts);
        cmd.Parameters.AddWithValue("$notificationUpdatedAt",
            task.NotificationUpdatedAt.HasValue ? FormatDto(task.NotificationUpdatedAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$startedAt",
            task.StartedAt.HasValue ? FormatDto(task.StartedAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$lastProgressAt", FormatDto(task.LastProgressAt));
        cmd.Parameters.AddWithValue("$restartCount", task.RestartCount);
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
                RunMode = SubagentRunModeExtensions.Parse(reader.GetString(reader.GetOrdinal("run_mode"))),
                SkillName = reader.IsDBNull(reader.GetOrdinal("skill_name"))
                    ? null : reader.GetString(reader.GetOrdinal("skill_name")),
                NotificationState = SubagentNotificationStateExtensions.Parse(
                    reader.GetString(reader.GetOrdinal("notification_state"))),
                NotificationAttempts = reader.GetInt32(reader.GetOrdinal("notification_attempts")),
                NotificationUpdatedAt = reader.IsDBNull(reader.GetOrdinal("notification_updated_at"))
                    ? null : ParseDto(reader.GetString(reader.GetOrdinal("notification_updated_at"))),
                StartedAt = reader.IsDBNull(reader.GetOrdinal("started_at"))
                    ? null : ParseDto(reader.GetString(reader.GetOrdinal("started_at"))),
                LastProgressAt = ParseDto(reader.GetString(reader.GetOrdinal("last_progress_at"))),
                RestartCount = reader.GetInt32(reader.GetOrdinal("restart_count")),
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-store] Crash recovery: requeued/released {Count} interrupted tasks and notifications")]
    private partial void LogCrashRecovery(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-store] Schema migrated from v{FromVersion} to v{ToVersion} (non-destructive)")]
    private partial void LogSchemaMigrated(int fromVersion, int toVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-store] Cleanup completed: {Count} records affected")]
    private partial void LogCleanupCompleted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-store] Cleanup failed: {ErrorMessage}")]
    private partial void LogCleanupFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-store] Task {TaskId} repointed to parent {ParentConversation} / channel {ParentChannel}")]
    private partial void LogTaskParentRepointed(string taskId, string parentConversation, string parentChannel);
}
