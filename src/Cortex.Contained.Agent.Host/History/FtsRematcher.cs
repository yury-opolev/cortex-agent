using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Re-matches a query against source rows with a temporary in-memory FTS5 table that uses the
/// same tokenizer, prefix configuration and folding as the index (§10 step 3), so a verified hit
/// has exactly the index's match semantics on the current row content.
/// </summary>
public static class FtsRematcher
{
    /// <summary>Returns the ids of <paramref name="rows"/> whose indexed body matches <paramref name="matchExpression"/>.</summary>
    public static IReadOnlySet<long> Match(string matchExpression, IEnumerable<MessageRecord> rows) =>
        Match(matchExpression, rows, HistoryKeywordVariant.Plain);

    /// <summary>
    /// Like <see cref="Match(string, IEnumerable{MessageRecord})"/> on the table <paramref name="table"/>
    /// queries: <c>(body, stem)</c> with stems for <see cref="HistoryKeywordVariant.Stemmed"/>, the
    /// trigram tokenizer for <see cref="HistoryKeywordVariant.Trigram"/>.
    /// </summary>
    public static IReadOnlySet<long> Match(string matchExpression, IEnumerable<MessageRecord> rows, HistoryKeywordVariant table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchExpression);
        ArgumentNullException.ThrowIfNull(rows);

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = table == HistoryKeywordVariant.Trigram
                ? $"CREATE VIRTUAL TABLE m USING fts5(body, stem UNINDEXED, tokenize='{HistoryConfigFingerprint.TrigramTokenizerSpec}');"
                : $"CREATE VIRTUAL TABLE m USING fts5(body, stem, tokenize='{HistoryConfigFingerprint.TokenizerSpec}', prefix='{HistoryConfigFingerprint.PrefixSpec}');";
            create.ExecuteNonQuery();
        }

        using (var transaction = connection.BeginTransaction())
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO m(rowid, body, stem) VALUES (@id, @body, @stem);";
            var id = insert.Parameters.Add("@id", SqliteType.Integer);
            var body = insert.Parameters.Add("@body", SqliteType.Text);
            var stem = insert.Parameters.Add("@stem", SqliteType.Text);
            foreach (var row in rows)
            {
                var indexed = HistoryRowText.IndexedBody(row);
                id.Value = row.Id;
                body.Value = indexed;
                stem.Value = table == HistoryKeywordVariant.Stemmed ? HistoryStemmer.StemBody(indexed) : string.Empty;
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var matched = new HashSet<long>();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT rowid FROM m WHERE m MATCH @match;";
        query.Parameters.AddWithValue("@match", matchExpression);
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            matched.Add(reader.GetInt64(0));
        }

        return matched;
    }
}
