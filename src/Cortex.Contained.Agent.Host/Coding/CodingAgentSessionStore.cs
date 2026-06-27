using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Coding;

/// <summary>
/// SQLite-backed store for the <c>(channelId, sessionId)</c> binding and per-session
/// activity timestamps. Source of truth for resumption across Bridge restarts.
/// </summary>
public sealed partial class CodingAgentSessionStore : SqliteStoreBase
{
    private readonly Lock syncLock = new();

    /// <summary>
    /// Initialises the session store, opening the SQLite database at <paramref name="stateRoot"/>/external-agent/sessions.db.
    /// </summary>
    public CodingAgentSessionStore(string stateRoot)
        : base(PrepareDatabasePath(stateRoot, "external-agent", "sessions.db"), enableWalMode: true)
    {
        this.EnsureSchema();
    }

    public void Upsert(CodingAgentSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO external_agent_sessions
                    (session_id, channel_id, working_folder, policy, session_name, state,
                     created_at, last_activity_at, last_user_message, last_assistant_summary,
                     last_tool_calls, ended_at)
                VALUES
                    ($id, $channel, $folder, $policy, $name, $state,
                     $created, $activity, $userMsg, $assistantSummary,
                     $toolCalls, $ended)
                ON CONFLICT(session_id) DO UPDATE SET
                    channel_id = excluded.channel_id,
                    working_folder = excluded.working_folder,
                    policy = excluded.policy,
                    session_name = excluded.session_name,
                    state = excluded.state,
                    last_activity_at = excluded.last_activity_at,
                    last_user_message = COALESCE(excluded.last_user_message, last_user_message),
                    last_assistant_summary = COALESCE(excluded.last_assistant_summary, last_assistant_summary),
                    last_tool_calls = COALESCE(excluded.last_tool_calls, last_tool_calls),
                    ended_at = excluded.ended_at;
                """;
            cmd.Parameters.AddWithValue("$id", record.SessionId);
            cmd.Parameters.AddWithValue("$channel", record.ChannelId);
            cmd.Parameters.AddWithValue("$folder", record.WorkingFolder);
            cmd.Parameters.AddWithValue("$policy", (int)record.Policy);
            cmd.Parameters.AddWithValue("$name", (object?)record.SessionName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", (int)record.State);
            cmd.Parameters.AddWithValue("$created", record.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$activity", record.LastActivityAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$userMsg", (object?)record.LastUserMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$assistantSummary", (object?)record.LastAssistantSummary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$toolCalls", (object?)record.LastToolCallsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ended", (object?)record.EndedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public CodingAgentSessionRecord? GetById(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM external_agent_sessions WHERE session_id = $id";
            cmd.Parameters.AddWithValue("$id", sessionId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        }
    }

    public IReadOnlyList<CodingAgentSessionRecord> ListActiveByChannel(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        var rows = new List<CodingAgentSessionRecord>();
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM external_agent_sessions
                WHERE channel_id = $channel AND ended_at IS NULL
                ORDER BY last_activity_at DESC
                """;
            cmd.Parameters.AddWithValue("$channel", channelId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(Map(reader));
            }
        }

        return rows;
    }

    public IReadOnlyList<CodingAgentSessionRecord> ListAll()
    {
        var rows = new List<CodingAgentSessionRecord>();
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM external_agent_sessions ORDER BY last_activity_at DESC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(Map(reader));
            }
        }

        return rows;
    }

    public IReadOnlyList<CodingAgentSessionRecord> ListIdleSince(DateTimeOffset cutoff)
    {
        var rows = new List<CodingAgentSessionRecord>();
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM external_agent_sessions
                WHERE ended_at IS NULL AND last_activity_at < $cutoff
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(Map(reader));
            }
        }

        return rows;
    }

    public void MarkEnded(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (this.syncLock)
        {
            using var cmd = this.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE external_agent_sessions
                SET state = $state, ended_at = $ended
                WHERE session_id = $id
                """;
            cmd.Parameters.AddWithValue("$state", (int)CodingSessionState.Ended);
            cmd.Parameters.AddWithValue("$ended", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    private void EnsureSchema()
    {
        this.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS external_agent_sessions (
                session_id              TEXT PRIMARY KEY,
                channel_id              TEXT NOT NULL,
                working_folder          TEXT NOT NULL,
                policy                  INTEGER NOT NULL,
                session_name            TEXT,
                state                   INTEGER NOT NULL,
                created_at              TEXT NOT NULL,
                last_activity_at        TEXT NOT NULL,
                last_user_message       TEXT,
                last_assistant_summary  TEXT,
                last_tool_calls         TEXT,
                ended_at                TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_external_agent_sessions_channel
                ON external_agent_sessions(channel_id, last_activity_at);
            CREATE INDEX IF NOT EXISTS ix_external_agent_sessions_idle
                ON external_agent_sessions(last_activity_at) WHERE ended_at IS NULL;
            """);
    }

    private static CodingAgentSessionRecord Map(SqliteDataReader reader)
    {
        return new CodingAgentSessionRecord
        {
            SessionId = reader.GetString(reader.GetOrdinal("session_id")),
            ChannelId = reader.GetString(reader.GetOrdinal("channel_id")),
            WorkingFolder = reader.GetString(reader.GetOrdinal("working_folder")),
            Policy = (CodingPolicy)reader.GetInt32(reader.GetOrdinal("policy")),
            SessionName = ReadNullableString(reader, "session_name"),
            State = (CodingSessionState)reader.GetInt32(reader.GetOrdinal("state")),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture),
            LastActivityAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_activity_at")), CultureInfo.InvariantCulture),
            LastUserMessage = ReadNullableString(reader, "last_user_message"),
            LastAssistantSummary = ReadNullableString(reader, "last_assistant_summary"),
            LastToolCallsJson = ReadNullableString(reader, "last_tool_calls"),
            EndedAt = ReadNullableDate(reader, "ended_at"),
        };
    }

    private static string? ReadNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string column)
    {
        var s = ReadNullableString(reader, column);
        return s is null ? null : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
    }

    public static string SerializeToolCalls(IReadOnlyList<CodingToolCall> calls)
    {
        return JsonSerializer.Serialize(calls);
    }

    public static IReadOnlyList<CodingToolCall> DeserializeToolCalls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CodingToolCall>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
