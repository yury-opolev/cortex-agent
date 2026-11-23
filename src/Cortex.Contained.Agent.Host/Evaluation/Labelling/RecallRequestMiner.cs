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
