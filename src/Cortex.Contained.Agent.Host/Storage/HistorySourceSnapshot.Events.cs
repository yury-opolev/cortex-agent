using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Events;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Event-log reads on the snapshot (same read view as the message rows they reference).</summary>
public sealed partial class HistorySourceSnapshot
{
    private const string EventColumns = "Seq, Id, Ts, Type, AuthorKind, AuthorId, ChannelId, RunId, CausationId, MessageRowId, OperationId, Payload";

    /// <summary>The event with <paramref name="eventId"/>, or null.</summary>
    public async Task<EventRecord?> ReadEventAsync(string eventId, CancellationToken cancellationToken)
    {
        var events = await this.QueryEventsAsync(
            $"SELECT {EventColumns} FROM Events WHERE Id = @value;",
            eventId,
            cancellationToken).ConfigureAwait(false);
        return events.Count > 0 ? events[0] : null;
    }

    /// <summary>Every event of run <paramref name="runId"/>, oldest first.</summary>
    public Task<IReadOnlyList<EventRecord>> ReadRunEventsAsync(string runId, CancellationToken cancellationToken) =>
        this.QueryEventsAsync($"SELECT {EventColumns} FROM Events WHERE RunId = @value ORDER BY Seq;", runId, cancellationToken);

    /// <summary>Every event of send operation <paramref name="operationId"/>, oldest first.</summary>
    public Task<IReadOnlyList<EventRecord>> ReadOperationEventsAsync(string operationId, CancellationToken cancellationToken) =>
        this.QueryEventsAsync($"SELECT {EventColumns} FROM Events WHERE OperationId = @value ORDER BY Seq;", operationId, cancellationToken);

    /// <summary>Every event that describes message row <paramref name="messageRowId"/>, oldest first.</summary>
    public Task<IReadOnlyList<EventRecord>> ReadMessageEventsAsync(long messageRowId, CancellationToken cancellationToken) =>
        this.QueryEventsAsync($"SELECT {EventColumns} FROM Events WHERE MessageRowId = @value ORDER BY Seq;", messageRowId, cancellationToken);

    internal static EventRecord ReadEvent(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        SqliteDateTimeText.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        JsonNode.Parse(reader.GetString(11)) as JsonObject ?? new JsonObject());

    private async Task<IReadOnlyList<EventRecord>> QueryEventsAsync(string sql, object value, CancellationToken cancellationToken)
    {
        var events = new List<EventRecord>();
        await using var cmd = this.Command(sql);
        cmd.Parameters.AddWithValue("@value", value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }
}
