using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.History;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// A consistent, read-only view of <c>messages.db</c>: one reader connection inside one
/// deferred transaction whose snapshot is pinned at open. Used by the projector (whole-channel
/// reads) and recall tools (verification, unindexed tail, reads around a message).
/// </summary>
public sealed partial class HistorySourceSnapshot : IAsyncDisposable
{
    /// <summary>Categories the recall tools can see: Normal, Internal, System, Proactive.</summary>
    internal const string IndexedCategories = "(0, 1, 2, 3)";

    private const string Columns = "Id, UserId, ChannelId, Role, Content, Timestamp, MessageId, Category, ToolCalls";

    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;

    private HistorySourceSnapshot(SqliteConnection connection, SqliteTransaction transaction)
    {
        this.connection = connection;
        this.transaction = transaction;
    }

    internal static async Task<HistorySourceSnapshot> BeginAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = new HistorySourceSnapshot(connection, transaction);

        // The first read pins the WAL snapshot for the lifetime of the transaction.
        await using var pin = snapshot.Command("SELECT COUNT(*) FROM Meta;");
        await pin.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>Reads identity and journal bounds.</summary>
    public async Task<HistorySourceMeta> ReadMetaAsync(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = this.Command("SELECT Key, Value FROM Meta;");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return new HistorySourceMeta(
            values["DbInstanceId"],
            long.Parse(values["RestoreGeneration"], CultureInfo.InvariantCulture),
            long.Parse(values["JournalHead"], CultureInfo.InvariantCulture),
            long.Parse(values["JournalPrunedThrough"], CultureInfo.InvariantCulture))
        {
            PurgeGeneration = values.TryGetValue("PurgeGeneration", out var purgeGeneration)
                ? long.Parse(purgeGeneration, CultureInfo.InvariantCulture)
                : 0,
        };
    }

    /// <summary>Distinct channels that have at least one indexed-category row.</summary>
    public async Task<IReadOnlyList<string>> ReadChannelIdsAsync(CancellationToken cancellationToken)
    {
        var channels = new List<string>();
        await using var cmd = this.Command($"SELECT DISTINCT ChannelId FROM Messages WHERE Category IN {IndexedCategories} ORDER BY ChannelId;");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            channels.Add(reader.GetString(0));
        }

        return channels;
    }

    /// <summary>Number of indexed-category rows per channel.</summary>
    public async Task<IReadOnlyDictionary<string, int>> ReadChannelSizesAsync(CancellationToken cancellationToken)
    {
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var cmd = this.Command($"SELECT ChannelId, COUNT(*) FROM Messages WHERE Category IN {IndexedCategories} GROUP BY ChannelId;");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sizes[reader.GetString(0)] = reader.GetInt32(1);
        }

        return sizes;
    }

    /// <summary>Every indexed-category row of one channel, ordered by <c>(Timestamp, Id)</c>.</summary>
    public Task<IReadOnlyList<MessageRecord>> ReadChannelAsync(string channelId, CancellationToken cancellationToken) =>
        this.QueryAsync(
            $"SELECT {Columns} FROM Messages WHERE ChannelId = @channelId AND Category IN {IndexedCategories} ORDER BY Timestamp, Id;",
            cmd => cmd.Parameters.AddWithValue("@channelId", channelId),
            cancellationToken);

    /// <summary>The rows with the given ids (any category), ordered by <c>(Timestamp, Id)</c>. Missing ids are skipped.</summary>
    public Task<IReadOnlyList<MessageRecord>> ReadByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? Task.FromResult<IReadOnlyList<MessageRecord>>([])
            : this.QueryAsync(
                $"SELECT {Columns} FROM Messages WHERE Id IN (SELECT value FROM json_each(@ids)) ORDER BY Timestamp, Id;",
                cmd => cmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(ids)),
                cancellationToken);

    /// <summary>
    /// Up to <paramref name="window"/> rows before and after <paramref name="anchor"/> in its
    /// channel (recall visibility), plus the anchor, in <c>(Timestamp, Id)</c> order.
    /// </summary>
    public async Task<IReadOnlyList<MessageRecord>> ReadAroundAsync(MessageRecord anchor, int window, bool includeInternal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var categories = includeInternal ? IndexedCategories : "(0, 2, 3)";
        var timestamp = SqliteDateTimeText.Format(anchor.Timestamp);

        var before = await this.QueryAsync(
            $"SELECT {Columns} FROM Messages WHERE ChannelId = @channelId AND Category IN {categories} AND Role <> 'tool' " +
            "AND (Timestamp < @ts OR (Timestamp = @ts AND Id < @id)) ORDER BY Timestamp DESC, Id DESC LIMIT @window;",
            cmd => Bind(cmd, anchor.ChannelId, timestamp, anchor.Id, window),
            cancellationToken).ConfigureAwait(false);
        var after = await this.QueryAsync(
            $"SELECT {Columns} FROM Messages WHERE ChannelId = @channelId AND Category IN {categories} AND Role <> 'tool' " +
            "AND (Timestamp > @ts OR (Timestamp = @ts AND Id > @id)) ORDER BY Timestamp, Id LIMIT @window;",
            cmd => Bind(cmd, anchor.ChannelId, timestamp, anchor.Id, window),
            cancellationToken).ConfigureAwait(false);

        return before.Reverse().Append(anchor).Concat(after).ToList();

        static void Bind(SqliteCommand cmd, string channelId, string ts, long id, int window)
        {
            cmd.Parameters.AddWithValue("@channelId", channelId);
            cmd.Parameters.AddWithValue("@ts", ts);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@window", window);
        }
    }

    /// <summary>
    /// The newest <paramref name="limit"/> rows of a channel with <c>since ≤ Timestamp &lt; until</c>
    /// (recall visibility), returned oldest first. <c>HasMore</c> is true when older rows were cut.
    /// </summary>
    public async Task<(IReadOnlyList<MessageRecord> Rows, bool HasMore)> ReadRangeAsync(
        string channelId,
        DateTimeOffset? since,
        DateTimeOffset? until,
        int limit,
        bool includeInternal,
        CancellationToken cancellationToken)
    {
        var categories = includeInternal ? IndexedCategories : "(0, 2, 3)";
        var rows = await this.QueryAsync(
            $"SELECT {Columns} FROM Messages WHERE ChannelId = @channelId AND Category IN {categories} AND Role <> 'tool' " +
            "AND (@since IS NULL OR Timestamp >= @since) AND (@until IS NULL OR Timestamp < @until) " +
            "ORDER BY Timestamp DESC, Id DESC LIMIT @limit;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@channelId", channelId);
                cmd.Parameters.AddWithValue("@since", since is { } s ? SqliteDateTimeText.Format(s) : DBNull.Value);
                cmd.Parameters.AddWithValue("@until", until is { } u ? SqliteDateTimeText.Format(u) : DBNull.Value);
                cmd.Parameters.AddWithValue("@limit", limit + 1);
            },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > limit;
        return (rows.Take(limit).Reverse().ToList(), hasMore);
    }

    /// <summary>One row by id, or null.</summary>
    public async Task<MessageRecord?> ReadByIdAsync(long id, CancellationToken cancellationToken)
    {
        var rows = await this.ReadByIdsAsync([id], cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Journal rows with <c>Seq &gt; afterSeq</c>, oldest first, at most <paramref name="max"/>.</summary>
    public async Task<IReadOnlyList<HistoryJournalEntry>> ReadJournalAsync(long afterSeq, int max, CancellationToken cancellationToken)
    {
        var entries = new List<HistoryJournalEntry>();
        await using var cmd = this.Command("SELECT Seq, Op, ChannelId, MessageIds, FromTs, ToTs, At, PurgeGeneration FROM HistoryJournal WHERE Seq > @after ORDER BY Seq LIMIT @max;");
        cmd.Parameters.AddWithValue("@after", afterSeq);
        cmd.Parameters.AddWithValue("@max", max);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new HistoryJournalEntry(
                reader.GetInt64(0),
                string.Equals(reader.GetString(1), "purge", StringComparison.Ordinal) ? HistoryJournalOp.Purge : HistoryJournalOp.Upsert,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? [] : JsonSerializer.Deserialize<List<long>>(reader.GetString(3)) ?? [],
                reader.IsDBNull(4) ? null : SqliteDateTimeText.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : SqliteDateTimeText.Parse(reader.GetString(5)),
                SqliteDateTimeText.Parse(reader.GetString(6)))
            {
                PurgeGeneration = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            });
        }

        return entries;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.transaction.DisposeAsync().ConfigureAwait(false);
        await this.connection.DisposeAsync().ConfigureAwait(false);
    }

    private SqliteCommand Command(string sql)
    {
        var cmd = this.connection.CreateCommand();
        cmd.Transaction = this.transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private async Task<IReadOnlyList<MessageRecord>> QueryAsync(string sql, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        var rows = new List<MessageRecord>();
        await using var cmd = this.Command(sql);
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MessageStore.ReadRecord(reader));
        }

        return rows;
    }
}
