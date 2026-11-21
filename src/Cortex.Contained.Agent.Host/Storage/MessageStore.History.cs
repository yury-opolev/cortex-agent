using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.History;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// History support for <see cref="MessageStore"/>: the change journal and <c>Meta</c> written in
/// the same transaction as every <c>Messages</c> mutation, the purge transaction, read snapshots,
/// and erasure bookkeeping (§8, §11).
/// </summary>
public sealed partial class MessageStore
{
    private static readonly JsonSerializerOptions metaJson = new(JsonSerializerDefaults.Web);

    // ──────────────────────────────────────────────
    //  Schema
    // ──────────────────────────────────────────────

    private void InitializeHistorySchema()
    {
        using var cmd = this.writer.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS HistoryJournal (
                Seq        INTEGER PRIMARY KEY AUTOINCREMENT,
                Op         TEXT NOT NULL,
                ChannelId  TEXT NULL,
                MessageIds TEXT NULL,
                FromTs     TEXT NULL,
                ToTs       TEXT NULL,
                At         TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Meta (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MemoryQuarantineWindows (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                ErasureId TEXT NOT NULL,
                FromTs    TEXT NOT NULL,
                ToTs      TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MemoryQuarantineDecisions (
                MemoryId  TEXT PRIMARY KEY,
                Decision  TEXT NOT NULL,
                DecidedAt TEXT NOT NULL
            );

            INSERT OR IGNORE INTO Meta (Key, Value) VALUES
                ('DbInstanceId', @instanceId),
                ('RestoreGeneration', '0'),
                ('JournalHead', '0'),
                ('JournalPrunedThrough', '0'),
                ('PendingErasure', '[]');
            """;
        cmd.Parameters.AddWithValue("@instanceId", Guid.NewGuid().ToString("N"));
        cmd.ExecuteNonQuery();
    }

    // ──────────────────────────────────────────────
    //  Writer transaction helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="work"/> in one writer transaction under <see cref="writeSerializer"/>.
    /// Any exception rolls back every statement of the transaction, including journal rows.
    /// </summary>
    private async Task<T> WriteAsync<T>(Func<SqliteTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        await this.writeSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = (SqliteTransaction)await this.writer.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await work(transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            this.writeSerializer.Release();
        }
    }

    private SqliteCommand WriterCommand(SqliteTransaction transaction, string sql)
    {
        var cmd = this.writer.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private async Task<long> InsertRowAsync(SqliteTransaction transaction, MessageRecord record, CancellationToken cancellationToken)
    {
        await using var cmd = this.WriterCommand(transaction, """
            INSERT INTO Messages (UserId, ChannelId, Role, Content, Timestamp, MessageId, Category, ToolCalls)
            VALUES (@userId, @channelId, @role, @content, @timestamp, @messageId, @category, @toolCalls)
            RETURNING Id;
            """);
        cmd.Parameters.AddWithValue("@userId", record.UserId);
        cmd.Parameters.AddWithValue("@channelId", record.ChannelId);
        cmd.Parameters.AddWithValue("@role", record.Role);
        cmd.Parameters.AddWithValue("@content", record.Content);
        cmd.Parameters.AddWithValue("@timestamp", SqliteDateTimeText.Format(record.Timestamp));
        cmd.Parameters.AddWithValue("@messageId", (object?)record.MessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@category", (int)record.Category);
        cmd.Parameters.AddWithValue("@toolCalls", (object?)record.ToolCalls ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Inserts <paramref name="records"/> and journals one upsert per channel.</summary>
    private async Task<Dictionary<string, List<long>>> InsertRowsAsync(
        SqliteTransaction transaction,
        IReadOnlyList<MessageRecord> records,
        CancellationToken cancellationToken)
    {
        var byChannel = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var id = await this.InsertRowAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            if (!byChannel.TryGetValue(record.ChannelId, out var ids))
            {
                ids = [];
                byChannel[record.ChannelId] = ids;
            }

            ids.Add(id);
        }

        this.FaultInjector?.Invoke("after-row-write");
        foreach (var (channelId, ids) in byChannel)
        {
            await this.AppendUpsertAsync(transaction, channelId, ids, cancellationToken).ConfigureAwait(false);
        }

        return byChannel;
    }

    /// <summary>Updates one column of one row and journals the change when the row exists.</summary>
    private Task<bool> UpdateColumnAsync(string column, long id, string? value, CancellationToken cancellationToken) =>
        this.WriteAsync(
            async transaction =>
            {
                await using var cmd = this.WriterCommand(transaction, $"UPDATE Messages SET {column} = @value WHERE Id = @id RETURNING ChannelId;");
                cmd.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", id);
                if (await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string channelId)
                {
                    return false;
                }

                this.FaultInjector?.Invoke("after-row-write");
                await this.AppendUpsertAsync(transaction, channelId, [id], cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    private Task<long> AppendUpsertAsync(SqliteTransaction transaction, string channelId, IReadOnlyList<long> ids, CancellationToken cancellationToken) =>
        this.AppendJournalAsync(transaction, "upsert", channelId, JsonSerializer.Serialize(ids), null, null, cancellationToken);

    private Task<long> AppendPurgeAsync(SqliteTransaction transaction, HistoryPurgeScope scope, CancellationToken cancellationToken) =>
        this.AppendJournalAsync(
            transaction,
            "purge",
            scope.ChannelId,
            null,
            null,
            scope.OlderThan is { } olderThan ? SqliteDateTimeText.Format(olderThan) : null,
            cancellationToken);

    private async Task<long> AppendJournalAsync(
        SqliteTransaction transaction,
        string op,
        string? channelId,
        string? messageIds,
        string? fromTs,
        string? toTs,
        CancellationToken cancellationToken)
    {
        await using var cmd = this.WriterCommand(transaction, """
            INSERT INTO HistoryJournal (Op, ChannelId, MessageIds, FromTs, ToTs, At)
            VALUES (@op, @channelId, @messageIds, @fromTs, @toTs, @at)
            RETURNING Seq;
            """);
        cmd.Parameters.AddWithValue("@op", op);
        cmd.Parameters.AddWithValue("@channelId", (object?)channelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@messageIds", (object?)messageIds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fromTs", (object?)fromTs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@toTs", (object?)toTs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", SqliteDateTimeText.Format(DateTimeOffset.UtcNow));
        var seq = (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await this.SetMetaAsync(transaction, "JournalHead", seq.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        return seq;
    }

    private async Task SetMetaAsync(SqliteTransaction transaction, string key, string value, CancellationToken cancellationToken)
    {
        await using var cmd = this.WriterCommand(transaction, "INSERT INTO Meta (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;");
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetMetaAsync(SqliteTransaction transaction, string key, CancellationToken cancellationToken)
    {
        await using var cmd = this.WriterCommand(transaction, "SELECT Value FROM Meta WHERE Key = @key;");
        cmd.Parameters.AddWithValue("@key", key);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    // ──────────────────────────────────────────────
    //  Purge transaction (§11 step 2)
    // ──────────────────────────────────────────────

    /// <inheritdoc />
    public Task<MessagePurgeOutcome> PurgeAsync(MessagePurgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.WriteAsync(
            async transaction =>
            {
                var (where, bind) = PurgeFilter(request.Scope);

                var timestamps = new List<DateTimeOffset>();
                await using (var select = this.WriterCommand(transaction, $"SELECT Timestamp FROM Messages WHERE {where} ORDER BY Timestamp;"))
                {
                    bind(select);
                    await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        timestamps.Add(SqliteDateTimeText.Parse(reader.GetString(0)));
                    }
                }

                int deleted;
                await using (var delete = this.WriterCommand(transaction, $"DELETE FROM Messages WHERE {where};"))
                {
                    bind(delete);
                    deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                this.FaultInjector?.Invoke("after-purge-delete");
                await this.AppendPurgeAsync(transaction, request.Scope, cancellationToken).ConfigureAwait(false);

                var windows = QuarantineWindowBuilder.Build(timestamps, request.QuarantineClusterGap, request.QuarantineSlack, request.Now);
                foreach (var window in windows)
                {
                    await using var insert = this.WriterCommand(transaction, "INSERT INTO MemoryQuarantineWindows (ErasureId, FromTs, ToTs, CreatedAt) VALUES (@id, @from, @to, @at);");
                    insert.Parameters.AddWithValue("@id", request.ErasureId);
                    insert.Parameters.AddWithValue("@from", SqliteDateTimeText.Format(window.From));
                    insert.Parameters.AddWithValue("@to", SqliteDateTimeText.Format(window.To));
                    insert.Parameters.AddWithValue("@at", SqliteDateTimeText.Format(request.Now));
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var inserted = 0;
                if (request.Replacement is { } replacement)
                {
                    var ids = await this.InsertRowsAsync(transaction, replacement, cancellationToken).ConfigureAwait(false);
                    inserted = ids.Values.Sum(list => list.Count);
                    var generation = long.Parse(await this.GetMetaAsync(transaction, "RestoreGeneration", cancellationToken).ConfigureAwait(false) ?? "0", CultureInfo.InvariantCulture);
                    await this.SetMetaAsync(transaction, "RestoreGeneration", (generation + 1).ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                }

                DateTimeOffset? rangeFrom = timestamps.Count > 0 ? timestamps[0] : null;
                DateTimeOffset? rangeTo = timestamps.Count > 0 ? timestamps[^1] : null;
                if (request.PendingSteps is { Count: > 0 } steps)
                {
                    var tasks = await this.ReadPendingErasureAsync(transaction, cancellationToken).ConfigureAwait(false);
                    tasks.Add(new ErasureTask
                    {
                        Id = request.ErasureId,
                        ChannelId = request.Scope.ChannelId,
                        RangeFrom = rangeFrom,
                        RangeTo = rangeTo,
                        Deleted = deleted,
                        CreatedAt = request.Now,
                        Steps = steps.ToList(),
                    });
                    await this.WritePendingErasureAsync(transaction, tasks, cancellationToken).ConfigureAwait(false);
                }

                return new MessagePurgeOutcome(deleted, inserted, windows, rangeFrom, rangeTo);
            },
            cancellationToken);
    }

    private static (string Where, Action<SqliteCommand> Bind) PurgeFilter(HistoryPurgeScope scope)
    {
        var clauses = new List<string>();
        if (scope.ChannelId is not null)
        {
            clauses.Add("ChannelId = @channelId");
        }

        if (scope.OlderThan is not null)
        {
            clauses.Add("Timestamp < @olderThan");
        }

        var where = clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses);
        return (where, cmd =>
        {
            if (scope.ChannelId is not null)
            {
                cmd.Parameters.AddWithValue("@channelId", scope.ChannelId);
            }

            if (scope.OlderThan is { } olderThan)
            {
                cmd.Parameters.AddWithValue("@olderThan", SqliteDateTimeText.Format(olderThan));
            }
        });
    }

    // ──────────────────────────────────────────────
    //  Pending erasure bookkeeping
    // ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<ErasureTask>> ReadPendingErasureAsync(CancellationToken cancellationToken)
    {
        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Meta WHERE Key = 'PendingErasure';";
        return Deserialize(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string);
    }

    /// <inheritdoc />
    public Task AppendErasureTaskAsync(ErasureTask task, CancellationToken cancellationToken) =>
        this.WriteAsync(
            async transaction =>
            {
                var tasks = await this.ReadPendingErasureAsync(transaction, cancellationToken).ConfigureAwait(false);
                tasks.Add(task);
                await this.WritePendingErasureAsync(transaction, tasks, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task CompleteErasureStepAsync(string taskId, string stepName, CancellationToken cancellationToken) =>
        this.WriteAsync(
            async transaction =>
            {
                var tasks = await this.ReadPendingErasureAsync(transaction, cancellationToken).ConfigureAwait(false);
                var updated = tasks
                    .Select(t => string.Equals(t.Id, taskId, StringComparison.Ordinal)
                        ? t with { Steps = t.Steps.Where(s => !string.Equals(s, stepName, StringComparison.Ordinal)).ToList() }
                        : t)
                    .Where(t => t.Steps.Count > 0)
                    .ToList();
                await this.WritePendingErasureAsync(transaction, updated, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    private async Task<List<ErasureTask>> ReadPendingErasureAsync(SqliteTransaction transaction, CancellationToken cancellationToken) =>
        Deserialize(await this.GetMetaAsync(transaction, "PendingErasure", cancellationToken).ConfigureAwait(false));

    private Task WritePendingErasureAsync(SqliteTransaction transaction, IReadOnlyList<ErasureTask> tasks, CancellationToken cancellationToken) =>
        this.SetMetaAsync(transaction, "PendingErasure", JsonSerializer.Serialize(tasks, metaJson), cancellationToken);

    private static List<ErasureTask> Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<ErasureTask>>(json, metaJson) ?? [];

    // ──────────────────────────────────────────────
    //  Maintenance
    // ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<WalCheckpointResult> CheckpointWalAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        await this.writeSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = this.writer.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new WalCheckpointResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }
        finally
        {
            this.writeSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async Task VacuumAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        await this.writeSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var vacuum = this.writer.CreateCommand())
            {
                vacuum.CommandText = "VACUUM;";
                await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var record = this.writer.CreateCommand();
            record.CommandText = "INSERT INTO Meta (Key, Value) VALUES ('LastVacuumAt', @at) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
            record.Parameters.AddWithValue("@at", SqliteDateTimeText.Format(now));
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.writeSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> ReadLastVacuumAsync(CancellationToken cancellationToken)
    {
        await using var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Meta WHERE Key = 'LastVacuumAt';";
        return SqliteDateTimeText.ParseNullable(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string);
    }

    // ──────────────────────────────────────────────
    //  Read snapshots and journal pruning (IHistorySource)
    // ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<HistorySourceSnapshot> OpenSnapshotAsync(CancellationToken cancellationToken)
    {
        var connection = await this.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await HistorySourceSnapshot.BeginAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task PruneJournalAsync(long throughSeq, CancellationToken cancellationToken) =>
        this.WriteAsync(
            async transaction =>
            {
                var current = long.Parse(await this.GetMetaAsync(transaction, "JournalPrunedThrough", cancellationToken).ConfigureAwait(false) ?? "0", CultureInfo.InvariantCulture);
                if (throughSeq <= current)
                {
                    return false;
                }

                await using var cmd = this.WriterCommand(transaction, "DELETE FROM HistoryJournal WHERE Seq <= @through;");
                cmd.Parameters.AddWithValue("@through", throughSeq);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await this.SetMetaAsync(transaction, "JournalPrunedThrough", throughSeq.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
}
