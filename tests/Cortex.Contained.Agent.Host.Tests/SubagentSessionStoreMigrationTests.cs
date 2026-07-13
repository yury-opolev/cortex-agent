using System.Globalization;
using Cortex.Contained.Agent.Host.Agent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Verifies the v1 → v2 subagent schema migration is additive and non-destructive:
/// existing tasks survive, interrupted work is requeued, and historical terminal
/// tasks are marked delivered so they are never re-announced after an upgrade.
/// </summary>
public class SubagentSessionStoreMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public SubagentSessionStoreMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "subagent-migration-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ── Migration from v1 ────────────────────────────────────────────────

    [Fact]
    public void Constructor_V1Database_MigratesWithoutDroppingTasks()
    {
        SeedV1Database(conn =>
        {
            InsertV1Task(conn, "sa-queued", "queued");
            InsertV1Task(conn, "sa-running", "running", messagesJson: """[{"role":"user","content":"hi"}]""");
            InsertV1Task(conn, "sa-revising", "revising", messagesJson: """[{"role":"user","content":"hi"}]""");
            InsertV1Task(conn, "sa-completed", "completed", completedAt: "2026-07-01T11:00:00.0000000Z", result: "ok");
            InsertV1Task(conn, "sa-failed", "failed", completedAt: "2026-07-01T11:00:00.0000000Z");
            InsertV1Task(conn, "sa-cancelled", "cancelled", completedAt: "2026-07-01T11:00:00.0000000Z");
        });

        using var store = CreateStore();

        Assert.NotNull(store.GetById("sa-queued"));
        Assert.NotNull(store.GetById("sa-running"));
        Assert.NotNull(store.GetById("sa-revising"));
        Assert.NotNull(store.GetById("sa-completed"));
        Assert.NotNull(store.GetById("sa-failed"));
        Assert.NotNull(store.GetById("sa-cancelled"));
        Assert.Equal(2, ReadUserVersion());
        Assert.Equal("ok", store.GetById("sa-completed")!.Result);
    }

    [Fact]
    public void Constructor_V1Database_InfersResumeModeFromMessages()
    {
        SeedV1Database(conn =>
        {
            InsertV1Task(conn, "sa-fresh", "queued");
            InsertV1Task(conn, "sa-history", "running", messagesJson: """[{"role":"user","content":"hi"}]""");
        });

        using var store = CreateStore();

        Assert.Equal(SubagentRunMode.New, store.GetById("sa-fresh")!.RunMode);
        Assert.Equal(SubagentRunMode.Resume, store.GetById("sa-history")!.RunMode);
    }

    [Fact]
    public void Constructor_V1Database_RequeuesInterruptedTasks()
    {
        SeedV1Database(conn =>
        {
            InsertV1Task(conn, "sa-running", "running", messagesJson: """[{"role":"user","content":"hi"}]""");
            InsertV1Task(conn, "sa-revising", "revising", messagesJson: """[{"role":"user","content":"hi"}]""");
        });

        using var store = CreateStore();

        var running = store.GetById("sa-running");
        var revising = store.GetById("sa-revising");
        Assert.NotNull(running);
        Assert.NotNull(revising);
        Assert.Equal(SubagentTaskState.Queued, running.State);
        Assert.Equal(SubagentTaskState.Queued, revising.State);
        Assert.Null(running.CompletedAt);
        Assert.Equal(SubagentRunMode.Resume, running.RunMode);
        Assert.Equal(SubagentRunMode.Resume, revising.RunMode);
    }

    [Fact]
    public void Constructor_V1Database_DoesNotReplayHistoricalTerminalTasks()
    {
        SeedV1Database(conn =>
        {
            InsertV1Task(conn, "sa-completed", "completed", completedAt: "2026-07-01T11:00:00.0000000Z", result: "ok");
            InsertV1Task(conn, "sa-failed", "failed", completedAt: "2026-07-01T11:00:00.0000000Z");
            InsertV1Task(conn, "sa-cancelled", "cancelled", completedAt: "2026-07-01T11:00:00.0000000Z");
        });

        using var store = CreateStore();

        // Terminal states are untouched and their (already delivered pre-migration)
        // results are marked delivered so the notification pump never re-announces them.
        Assert.Equal(SubagentTaskState.Completed, store.GetById("sa-completed")!.State);
        Assert.Equal(SubagentTaskState.Failed, store.GetById("sa-failed")!.State);
        Assert.Equal(SubagentTaskState.Cancelled, store.GetById("sa-cancelled")!.State);
        Assert.Equal(SubagentNotificationState.Delivered, store.GetById("sa-completed")!.NotificationState);
        Assert.Equal(SubagentNotificationState.Delivered, store.GetById("sa-failed")!.NotificationState);
        Assert.Equal(SubagentNotificationState.Delivered, store.GetById("sa-cancelled")!.NotificationState);
        Assert.Null(store.TryClaimOldestPendingNotification());
        Assert.Null(store.TryClaimOldestQueued());
    }

    // ── Fresh database ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_NewDatabase_CreatesVersion2Schema()
    {
        using (var store = CreateStore())
        {
            var task = new SubagentTask
            {
                TaskId = "sa-v2",
                ParentConversation = "conv-1",
                ParentChannel = "webchat-default",
                Description = "V2 round trip",
                Prompt = "do things",
                State = SubagentTaskState.Queued,
                RunMode = SubagentRunMode.Resume,
                SkillName = "deep-research",
                NotificationState = SubagentNotificationState.Pending,
                NotificationAttempts = 2,
                NotificationUpdatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastProgressAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                RestartCount = 3,
            };
            store.Create(task);

            var reloaded = store.GetById("sa-v2");
            Assert.NotNull(reloaded);
            Assert.Equal(SubagentRunMode.Resume, reloaded.RunMode);
            Assert.Equal("deep-research", reloaded.SkillName);
            Assert.Equal(SubagentNotificationState.Pending, reloaded.NotificationState);
            Assert.Equal(2, reloaded.NotificationAttempts);
            Assert.NotNull(reloaded.NotificationUpdatedAt);
            Assert.NotNull(reloaded.StartedAt);
            Assert.Equal(3, reloaded.RestartCount);
        }

        Assert.Equal(2, ReadUserVersion());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private SubagentSessionStore CreateStore()
        => new(_tempDir, NullLogger<SubagentSessionStore>.Instance);

    private string DatabasePath => Path.Combine(_tempDir, "subagents", "subagents.db");

    /// <summary>Creates a v1-schema database exactly as the pre-migration store would have.</summary>
    private void SeedV1Database(Action<SqliteConnection> seed)
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "subagents"));
        using var conn = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
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

                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        seed(conn);
    }

    private static void InsertV1Task(
        SqliteConnection conn,
        string taskId,
        string state,
        string messagesJson = "[]",
        string? completedAt = null,
        string? result = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO subagent_tasks
                (task_id, parent_conversation, parent_channel, description, prompt,
                 state, messages_json, result, eval_response, created_at, completed_at, rounds)
            VALUES
                ($taskId, 'conv-1', 'webchat-default', 'v1 task', 'do things',
                 $state, $messagesJson, $result, NULL, '2026-07-01T10:00:00.0000000Z', $completedAt, 0)
            """;
        cmd.Parameters.AddWithValue("$taskId", taskId);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$messagesJson", messagesJson);
        cmd.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completedAt", (object?)completedAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private int ReadUserVersion()
    {
        using var conn = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
