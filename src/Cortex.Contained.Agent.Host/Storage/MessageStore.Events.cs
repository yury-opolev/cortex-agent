using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Events;
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
            """;
        cmd.ExecuteNonQuery();
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
}
