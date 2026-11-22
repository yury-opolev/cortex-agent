using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Integrity;

/// <summary>Invariants of the increment 2 integrity gate, checked after every scenario.</summary>
internal static class IntegrityAssertions
{
    /// <summary>
    /// Every message row has an event; no event points at a missing row; every committed purge left
    /// exactly one tombstone; the journal head matches the journal.
    /// </summary>
    public static async Task AssertEventLogConsistentAsync(MessageStore store)
    {
        await using var connection = await store.OpenReaderAsync(CancellationToken.None);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Messages m WHERE NOT EXISTS (SELECT 1 FROM Events e WHERE e.MessageRowId = m.Id);"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Events WHERE MessageRowId IS NOT NULL AND MessageRowId NOT IN (SELECT Id FROM Messages);"));
        Assert.Equal(
            await ScalarAsync(connection, "SELECT CAST(Value AS INTEGER) FROM Meta WHERE Key = 'PurgeGeneration';"),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM Events WHERE Type = 'history_purged';"));
        Assert.True(
            await ScalarAsync(connection, "SELECT CAST(Value AS INTEGER) FROM Meta WHERE Key = 'JournalHead';")
            >= await ScalarAsync(connection, "SELECT COALESCE(MAX(Seq), 0) FROM HistoryJournal;"));
    }

    /// <summary>Segments (without row ids) and postings of a generation, for equivalence checks.</summary>
    public static (IReadOnlyList<string> Segments, IReadOnlyList<string> Postings) Snapshot(HistoryIndexDatabase database)
    {
        using var reader = database.OpenReader();
        var segments = reader.ReadSegments()
            .Select(s => $"{s.ChannelId}|{s.StartTs}|{s.EndTs}|{string.Join(',', s.MsgIds)}|{s.HasInternal}|{s.HasRecallEcho}|{s.Fingerprint}")
            .ToList();
        return (segments, reader.ReadPostings());
    }

    /// <summary>A full rebuild of <paramref name="store"/> into a fresh directory, for comparison with the incremental index.</summary>
    public static async Task<(IReadOnlyList<string> Segments, IReadOnlyList<string> Postings)> RebuildSnapshotAsync(
        MessageStore store,
        Microsoft.Extensions.Options.IOptionsMonitor<HistoryOptions> options,
        string directory)
    {
        using var projector = new HistoryProjector(
            store, store, new HistoryIndexGenerations(directory), new HistoryAccessGate(), options, new HistoryStatus(), NullLogger<HistoryProjector>.Instance);
        await projector.InitializeAsync(CancellationToken.None);
        await projector.RebuildAsync(CancellationToken.None);
        return Snapshot(projector.Current!);
    }

    private static async Task<long> ScalarAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
