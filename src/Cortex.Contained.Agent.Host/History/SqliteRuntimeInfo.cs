using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Reports the SQLite engine linked into this process and whether it can host the history
/// index (FTS5 with <c>contentless_delete</c>, SQLite 3.43+, and the FTS5 <c>secure-delete</c>
/// option, SQLite 3.42+). Probed once per process against an in-memory database.
/// </summary>
public static class SqliteRuntimeInfo
{
    /// <summary>The oldest SQLite version that supports every FTS5 option the history index uses.</summary>
    public static readonly Version MinimumHistoryIndexVersion = new(3, 43, 0);

    private static readonly Lazy<SqliteCapabilities> capabilities = new(Probe);

    /// <summary>The probed capabilities of the linked SQLite engine.</summary>
    public static SqliteCapabilities Capabilities => capabilities.Value;

    private static SqliteCapabilities Probe()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var versionText = Scalar(connection, "SELECT sqlite_version();");
        if (!Version.TryParse(versionText, out var version) || version < MinimumHistoryIndexVersion)
        {
            return new SqliteCapabilities(versionText, false, $"SQLite {versionText} is older than {MinimumHistoryIndexVersion}.");
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE VIRTUAL TABLE probe_fts USING fts5(
                  body, content='', contentless_delete=1,
                  tokenize='unicode61 remove_diacritics 2', prefix='3 4');
                INSERT INTO probe_fts(probe_fts, rank) VALUES('secure-delete', 1);
                CREATE VIRTUAL TABLE probe_vocab USING fts5vocab(probe_fts, 'row');
                INSERT INTO probe_fts(rowid, body) VALUES (1, 'probe');
                DELETE FROM probe_fts WHERE rowid = 1;
                """;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            return new SqliteCapabilities(versionText, false, ex.Message);
        }

        return new SqliteCapabilities(versionText, true, null);
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

/// <summary>Result of probing the linked SQLite engine.</summary>
/// <param name="Version">The value of <c>sqlite_version()</c>.</param>
/// <param name="SupportsHistoryIndex">True when every FTS5 feature of the history index works.</param>
/// <param name="Error">Why the engine is unsuitable, or null.</param>
public sealed record SqliteCapabilities(string Version, bool SupportsHistoryIndex, string? Error);
