using System.Globalization;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// <see cref="ILabelSource"/> over a private, embedded copy of <c>messages.db</c> (<see cref="EvalCorpus"/>): user and
/// assistant messages (not recall echoes, not internal rows) as seeds, <c>history_search</c> calls as real requests,
/// the ablation grid's configurations (without reranking) as the pooled systems, and the corpus's own document
/// frequencies for the paraphrase guard.
/// </summary>
public sealed class HistoryLabelSource : ILabelSource
{
    /// <summary>Turns shown before a real request.</summary>
    public const int ContextTurns = 4;

    private readonly EvalCorpus corpus;
    private readonly EvalWorkspace workspace;
    private readonly IReadOnlyList<AblationConfig> pooled;
    private long? segments;

    /// <param name="corpus">The embedded copy (built for every keyword variant of <paramref name="pooled"/>).</param>
    /// <param name="workspace">Where the dataset is written (its protection seals it).</param>
    /// <param name="pooled">The configurations whose top-k is pooled (reranked ones are left out).</param>
    public HistoryLabelSource(EvalCorpus corpus, EvalWorkspace workspace, IReadOnlyList<AblationConfig> pooled)
    {
        ArgumentNullException.ThrowIfNull(pooled);
        this.corpus = corpus;
        this.workspace = workspace;
        this.pooled = pooled.Where(c => c.RerankBudget() is null).ToList();
    }

    public string Tool => "history-eval";

    public int MinimumQueries => 0;

    public Task<IReadOnlyList<LabelSeed>> SampleAsync(int count, double realShare, CancellationToken cancellationToken)
    {
        using var connection = this.corpus.OpenCopy();
        var messages = new List<SourceMessage>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, ChannelId, Role, Timestamp, Content, ToolCalls FROM Messages WHERE Role IN ('user', 'assistant') AND Category IN (0, 3) ORDER BY Id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var echo = !reader.IsDBNull(5) && ToolCallSummary.ParseJson(reader.GetString(5))
                    .Any(e => ToolCallSummary.IsRecallSource(e.Name) || ToolCallSummary.IsRecallTurnMarker(e));
                if (!echo)
                {
                    messages.Add(new SourceMessage(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), SqliteDateTimeText.Parse(reader.GetString(3)), reader.GetString(4)));
                }
            }
        }

        var real = RecallRequestMiner.Read(connection, "history_search");
        return Task.FromResult(LabelSeeds.History(messages, real, count, realShare));
    }

    public async Task<SeedMaterial?> MaterialAsync(LabelSeed seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Stratum == "real")
        {
            using var connection = this.corpus.OpenCopy();
            return seed.RequestMessageId is { } id ? RecallRequestMiner.Material(connection, long.Parse(id, CultureInfo.InvariantCulture), ContextTurns) : null;
        }

        var items = await this.ReadAsync(seed.SourceIds, cancellationToken).ConfigureAwait(false);
        return items.Count == seed.SourceIds.Count ? new SeedMaterial(seed.SourceIds.Select(id => items[id]).ToList()) : null;
    }

    public async Task<IReadOnlyList<string>> PoolAsync(DraftedQuery query, int depth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var probe = new EvalQuery
        {
            Id = query.Id,
            Text = query.Text,
            Category = query.Category,
            GoldFilters = query.GoldFilters,
            ModelQuery = query.ModelText is null ? null : new EvalModelQuery { Text = query.ModelText, Filters = query.ModelFilters },
        };
        var lists = new List<IReadOnlyList<long>>();
        foreach (var config in this.pooled)
        {
            var run = await this.corpus.SearchAsync(config, probe, cancellationToken).ConfigureAwait(false);
            lists.Add(RankingMetrics.RankedMessages(run.Hits).Take(depth).ToList());
        }

        // Interleaved by rank, so a cap on the pool keeps every system's best results.
        var union = Enumerable.Range(0, depth).SelectMany(rank => lists.Where(l => rank < l.Count).Select(l => l[rank])).Distinct().ToList();
        if (query.NotAfter is { } notAfter)
        {
            var rows = await this.corpus.ReadRowsAsync(union, cancellationToken).ConfigureAwait(false);
            var early = rows.Where(r => r.Timestamp < notAfter).Select(r => r.Id).ToHashSet();
            union = union.Where(early.Contains).ToList();
        }

        return union.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
    }

    public async Task<IReadOnlyDictionary<string, MaterialItem>> ReadAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var numbers = ids.Select(id => long.TryParse(id, CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n > 0).ToList();
        var rows = await this.corpus.ReadRowsAsync(numbers, cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(
            r => r.Id.ToString(CultureInfo.InvariantCulture),
            r => new MaterialItem(r.Id.ToString(CultureInfo.InvariantCulture), r.Content, r.ChannelId, r.Timestamp, r.Role),
            StringComparer.Ordinal);
    }

    public bool ParaphraseOk(string text, IReadOnlyList<string> goldTexts)
    {
        this.segments ??= this.corpus.SegmentCount();
        return ParaphraseGuard.Check("draft", text, goldTexts, this.corpus.DocumentFrequency, this.segments.Value).Passed;
    }

    public void WriteDataset(IReadOnlyList<LabelledQuery> queries, string queryFamily, string judgeFamily)
    {
        ArgumentNullException.ThrowIfNull(queries);
        this.workspace.Protection.WriteLines(this.workspace.DatasetPath, queries.Select(q => EvalDataset.ToLine(DatasetFinaliser.ToHistory(q, queryFamily, judgeFamily))));
    }
}
