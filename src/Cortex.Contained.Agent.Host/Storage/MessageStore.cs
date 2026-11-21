using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Controls which message categories are returned by <see cref="MessageStore.GetMessagesAsync"/>.
/// </summary>
public enum MessageVisibility
{
    /// <summary>Normal + System messages — for chat UI and history page.</summary>
    History,

    /// <summary>Normal messages only — for agent seeding.</summary>
    Seeding,

    /// <summary>All messages including Internal — for diagnostics.</summary>
    All,
}

/// <summary>
/// Persistent message store backed by SQLite.
/// Stores all messages for this tenant container at /app/state/messages.db.
/// Each tenant container has its own independent MessageStore — full data isolation.
/// <para>
/// One exclusive writer connection is used only under <see cref="writeSerializer"/>. Every
/// mutation runs in one transaction that also appends <c>HistoryJournal</c> rows and advances
/// <c>Meta.JournalHead</c> (see <c>MessageStore.History.cs</c>). All reads use separate
/// read-only connections from a small pool.
/// </para>
/// </summary>
public sealed partial class MessageStore : IMessageStore, IHistorySource, IHistoryErasureStore, IAsyncDisposable
{
    private readonly SqliteConnection writer;
    private readonly string readerConnectionString;
    private readonly ILogger<MessageStore> logger;
    private bool disposed;

    // The writer connection is not safe for concurrent use: barge-in truncation
    // (UpdateContentAsync) can race the session loop (SaveMessageAsync /
    // UpdateToolCallsAsync). This semaphore serializes every write transaction.
    private readonly SemaphoreSlim writeSerializer = new(1, 1);

    public MessageStore(string databasePath, ILogger<MessageStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.logger = logger;

        // ":memory:" (tests) becomes a uniquely named shared-cache memory database so the
        // separate reader connections see the writer's data.
        var inMemory = string.Equals(databasePath, ":memory:", StringComparison.Ordinal);
        var dataSource = inMemory ? "messages-" + Guid.NewGuid().ToString("N") : databasePath;

        // Ensure the directory exists
        var directory = inMemory ? null : Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writerBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = inMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = inMemory ? SqliteCacheMode.Shared : SqliteCacheMode.Private,
            Pooling = false,
        };

        this.readerConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = inMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadOnly,
            Cache = inMemory ? SqliteCacheMode.Shared : SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();

        this.writer = new SqliteConnection(writerBuilder.ToString());
        this.writer.Open();

        // WAL lets readers run beside the writer; secure_delete overwrites deleted content.
        using var pragmaCmd = this.writer.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA secure_delete=ON;";
        pragmaCmd.ExecuteNonQuery();

        InitializeSchema();
        this.LogDatabaseInitialized(databasePath);
    }

    /// <summary>
    /// Test seam: invoked with a stage name inside write transactions
    /// (<c>after-row-write</c>, <c>after-purge-delete</c>). Throwing rolls the transaction back.
    /// </summary>
    internal Action<string>? FaultInjector { get; set; }

    /// <summary>Opens a new read-only connection (pooled). Callers dispose it.</summary>
    internal async Task<SqliteConnection> OpenReaderAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        var connection = new SqliteConnection(this.readerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    // ──────────────────────────────────────────────
    //  Write operations
    // ──────────────────────────────────────────────

    /// <summary>
    /// Saves a message to the store. Returns the auto-generated row Id.
    /// </summary>
    public async Task<long> SaveMessageAsync(
        string userId,
        string channelId,
        string role,
        string content,
        DateTimeOffset timestamp,
        string? messageId = null,
        MessageCategory category = MessageCategory.Normal,
        string? toolCalls = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(content);

        var record = new MessageRecord
        {
            UserId = userId,
            ChannelId = channelId,
            Role = role,
            Content = content,
            Timestamp = timestamp,
            MessageId = messageId,
            Category = category,
            ToolCalls = toolCalls,
        };

        return await this.WriteAsync(
            async transaction =>
            {
                var rowId = await this.InsertRowAsync(transaction, record, cancellationToken).ConfigureAwait(false);
                this.FaultInjector?.Invoke("after-row-write");
                await this.AppendUpsertAsync(transaction, channelId, [rowId], cancellationToken).ConfigureAwait(false);
                await this.InsertEventAsync(transaction, MessageEvents.ForInsert(record with { Id = rowId }, RunScope.Current), cancellationToken).ConfigureAwait(false);
                return rowId;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Patches the <c>ToolCalls</c> column of an existing message in place.
    /// Pass <c>null</c> to clear an existing value. No-op if the row does not exist.
    /// </summary>
    public async Task UpdateToolCallsAsync(
        long messageId,
        string? toolCallsJson,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        await this.UpdateColumnAsync("ToolCalls", messageId, toolCallsJson, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replace a stored message's textual <c>Content</c> in place — used to
    /// rewrite a barge-in-interrupted assistant turn to what was actually
    /// spoken. Idempotent. No-op if the row does not exist.
    /// </summary>
    public async Task UpdateContentAsync(
        long recordId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentNullException.ThrowIfNull(content);
        await this.UpdateColumnAsync("Content", recordId, content, cancellationToken).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────
    //  Read operations
    // ──────────────────────────────────────────────

    /// <summary>
    /// Retrieves messages for a channel, ordered oldest-first (chronological).
    /// Internally fetches newest-first and reverses for the caller.
    /// </summary>
    public async Task<List<MessageRecord>> GetMessagesAsync(
        string channelId,
        int limit = 100,
        DateTimeOffset? before = null,
        long? beforeId = null,
        MessageVisibility visibility = MessageVisibility.History,
        DateTimeOffset? after = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        var sql = "SELECT Id, UserId, ChannelId, Role, Content, Timestamp, MessageId, Category, ToolCalls FROM Messages WHERE ChannelId = @channelId";

        // Apply category filter based on visibility mode
        sql += visibility switch
        {
            MessageVisibility.Seeding => " AND Category = 0",              // Normal only
            MessageVisibility.History => " AND Category IN (0, 2, 3, 4)",  // Normal + System + Proactive + Transfer
            MessageVisibility.All => "",                                    // Everything
            _ => " AND Category IN (0, 2, 3, 4)",
        };

        // beforeId takes precedence — it's a precise, tie-free cursor
        if (beforeId.HasValue)
        {
            sql += " AND Id < @beforeId";
            cmd.Parameters.AddWithValue("@beforeId", beforeId.Value);
        }
        else if (before.HasValue)
        {
            sql += " AND Timestamp < @before";
            cmd.Parameters.AddWithValue("@before", SqliteDateTimeText.Format(before.Value));
        }

        if (after.HasValue)
        {
            sql += " AND Timestamp >= @after";
            cmd.Parameters.AddWithValue("@after", SqliteDateTimeText.Format(after.Value));
        }

        sql += " ORDER BY Id DESC LIMIT @limit";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@channelId", channelId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<MessageRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }

        // Reverse so the caller gets oldest-first (chronological order)
        results.Reverse();
        return results;
    }

    /// <summary>
    /// Gets conversation summaries grouped by channel, ordered by most recent activity.
    /// </summary>
    public async Task<List<ConversationSummary>> GetConversationsAsync(
        string? channelId = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        var sql = """
            SELECT ChannelId,
                   COUNT(*) AS MessageCount,
                   MAX(Timestamp) AS LastMessageAt,
                   MIN(Timestamp) AS FirstMessageAt
            FROM Messages
            WHERE Category IN (0, 2, 3)
            """;

        if (!string.IsNullOrWhiteSpace(channelId))
        {
            sql += " AND ChannelId = @channelId";
            cmd.Parameters.AddWithValue("@channelId", channelId);
        }

        sql += " GROUP BY ChannelId ORDER BY LastMessageAt DESC LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var results = new List<ConversationSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var chId = reader.GetString(0);
            results.Add(new ConversationSummary
            {
                ConversationId = chId,
                ChannelId = chId,
                Title = chId, // Use channel ID as title for now
                MessageCount = reader.GetInt32(1),
                LastMessageAt = SqliteDateTimeText.Parse(reader.GetString(2)),
            });
        }

        return results;
    }

    /// <summary>
    /// Searches messages across all channels using a simple text match.
    /// </summary>
    public async Task<List<MessageRecord>> SearchMessagesAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, UserId, ChannelId, Role, Content, Timestamp, MessageId, Category, ToolCalls
            FROM Messages
            WHERE Content LIKE @query AND Category IN (0, 2)
            ORDER BY Id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@query", $"%{query}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<MessageRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    /// <summary>
    /// Returns distinct channel IDs that have at least one message in the database.
    /// </summary>
    public async Task<List<string>> GetActiveChannelsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT ChannelId FROM Messages ORDER BY ChannelId;";

        var channels = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            channels.Add(reader.GetString(0));
        }

        return channels;
    }

    /// <summary>
    /// Returns a per-channel summary with total message count and last-activity timestamp.
    /// Executes a single grouped query across all rows in the <c>Messages</c> table.
    /// </summary>
    public async Task<List<ChannelSummary>> GetChannelSummariesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ChannelId,
                   COUNT(*) AS MessageCount,
                   MAX(Timestamp) AS LastActivity
            FROM Messages
            GROUP BY ChannelId
            ORDER BY LastActivity DESC;
            """;

        var summaries = new List<ChannelSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            summaries.Add(new ChannelSummary
            {
                ChannelId = reader.GetString(0),
                MessageCount = reader.GetInt32(1),
                LastActivity = SqliteDateTimeText.Parse(reader.GetString(2)),
            });
        }

        return summaries;
    }

    /// <summary>
    /// Gets the total number of distinct conversations (channels with messages).
    /// </summary>
    public async Task<long> GetConversationCountAsync(
        string? channelId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        if (!string.IsNullOrWhiteSpace(channelId))
        {
            cmd.CommandText = "SELECT COUNT(*) FROM (SELECT 1 FROM Messages WHERE ChannelId = @channelId AND Category IN (0, 2) GROUP BY ChannelId);";
            cmd.Parameters.AddWithValue("@channelId", channelId);
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM (SELECT 1 FROM Messages WHERE Category IN (0, 2) GROUP BY ChannelId);";
        }

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (long)(result ?? 0L);
    }

    /// <summary>
    /// Gets the total message count for a channel. Useful for diagnostics.
    /// </summary>
    public async Task<long> GetMessageCountAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Messages WHERE ChannelId = @channelId;";
        cmd.Parameters.AddWithValue("@channelId", channelId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (long)(result ?? 0L);
    }

    /// <summary>
    /// Gets the total message count across all channels.
    /// </summary>
    public async Task<long> GetTotalMessageCountAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Messages;";

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (long)(result ?? 0L);
    }

    // ──────────────────────────────────────────────
    //  Bulk operations (export/import)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Retrieves all messages across all channels, ordered by Id ASC (chronological).
    /// </summary>
    public async Task<List<MessageRecord>> GetAllMessagesAsync(
        MessageVisibility visibility = MessageVisibility.All,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        var sql = "SELECT Id, UserId, ChannelId, Role, Content, Timestamp, MessageId, Category, ToolCalls FROM Messages";

        sql += visibility switch
        {
            MessageVisibility.Seeding => " WHERE Category = 0",
            MessageVisibility.History => " WHERE Category IN (0, 2, 3)",
            MessageVisibility.All => "",
            _ => " WHERE Category IN (0, 2, 3)",
        };

        sql += " ORDER BY Id ASC";

        cmd.CommandText = sql;

        var results = new List<MessageRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    /// <summary>
    /// Inserts multiple messages in a single SQLite transaction.
    /// Returns the number of messages inserted.
    /// </summary>
    public async Task<int> BulkInsertAsync(
        IReadOnlyList<MessageRecord> records,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (records.Count == 0)
        {
            return 0;
        }

        return await this.WriteAsync(
            async transaction =>
            {
                var inserted = await this.InsertRowsAsync(transaction, records, cancellationToken).ConfigureAwait(false);
                return inserted.Values.Sum(ids => ids.Count);
            },
            cancellationToken).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────
    //  Delete operations (each is one purge transaction, see MessageStore.History.cs)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Deletes all messages for a specific channel.
    /// </summary>
    public async Task<int> DeleteChannelMessagesAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var outcome = await this.PurgeAsync(new MessagePurgeRequest(new HistoryPurgeScope(channelId, null)), cancellationToken).ConfigureAwait(false);
        this.LogMessagesDeleted(channelId, outcome.Deleted);
        return outcome.Deleted;
    }

    /// <summary>
    /// Deletes messages older than the given timestamp for a specific channel.
    /// </summary>
    public async Task<int> DeleteChannelMessagesOlderThanAsync(
        string channelId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var outcome = await this.PurgeAsync(new MessagePurgeRequest(new HistoryPurgeScope(channelId, olderThan)), cancellationToken).ConfigureAwait(false);
        this.LogMessagesDeleted(channelId, outcome.Deleted);
        return outcome.Deleted;
    }

    /// <summary>
    /// Deletes messages older than the given timestamp across all channels.
    /// </summary>
    public async Task<int> DeleteMessagesOlderThanAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var outcome = await this.PurgeAsync(new MessagePurgeRequest(new HistoryPurgeScope(null, olderThan)), cancellationToken).ConfigureAwait(false);
        this.LogAllMessagesDeleted(outcome.Deleted);
        return outcome.Deleted;
    }

    /// <summary>
    /// Deletes all messages across all channels.
    /// </summary>
    public async Task<int> DeleteAllMessagesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var outcome = await this.PurgeAsync(new MessagePurgeRequest(HistoryPurgeScope.All), cancellationToken).ConfigureAwait(false);
        this.LogAllMessagesDeleted(outcome.Deleted);
        return outcome.Deleted;
    }

    // ──────────────────────────────────────────────
    //  Schema
    // ──────────────────────────────────────────────

    private void InitializeSchema()
    {
        using (var cmd = this.writer.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Messages (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId      TEXT    NOT NULL,
                    ChannelId   TEXT    NOT NULL,
                    Role        TEXT    NOT NULL,
                    Content     TEXT    NOT NULL,
                    Timestamp   TEXT    NOT NULL,
                    MessageId   TEXT,
                    Category    INTEGER DEFAULT 0,
                    ToolCalls   TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_messages_channel_time
                    ON Messages (ChannelId, Timestamp);

                CREATE INDEX IF NOT EXISTS idx_messages_channel_id
                    ON Messages (ChannelId, Id);
                """;
            cmd.ExecuteNonQuery();
        }

        // Idempotent migration for pre-existing databases that lack ToolCalls.
        if (!this.ColumnExists("Messages", "ToolCalls"))
        {
            using var alter = this.writer.CreateCommand();
            alter.CommandText = "ALTER TABLE Messages ADD COLUMN ToolCalls TEXT;";
            alter.ExecuteNonQuery();
        }

        this.InitializeHistorySchema();
        this.InitializeEventsSchema();
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = this.writer.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    internal static MessageRecord ReadRecord(SqliteDataReader reader)
    {
        return new MessageRecord
        {
            Id = reader.GetInt64(0),
            UserId = reader.GetString(1),
            ChannelId = reader.GetString(2),
            Role = reader.GetString(3),
            Content = reader.GetString(4),
            Timestamp = SqliteDateTimeText.Parse(reader.GetString(5)),
            MessageId = reader.IsDBNull(6) ? null : reader.GetString(6),
            Category = (MessageCategory)reader.GetInt32(7),
            ToolCalls = reader.IsDBNull(8) ? null : reader.GetString(8),
        };
    }

    // ──────────────────────────────────────────────
    //  IAsyncDisposable
    // ──────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }
        this.disposed = true;

        this.writeSerializer.Dispose();
        await this.writer.DisposeAsync().ConfigureAwait(false);

        // Pooled readers keep file handles open; release them so the files can be deleted.
        using var pooled = new SqliteConnection(this.readerConnectionString);
        SqliteConnection.ClearPool(pooled);
    }

    // ──────────────────────────────────────────────
    //  LoggerMessage source-generated methods
    // ──────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "MessageStore initialized, database: {DatabasePath}")]
    private partial void LogDatabaseInitialized(string databasePath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} messages for channel {ChannelId}")]
    private partial void LogMessagesDeleted(string channelId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted all messages ({Count} total)")]
    private partial void LogAllMessagesDeleted(int count);
}
