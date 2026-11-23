using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// Real recall requests (the co-primary stratum of §4.2), mined from the agent's own past searches: every
/// <c>tool_call</c> event of <c>history_search</c> or <c>memory_search</c> with recorded arguments, paired with the
/// owner's message that preceded it in the same channel. One seed per owner message (the first search counts); the
/// recorded arguments are the model-written query. Reads ids, times and the (already redacted) arguments only.
/// </summary>
public static class RecallRequestMiner
{
    /// <summary>Seeds for the searches of <paramref name="tool"/> in the database at <paramref name="db"/>.</summary>
    public static IReadOnlyList<LabelSeed> Read(string db, string? password, string tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        if (!File.Exists(db))
        {
            return [];
        }

        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(db, password, SqliteOpenMode.ReadOnly, pooling: false));
        connection.Open();
        return Read(connection, tool);
    }

    /// <summary>Seeds for the searches of <paramref name="tool"/> through an open connection to a <c>messages.db</c> (or a copy).</summary>
    public static IReadOnlyList<LabelSeed> Read(SqliteConnection connection, string tool)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.Ts, json_extract(e.Payload, '$.args'),
                   (SELECT m.Id FROM Messages m
                     WHERE m.ChannelId = e.ChannelId AND m.Role = 'user' AND m.Category = 0 AND m.Timestamp <= e.Ts
                     ORDER BY m.Timestamp DESC, m.Id DESC LIMIT 1)
              FROM Events e
             WHERE e.Type = 'tool_call' AND e.ChannelId IS NOT NULL
               AND json_extract(e.Payload, '$.tool') = @tool
               AND json_extract(e.Payload, '$.args') IS NOT NULL
               AND json_extract(e.Payload, '$.redaction') IS NULL
             ORDER BY e.Seq;
            """;
        cmd.Parameters.AddWithValue("@tool", tool);
        using var reader = cmd.ExecuteReader();
        var seeds = new List<LabelSeed>();
        var seen = new HashSet<long>();
        while (reader.Read())
        {
            if (reader.IsDBNull(2) || !seen.Add(reader.GetInt64(2)))
            {
                continue;
            }

            seeds.Add(new LabelSeed
            {
                SeedId = string.Empty,
                Category = "real",
                Stratum = "real",
                RequestMessageId = reader.GetInt64(2).ToString(CultureInfo.InvariantCulture),
                RequestTime = SqliteDateTimeText.Parse(reader.GetString(0)),
                ModelArgs = IsJsonObject(reader.GetString(1)) ? reader.GetString(1) : null,
            });
        }

        return seeds;
    }

    /// <summary>The owner's request <paramref name="messageId"/> and up to <paramref name="turns"/> earlier user/assistant turns of its channel (oldest first); null when the message is gone.</summary>
    public static SeedMaterial? Material(SqliteConnection connection, long messageId, int turns)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.Content,
                   (SELECT json_group_array(Role || ': ' || Content) FROM (
                       SELECT m.Role, m.Content FROM Messages m
                        WHERE m.ChannelId = r.ChannelId AND m.Role IN ('user', 'assistant') AND m.Category IN (0, 3)
                          AND (m.Timestamp < r.Timestamp OR (m.Timestamp = r.Timestamp AND m.Id < r.Id))
                        ORDER BY m.Timestamp DESC, m.Id DESC LIMIT @turns))
              FROM Messages r WHERE r.Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", messageId);
        cmd.Parameters.AddWithValue("@turns", turns);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var context = JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [];
        context.Reverse();
        return new SeedMaterial([], reader.GetString(0)) { Context = context };
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
