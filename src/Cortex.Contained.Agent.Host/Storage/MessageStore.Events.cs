using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// The event log in <c>messages.db</c> (inc 2 spec §4): schema, the in-transaction append used by
/// every <c>Messages</c> mutation, and <see cref="IEventLog"/>.
/// </summary>
public sealed partial class MessageStore : IEventLog
{
    /// <summary>Largest allowed serialized payload.</summary>
    internal const int MaxEventPayloadBytes = 8192;

    private static readonly JsonSerializerOptions eventJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <inheritdoc />
    public Task<string> AppendAsync(EventDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return this.WriteAsync(transaction => this.InsertEventAsync(transaction, draft, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EventRecord?> GetAsync(string eventId, CancellationToken cancellationToken)
    {
        await using var snapshot = await this.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await snapshot.ReadEventAsync(eventId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventRecord>> ListRunAsync(string runId, CancellationToken cancellationToken)
    {
        await using var snapshot = await this.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await snapshot.ReadRunEventsAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventRecord>> ListOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        await using var snapshot = await this.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await snapshot.ReadOperationEventsAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    private void InitializeEventsSchema()
    {
        using var cmd = this.writer.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Events (
                Seq            INTEGER PRIMARY KEY AUTOINCREMENT,
                Id             TEXT    NOT NULL UNIQUE,
                Ts             TEXT    NOT NULL,
                Type           TEXT    NOT NULL,
                AuthorKind     TEXT    NOT NULL,
                AuthorId       TEXT    NULL,
                ChannelId      TEXT    NULL,
                RunId          TEXT    NULL,
                CausationId    TEXT    NULL,
                MessageRowId   INTEGER NULL,
                OperationId    TEXT    NULL,
                IdempotencyKey TEXT    NULL UNIQUE,
                Payload        TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_channel_ts ON Events (ChannelId, Ts);
            CREATE INDEX IF NOT EXISTS ix_events_run        ON Events (RunId);
            CREATE INDEX IF NOT EXISTS ix_events_message    ON Events (MessageRowId);
            CREATE INDEX IF NOT EXISTS ix_events_operation  ON Events (OperationId);

            INSERT OR IGNORE INTO Meta (Key, Value) VALUES ('PurgeGeneration', '0');
            """;
        cmd.ExecuteNonQuery();

        if (!this.ColumnExists("HistoryJournal", "PurgeGeneration"))
        {
            using var alter = this.writer.CreateCommand();
            alter.CommandText = "ALTER TABLE HistoryJournal ADD COLUMN PurgeGeneration INTEGER NULL;";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>Increments <c>Meta.PurgeGeneration</c> inside the purge transaction and returns the new value.</summary>
    private async Task<long> BumpPurgeGenerationAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var current = long.Parse(await this.GetMetaAsync(transaction, "PurgeGeneration", cancellationToken).ConfigureAwait(false) ?? "0", CultureInfo.InvariantCulture);
        var next = current + 1;
        await this.SetMetaAsync(transaction, "PurgeGeneration", next.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        return next;
    }

    /// <summary>
    /// Deletes the events in <paramref name="scope"/> (tombstones excepted) and every event whose
    /// message row no longer exists. Returns the count and the tool-output spill files the deleted
    /// tool results referenced, so the erasure workflow can delete exactly those files.
    /// </summary>
    private async Task<(int Deleted, IReadOnlyList<string> SpillFiles)> DeleteEventsAsync(
        SqliteTransaction transaction,
        HistoryPurgeScope scope,
        CancellationToken cancellationToken)
    {
        var (where, bind) = PurgeFilter(scope, "Ts");
        var spillFiles = new List<string>();
        await using (var select = this.WriterCommand(transaction, $"SELECT json_extract(Payload, '$.spill') FROM Events WHERE Type = 'tool_result' AND json_extract(Payload, '$.spill') IS NOT NULL AND {where};"))
        {
            bind(select);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                spillFiles.Add(reader.GetString(0));
            }
        }

        int deleted;
        await using (var delete = this.WriterCommand(transaction, $"DELETE FROM Events WHERE Type <> 'history_purged' AND {where};"))
        {
            bind(delete);
            deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var orphans = this.WriterCommand(transaction, "DELETE FROM Events WHERE MessageRowId IS NOT NULL AND MessageRowId NOT IN (SELECT Id FROM Messages);"))
        {
            deleted += await orphans.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return (deleted, spillFiles);
    }

    /// <summary>Appends one event inside <paramref name="transaction"/>; idempotent on <see cref="EventDraft.IdempotencyKey"/>.</summary>
    private async Task<string> InsertEventAsync(SqliteTransaction transaction, EventDraft draft, CancellationToken cancellationToken)
    {
        var payload = draft.Payload.ToJsonString(eventJson);
        if (Encoding.UTF8.GetByteCount(payload) > MaxEventPayloadBytes)
        {
            throw new InvalidOperationException($"Event payload of type {draft.Type} exceeds {MaxEventPayloadBytes} bytes.");
        }

        if (draft.IdempotencyKey is not null)
        {
            await using var find = this.WriterCommand(transaction, "SELECT Id FROM Events WHERE IdempotencyKey = @key;");
            find.Parameters.AddWithValue("@key", draft.IdempotencyKey);
            if (await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string existing)
            {
                return existing;
            }
        }

        var id = EventIds.NewEventId();
        await using var cmd = this.WriterCommand(transaction, """
            INSERT INTO Events (Id, Ts, Type, AuthorKind, AuthorId, ChannelId, RunId, CausationId, MessageRowId, OperationId, IdempotencyKey, Payload)
            VALUES (@id, @ts, @type, @authorKind, @authorId, @channelId, @runId, @causationId, @messageRowId, @operationId, @key, @payload);
            """);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@ts", SqliteDateTimeText.Format(draft.Ts ?? DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("@type", draft.Type);
        cmd.Parameters.AddWithValue("@authorKind", draft.AuthorKind);
        cmd.Parameters.AddWithValue("@authorId", (object?)draft.AuthorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@channelId", (object?)draft.ChannelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@runId", (object?)draft.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@causationId", (object?)draft.CausationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@messageRowId", (object?)draft.MessageRowId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@operationId", (object?)draft.OperationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@key", (object?)draft.IdempotencyKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@payload", payload);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        this.FaultInjector?.Invoke("after-event-write");
        return id;
    }

    /// <summary>The first event that describes row <paramref name="messageRowId"/> (the one a correction supersedes), or null.</summary>
    private async Task<string?> FirstMessageEventIdAsync(SqliteTransaction transaction, long messageRowId, CancellationToken cancellationToken)
    {
        await using var cmd = this.WriterCommand(transaction, "SELECT Id FROM Events WHERE MessageRowId = @id ORDER BY Seq LIMIT 1;");
        cmd.Parameters.AddWithValue("@id", messageRowId);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
}
