using System.Globalization;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// SQLite-backed todo store for the main agent. Persists todo lists to
/// <c>/app/state/todos.db</c> so they survive container restarts.
/// </summary>
public sealed partial class SqliteTodoStore : SqliteStoreBase, ITodoStore
{
    private readonly Lock syncLock = new();
    private readonly ILogger<SqliteTodoStore> logger;
    private readonly Timer cleanupTimer;

    /// <summary>Auto-delete fully completed lists after this duration.</summary>
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromHours(24);

    /// <summary>Cleanup runs every 6 hours.</summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    /// <summary>Maximum lists per conversation.</summary>
    internal const int MaxListsPerConversation = 5;

    /// <summary>Maximum items per list.</summary>
    internal const int MaxItemsPerList = 20;

    private const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Initialises the todo store, opening the SQLite database at <paramref name="stateRoot"/>/todos/todos.db.
    /// </summary>
    public SqliteTodoStore(string stateRoot, ILogger<SqliteTodoStore> logger)
        : base(PrepareDatabasePath(stateRoot, "todos", "todos.db"), enableWalMode: true)
    {
        this.logger = logger;
        this.EnsureSchema();
        this.cleanupTimer = new Timer(this.OnCleanupTick, null, CleanupInterval, CleanupInterval);
    }

    // ── Schema ───────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        var version = GetSchemaVersion();
        if (version < CurrentSchemaVersion)
        {
            ExecuteNonQuery("DROP TABLE IF EXISTS todo_lists");

            ExecuteNonQuery("""
                CREATE TABLE todo_lists (
                    name            TEXT NOT NULL,
                    conversation_id TEXT NOT NULL,
                    todos_markdown  TEXT NOT NULL,
                    updated_at      TEXT NOT NULL,
                    PRIMARY KEY (name, conversation_id)
                );

                CREATE INDEX idx_todos_conversation
                    ON todo_lists (conversation_id);
                """);

            this.SetSchemaVersion(CurrentSchemaVersion);
        }
    }

    // ── ITodoStore ───────────────────────────────────────────────────────

    public void Write(string conversationId, string name, string markdown)
    {
        var items = TodoParser.Parse(markdown);
        if (items.Count > MaxItemsPerList)
        {
            this.LogTodosTooManyItems(name, conversationId, items.Count, MaxItemsPerList);
            return;
        }

        lock (this.syncLock)
        {
            // Check max lists per conversation (exclude updates to existing lists)
            var existingCount = CountLists(conversationId);
            var isNew = !ListExists(conversationId, name);
            if (isNew && existingCount >= MaxListsPerConversation)
            {
                // Try to auto-remove oldest completed list
                if (!RemoveOldestCompleted(conversationId))
                {
                    this.LogTodosTooManyLists(conversationId, MaxListsPerConversation);
                    return;
                }
            }

            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO todo_lists (name, conversation_id, todos_markdown, updated_at)
                VALUES ($name, $conversationId, $markdown, $updatedAt)
                ON CONFLICT (name, conversation_id)
                DO UPDATE SET todos_markdown = $markdown, updated_at = $updatedAt
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$conversationId", conversationId);
            cmd.Parameters.AddWithValue("$markdown", markdown);
            cmd.Parameters.AddWithValue("$updatedAt", FormatDto(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }

        this.LogTodosWritten(name, conversationId, items.Count);
    }

    public TodoList? Read(string conversationId, string name)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "SELECT todos_markdown, updated_at FROM todo_lists WHERE name = $name AND conversation_id = $conversationId";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$conversationId", conversationId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var markdown = reader.GetString(0);
            var updatedAt = ParseDto(reader.GetString(1));
            return new TodoList
            {
                Name = name,
                Markdown = markdown,
                Items = TodoParser.Parse(markdown),
                UpdatedAt = updatedAt,
            };
        }
    }

    public IReadOnlyList<TodoList> ReadAll(string conversationId)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "SELECT name, todos_markdown, updated_at FROM todo_lists WHERE conversation_id = $conversationId ORDER BY updated_at";
            cmd.Parameters.AddWithValue("$conversationId", conversationId);
            return ReadLists(cmd);
        }
    }

    public bool Delete(string conversationId, string name)
    {
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM todo_lists WHERE name = $name AND conversation_id = $conversationId";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$conversationId", conversationId);
            var deleted = cmd.ExecuteNonQuery() > 0;

            if (deleted)
            {
                this.LogTodosDeleted(name, conversationId);
            }

            return deleted;
        }
    }

    public IReadOnlyList<TodoSummary> GetSummaries(string conversationId)
    {
        var lists = ReadAll(conversationId);
        return lists
            .Select(l => TodoParser.Summarize(l.Name, l.Items))
            .ToList();
    }

    // ── Cleanup ──────────────────────────────────────────────────────────

    private void OnCleanupTick(object? state)
    {
        try
        {
            var purged = Cleanup();
            if (purged > 0)
            {
                this.LogTodosCleanupCompleted(purged);
            }
        }
#pragma warning disable CA1031 // Cleanup must not crash the timer
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogTodosCleanupFailed(ex.Message);
        }
    }

    /// <summary>
    /// Delete todo lists that are fully completed/skipped and older than <see cref="CompletedRetention"/>.
    /// </summary>
    public int Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - CompletedRetention;
        var purged = 0;

        lock (this.syncLock)
        {
            // Get all lists older than cutoff
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "SELECT name, conversation_id, todos_markdown FROM todo_lists WHERE updated_at <= $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", FormatDto(cutoff));

            var toDelete = new List<(string Name, string ConversationId)>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    var convId = reader.GetString(1);
                    var markdown = reader.GetString(2);
                    var items = TodoParser.Parse(markdown);

                    // Only delete if ALL items are completed or skipped
                    if (items.Count > 0 && items.All(i => i.Status is TodoStatus.Completed or TodoStatus.Skipped))
                    {
                        toDelete.Add((name, convId));
                    }
                }
            }

            foreach (var (name, convId) in toDelete)
            {
                using var delCmd = this.Connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM todo_lists WHERE name = $name AND conversation_id = $conversationId";
                delCmd.Parameters.AddWithValue("$name", name);
                delCmd.Parameters.AddWithValue("$conversationId", convId);
                purged += delCmd.ExecuteNonQuery();
            }
        }

        return purged;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        this.cleanupTimer.Dispose();
        base.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private int CountLists(string conversationId)
    {
        using var cmd = this.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM todo_lists WHERE conversation_id = $conversationId";
        cmd.Parameters.AddWithValue("$conversationId", conversationId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private bool ListExists(string conversationId, string name)
    {
        using var cmd = this.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM todo_lists WHERE name = $name AND conversation_id = $conversationId";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$conversationId", conversationId);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Remove the oldest fully-completed list to make room for a new one.</summary>
    private bool RemoveOldestCompleted(string conversationId)
    {
        using var cmd = this.Connection.CreateCommand();
        cmd.CommandText = "SELECT name, todos_markdown FROM todo_lists WHERE conversation_id = $conversationId ORDER BY updated_at";
        cmd.Parameters.AddWithValue("$conversationId", conversationId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var markdown = reader.GetString(1);
            var items = TodoParser.Parse(markdown);

            if (items.Count > 0 && items.All(i => i.Status is TodoStatus.Completed or TodoStatus.Skipped))
            {
                reader.Close();
                using var delCmd = this.Connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM todo_lists WHERE name = $name AND conversation_id = $conversationId";
                delCmd.Parameters.AddWithValue("$name", name);
                delCmd.Parameters.AddWithValue("$conversationId", conversationId);
                delCmd.ExecuteNonQuery();
                this.LogTodosAutoRemoved(name, conversationId);
                return true;
            }
        }

        return false;
    }

    private static List<TodoList> ReadLists(SqliteCommand cmd)
    {
        var lists = new List<TodoList>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var markdown = reader.GetString(1);
            var updatedAt = ParseDto(reader.GetString(2));
            lists.Add(new TodoList
            {
                Name = name,
                Markdown = markdown,
                Items = TodoParser.Parse(markdown),
                UpdatedAt = updatedAt,
            });
        }

        return lists;
    }

    private static string FormatDto(DateTimeOffset dto)
        => SqliteDateTimeText.Format(dto);

    private static DateTimeOffset ParseDto(string s)
        => SqliteDateTimeText.Parse(s);

    // ── Logging ──────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "[todos] Written: \"{Name}\" for {ConversationId} ({ItemCount} items)")]
    private partial void LogTodosWritten(string name, string conversationId, int itemCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "[todos] Deleted: \"{Name}\" for {ConversationId}")]
    private partial void LogTodosDeleted(string name, string conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[todos] Too many items in \"{Name}\" for {ConversationId}: {Count} exceeds max {Max}")]
    private partial void LogTodosTooManyItems(string name, string conversationId, int count, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[todos] Too many lists for {ConversationId}: max {Max} reached")]
    private partial void LogTodosTooManyLists(string conversationId, int max);

    [LoggerMessage(Level = LogLevel.Information, Message = "[todos] Auto-removed completed list \"{Name}\" for {ConversationId} to make room")]
    private partial void LogTodosAutoRemoved(string name, string conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[todos] Cleanup completed: {Count} lists purged")]
    private partial void LogTodosCleanupCompleted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[todos] Cleanup failed: {ErrorMessage}")]
    private partial void LogTodosCleanupFailed(string errorMessage);
}
