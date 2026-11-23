using System.Diagnostics;
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>
/// A private copy of <c>messages.db</c> with one fully embedded index generation per keyword variant
/// (§4.2 ablations). The live database and index are never touched; everything stays in the run
/// directory, which holds raw message text (D6): outputs beside it are numbers only. On an encrypted
/// install the copy and its index generations are encrypted with the evaluation ring (<see cref="EvalProtection"/>). A
/// <c>manifest.json</c> with the copied corpus's time range is written first, so a purge overlapping
/// that range destroys the run directory (purge erasure covers the <c>runs</c> of the configured
/// <c>History:EvalArtifactsDirectory</c>); the owner otherwise deletes the run directory after use.
/// </summary>
public sealed class EvalCorpus : IAsyncDisposable
{
    private const int PageLimit = 30;
    private const int MaxWords = 3000;

    private readonly MessageStore store;
    private readonly MutableOptionsMonitor monitor;
    private readonly IRerankStage? reranker;
    private readonly Dictionary<HistoryKeywordVariant, (HistoryProjector Projector, HistoryRecallService Recall)> variants = [];

    private EvalCorpus(MessageStore store, MutableOptionsMonitor monitor, IRerankStage? reranker)
    {
        this.store = store;
        this.monitor = monitor;
        this.reranker = reranker;
    }

    /// <summary>
    /// Copies <paramref name="sourceDb"/> and builds an embedded generation for every keyword variant in
    /// <paramref name="configs"/>. <paramref name="embedderFactory"/> receives the options monitor the
    /// corpus switches per configuration (so the query-instruction ablation reaches the embedder).
    /// <paramref name="reranker"/> serves configurations with a rerank budget (§4.3). <paramref name="protection"/>
    /// (default plaintext) decides how the source is read and how the copy is encrypted.
    /// </summary>
    public static async Task<EvalCorpus> CreateAsync(
        string sourceDb,
        string runDirectory,
        HistoryOptions baseline,
        IReadOnlyList<AblationConfig> configs,
        Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
        CancellationToken cancellationToken,
        IRerankStage? reranker = null,
        EvalProtection? protection = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(embedderFactory);
        protection ??= EvalProtection.Plaintext;
        Directory.CreateDirectory(runDirectory);
        var copyPath = Path.Combine(runDirectory, "messages.db");
        var copyPassword = protection.CopyPassword(MessageStore.PasswordPurpose);
        EncryptedSqlite.CopyDatabase(sourceDb, protection.SourcePassword(MessageStore.PasswordPurpose), copyPath, copyPassword);

        var manifest = ReadCorpusRange(copyPath, copyPassword);
        EvalWorkspace.WriteJson(Path.Combine(runDirectory, "manifest.json"), manifest);

        var monitor = new MutableOptionsMonitor(baseline);
        var embedder = embedderFactory(monitor);
        var corpus = new EvalCorpus(new MessageStore(copyPath, NullLogger<MessageStore>.Instance, protection.CopyMessageEncryption), monitor, reranker);
        foreach (var variant in configs.Select(c => c.KeywordVariant).Distinct())
        {
            monitor.CurrentValue = new AblationConfig { Name = "build", KeywordVariant = variant }.Apply(baseline);
            var clock = new EvalClock();
            var gate = new HistoryAccessGate();
            var status = new HistoryStatus();
            var projector = new HistoryProjector(
                corpus.store, corpus.store, new HistoryIndexGenerations(Path.Combine(runDirectory, "index-" + variant.ToString().ToLowerInvariant()), protection.Ring),
                gate, monitor, status, NullLogger<HistoryProjector>.Instance, embedder, clock);
            await projector.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await projector.RebuildAsync(cancellationToken).ConfigureAwait(false);
            clock.Now += TimeSpan.FromDays(1); // every open segment is past its debounce
            while ((await projector.EmbedPendingAsync(cancellationToken).ConfigureAwait(false)).Published > 0)
            {
            }

            corpus.variants[variant] = (projector, new HistoryRecallService(corpus.store, projector, gate, status, monitor, embedder, clock, reranker));
        }

        return corpus;
    }

    /// <summary>Runs <paramref name="config"/> for <paramref name="query"/>, paging until <c>3000</c> words were returned.</summary>
    public async Task<RetrievalRun> SearchAsync(AblationConfig config, EvalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(query);
        var (_, recall) = this.variants[config.KeywordVariant];
        this.monitor.CurrentValue = config.Apply(this.monitor.Baseline);
        var rerank = config.RerankBudget();
        if (rerank is not null && this.reranker is null)
        {
            throw new InvalidOperationException($"Configuration '{config.Name}' reranks, but this run has no reranker: evaluate rerank configurations with 'rerank-curve' or 'gate-r'.");
        }

        var model = config.UseModelQuery ? query.ModelQuery : null;
        var filters = model is not null ? model.Filters : query.GoldFilters;
        var hits = new List<ReturnedHit>();
        RerankOutcome? rerankOutcome = null;
        var words = 0;
        string? cursor = null;
        double confidence = 0;
        var watch = Stopwatch.StartNew();
        double latency = 0;
        do
        {
            var result = await recall.SearchAsync(
                new HistorySearchRequest
                {
                    Query = model?.Text ?? query.Text,
                    Channel = filters?.Channel,
                    Since = filters?.Since,
                    Until = filters?.Until,
                    Mode = config.Mode,
                    Limit = PageLimit,
                    Cursor = cursor,
                    Rerank = rerank is null ? RerankRequest.Off : RerankRequest.With(rerank),
                },
                cancellationToken).ConfigureAwait(false);
            if (cursor is null)
            {
                latency = watch.Elapsed.TotalMilliseconds;
                confidence = result.TopSimilarity ?? 0;
                rerankOutcome = result.RerankOutcome;
                if (config.Mode != HistorySearchMode.Keyword && result.Error is null && result.Mode != HistorySearchMode.Hybrid)
                {
                    // A degraded run would score a different configuration than the one named in the report.
                    throw new InvalidOperationException(
                        $"Configuration '{config.Name}' ran keyword-only for query '{query.Id}' ({result.Coverage.Dense}). Fix the cause and run again.");
                }
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
        while (cursor is not null && words < MaxWords);

        return new RetrievalRun(hits, confidence, latency) { Rerank = rerankOutcome };
    }

    /// <summary>Texts of the given rows (paraphrase guard only; never written out).</summary>
    public async Task<IReadOnlyList<MessageRecord>> ReadRowsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        await using var snapshot = await this.store.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await snapshot.ReadByIdsAsync(ids, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Segment document frequency of a folded term (paraphrase guard).</summary>
    public long DocumentFrequency(string term)
    {
        using var reader = this.variants.Values.First().Projector.Current!.OpenReader();
        return reader.DocumentFrequency(term);
    }

    /// <summary>Number of indexed segments (paraphrase guard).</summary>
    public long SegmentCount()
    {
        using var reader = this.variants.Values.First().Projector.Current!.OpenReader();
        return reader.SegmentCount();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var (projector, _) in this.variants.Values)
        {
            projector.Dispose();
        }

        await this.store.DisposeAsync().ConfigureAwait(false);
    }

    private static SmokeRunManifest ReadCorpusRange(string copyPath, string? password)
    {
        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(copyPath, password, SqliteOpenMode.ReadOnly, pooling: false));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MIN(Timestamp), MAX(Timestamp) FROM Messages;";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return reader.IsDBNull(0)
            ? new SmokeRunManifest(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            : new SmokeRunManifest(SqliteDateTimeText.Parse(reader.GetString(0)), SqliteDateTimeText.Parse(reader.GetString(1)));
    }

    private sealed class MutableOptionsMonitor(HistoryOptions baseline) : IOptionsMonitor<HistoryOptions>
    {
        public HistoryOptions Baseline { get; } = baseline;

        public HistoryOptions CurrentValue { get; set; } = baseline;

        public HistoryOptions Get(string? name) => this.CurrentValue;

        public IDisposable? OnChange(Action<HistoryOptions, string?> listener) => null;
    }

    private sealed class EvalClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => this.Now;
    }
}
