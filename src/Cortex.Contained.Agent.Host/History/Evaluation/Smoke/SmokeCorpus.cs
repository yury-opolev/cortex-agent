using System.Diagnostics;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History.Evaluation.Smoke;

/// <summary>Result of running one system for one query: what the reader saw and how long the first page took.</summary>
public sealed record SystemRun(IReadOnlyList<ReturnedHit> Hits, double FirstPageMs);

/// <summary>
/// A private, consistent copy of <c>messages.db</c> in a run directory, with its own freshly built
/// index generation. The live database and live index are never touched (§14.2, D6).
/// </summary>
public sealed class SmokeCorpus : IAsyncDisposable
{
    private const int PageLimit = 30;

    private readonly MessageStore store;
    private readonly HistoryProjector projector;
    private readonly HistoryRecallService recall;
    private readonly string copyPath;

    private SmokeCorpus(MessageStore store, HistoryProjector projector, HistoryRecallService recall, string copyPath)
    {
        this.store = store;
        this.projector = projector;
        this.recall = recall;
        this.copyPath = copyPath;
    }

    /// <summary>Copies <paramref name="sourceDb"/> into <paramref name="runDirectory"/> (SQLite backup API), writes the manifest and builds the index.</summary>
    public static async Task<SmokeCorpus> CreateAsync(string sourceDb, string runDirectory, HistoryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(runDirectory);
        var copyPath = Path.Combine(runDirectory, "messages.db");
        using (var source = new SqliteConnection($"Data Source={sourceDb};Mode=ReadOnly;Pooling=False"))
        using (var target = new SqliteConnection($"Data Source={copyPath};Pooling=False"))
        {
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        }

        var store = new MessageStore(copyPath, NullLogger<MessageStore>.Instance);
        var monitor = new FixedOptionsMonitor<HistoryOptions>(options);
        var gate = new HistoryAccessGate();
        var status = new HistoryStatus();
        var projector = new HistoryProjector(store, store, new HistoryIndexGenerations(Path.Combine(runDirectory, "index")), gate, monitor, status, NullLogger<HistoryProjector>.Instance);
        await projector.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await projector.RebuildAsync(cancellationToken).ConfigureAwait(false);

        var corpus = new SmokeCorpus(store, projector, new HistoryRecallService(store, projector, gate, status, monitor), copyPath);
        var (from, to) = await corpus.TimeRangeAsync(cancellationToken).ConfigureAwait(false);
        SmokeWorkspace.WriteJson(Path.Combine(runDirectory, "manifest.json"), new SmokeRunManifest(from, to));
        return corpus;
    }

    /// <summary>System a/b: <c>history_search</c>, paging until <paramref name="maxWords"/> words were returned.</summary>
    public async Task<SystemRun> SearchAsync(SmokeQuerySpec spec, int maxWords, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var hits = new List<ReturnedHit>();
        var words = 0;
        string? cursor = null;
        double firstPageMs = 0;
        do
        {
            var watch = Stopwatch.StartNew();
            var result = await this.recall.SearchAsync(spec.ToRequest(PageLimit, cursor), cancellationToken).ConfigureAwait(false);
            if (hits.Count == 0)
            {
                firstPageMs = watch.Elapsed.TotalMilliseconds;
            }

            if (result.Error is not null)
            {
                break;
            }

            foreach (var hit in result.Hits)
            {
                var lines = hit.Lines.Select(l => new ReturnedLine(l.MessageId, ReadingBudgetMetrics.Words(l.Text))).ToList();
                hits.Add(new ReturnedHit(hit.MemberIds, lines));
                words += lines.Sum(l => l.Words);
            }

            cursor = result.HasMore ? HistoryCursorCodec.Encode(result.Binding, result.Next) : null;
        }
        while (cursor is not null && words < maxWords);

        return new SystemRun(hits, firstPageMs);
    }

    /// <summary>
    /// System c: term-wise <c>LIKE</c> — every folded term must occur (AND), same default categories
    /// as <c>history_search</c> (Internal excluded), newest first.
    /// </summary>
    public async Task<SystemRun> LikeBaselineAsync(string query, int maxRows, CancellationToken cancellationToken)
    {
        var terms = QueryParser.Split(query).Select(t => string.Join(' ', t.Tokens)).ToList();
        if (terms.Count == 0)
        {
            return new SystemRun([], 0);
        }

        var watch = Stopwatch.StartNew();
        await using var connection = new SqliteConnection($"Data Source={this.copyPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        connection.CreateFunction("history_fold", (string? text) => TextFolder.Fold(text));
        await using var cmd = connection.CreateCommand();
        var clauses = terms.Select((_, i) => $"history_fold(Content) LIKE @t{i}");
        cmd.CommandText = $"SELECT Id, Content FROM Messages WHERE Category IN (0, 2, 3) AND Role <> 'tool' AND {string.Join(" AND ", clauses)} ORDER BY Timestamp DESC, Id DESC LIMIT @max;";
        for (var i = 0; i < terms.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@t{i}", "%" + terms[i] + "%");
        }

        cmd.Parameters.AddWithValue("@max", maxRows);
        var hits = new List<ReturnedHit>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            hits.Add(new ReturnedHit([id], [new ReturnedLine(id, ReadingBudgetMetrics.Words(reader.GetString(1)))]));
        }

        return new SystemRun(hits, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>Message count and word count of every segment (for the histogram).</summary>
    public IReadOnlyList<(int Messages, int Words)> SegmentSizes()
    {
        using var reader = this.projector.Current!.OpenReader();
        return reader.ReadSegmentSizes();
    }

    /// <summary>Text of the given rows for owner labelling (pool command only; stays in the local workspace).</summary>
    public async Task<IReadOnlyList<MessageRecord>> ReadRowsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        await using var snapshot = await this.store.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await snapshot.ReadByIdsAsync(ids, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        this.projector.Dispose();
        await this.store.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<(DateTimeOffset From, DateTimeOffset To)> TimeRangeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await this.store.OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MIN(Timestamp), MAX(Timestamp) FROM Messages;";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return reader.IsDBNull(0)
            ? (DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            : (SqliteDateTimeText.Parse(reader.GetString(0)), SqliteDateTimeText.Parse(reader.GetString(1)));
    }

    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
