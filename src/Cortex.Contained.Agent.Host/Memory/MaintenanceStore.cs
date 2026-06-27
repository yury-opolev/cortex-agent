using System.Globalization;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Persists maintenance task run history in SQLite so that nightly tasks
/// (compaction, etc.) can detect missed runs after a restart
/// and catch up gradually.
/// <para>
/// Schema:
/// <list type="bullet">
/// <item><b>maintenance_runs</b> — append-only log of every run (success or failure).</item>
/// <item><b>maintenance_state</b> — one row per task with the latest completed date
///   (the "day" that was handled, not the wall-clock time of execution).</item>
/// </list>
/// </para>
/// </summary>
public sealed class MaintenanceStore : SqliteStoreBase
{
    private readonly Lock syncLock = new();

    private const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Initialises the maintenance store, opening the SQLite database at <paramref name="dataPath"/>/maintenance/maintenance.db.
    /// </summary>
    public MaintenanceStore(string dataPath)
        : base(PrepareDatabasePath(dataPath, "maintenance", "maintenance.db"), enableWalMode: false)
    {
        this.EnsureSchema();
    }

    // ── Schema ──────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        var version = GetSchemaVersion();
        if (version < CurrentSchemaVersion)
        {
            ExecuteNonQuery("DROP TABLE IF EXISTS maintenance_runs");
            ExecuteNonQuery("DROP TABLE IF EXISTS maintenance_state");

            ExecuteNonQuery("""
                CREATE TABLE maintenance_state (
                    task_name           TEXT PRIMARY KEY,
                    last_completed_date TEXT NOT NULL,
                    updated_at_utc      TEXT NOT NULL
                );

                CREATE TABLE maintenance_runs (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_name       TEXT NOT NULL,
                    target_date     TEXT NOT NULL,
                    started_at_utc  TEXT NOT NULL,
                    finished_at_utc TEXT,
                    status          TEXT NOT NULL DEFAULT 'running',
                    error_message   TEXT
                );

                CREATE INDEX idx_runs_task_date
                    ON maintenance_runs (task_name, target_date);
                """);

            this.SetSchemaVersion(CurrentSchemaVersion);
        }
    }

    // ── State (latest completed date per task) ──────────────────────────

    /// <summary>
    /// Gets the last completed date for a task, or <c>null</c> if the task has never run.
    /// The date represents the "day that was handled" (e.g. the day whose conversations
    /// were reflected on), not the wall-clock time of execution.
    /// </summary>
    public DateOnly? GetLastCompletedDate(string taskName)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "SELECT last_completed_date FROM maintenance_state WHERE task_name = $name";
            cmd.Parameters.AddWithValue("$name", taskName);

            var result = cmd.ExecuteScalar();
            if (result is null or DBNull)
            {
                return null;
            }

            return DateOnly.Parse((string)result, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Updates the last completed date for a task.
    /// </summary>
    public void SetLastCompletedDate(string taskName, DateOnly date)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO maintenance_state (task_name, last_completed_date, updated_at_utc)
                VALUES ($name, $date, $now)
                ON CONFLICT(task_name) DO UPDATE SET
                    last_completed_date = $date,
                    updated_at_utc = $now
                """;
            cmd.Parameters.AddWithValue("$name", taskName);
            cmd.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$now", FormatUtcNow());
            cmd.ExecuteNonQuery();
        }
    }

    // ── Run history ─────────────────────────────────────────────────────

    /// <summary>
    /// Records the start of a maintenance run. Returns the run ID.
    /// </summary>
    public long RecordRunStart(string taskName, DateOnly targetDate)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO maintenance_runs (task_name, target_date, started_at_utc, status)
                VALUES ($name, $date, $now, 'running')
                """;
            cmd.Parameters.AddWithValue("$name", taskName);
            cmd.Parameters.AddWithValue("$date", targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$now", FormatUtcNow());
            cmd.ExecuteNonQuery();

            using var idCmd = this.Connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            return (long)idCmd.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Marks a maintenance run as completed successfully.
    /// Also updates the task's last-completed-date state.
    /// </summary>
    public void RecordRunSuccess(long runId, string taskName, DateOnly targetDate)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE maintenance_runs
                SET status = 'completed', finished_at_utc = $now
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", runId);
            cmd.Parameters.AddWithValue("$now", FormatUtcNow());
            cmd.ExecuteNonQuery();
        }

        // Update state outside the lock (SetLastCompletedDate acquires its own lock)
        SetLastCompletedDate(taskName, targetDate);
    }

    /// <summary>
    /// Marks a maintenance run as failed.
    /// </summary>
    public void RecordRunFailure(long runId, string errorMessage)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE maintenance_runs
                SET status = 'failed', finished_at_utc = $now, error_message = $error
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", runId);
            cmd.Parameters.AddWithValue("$now", FormatUtcNow());
            cmd.Parameters.AddWithValue("$error", errorMessage);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Purges run history older than the specified number of days.
    /// Returns the number of rows deleted.
    /// </summary>
    public int PurgeOldRuns(int keepDays = 30)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-keepDays);

        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM maintenance_runs
                WHERE finished_at_utc IS NOT NULL AND finished_at_utc <= $cutoff
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            return cmd.ExecuteNonQuery();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string FormatUtcNow()
        => DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
