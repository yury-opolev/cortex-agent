using System.Globalization;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// <see cref="ILabelSource"/> over a private copy of the memory store (<see cref="MemoryEvalCorpus"/>): memories as
/// seeds (<see cref="MemoryQuerySeeds"/>), <c>memory_search</c> calls from the event log as real requests, the memory
/// ablation grid (without reranking) as the pooled systems, and document frequencies over the memories for the
/// paraphrase guard. Memories carry no reliable time, so real requests are not limited to older candidates.
/// </summary>
public sealed class MemoryLabelSource : ILabelSource
{
    private readonly MemoryEvalCorpus corpus;
    private readonly MemoryEvalWorkspace workspace;
    private readonly IReadOnlyList<MemoryAblationConfig> pooled;
    private readonly Func<SqliteConnection?> openMessages;
    private Dictionary<string, MemorySeedSource>? memories;
    private Dictionary<string, long>? documentFrequency;

    /// <param name="corpus">The memory copy.</param>
    /// <param name="workspace">Where the dataset is written (its protection seals it).</param>
    /// <param name="pooled">The configurations whose top-k is pooled (reranked ones are left out).</param>
    /// <param name="openMessages">Opens <c>messages.db</c> read-only for the real requests, or returns null.</param>
    public MemoryLabelSource(MemoryEvalCorpus corpus, MemoryEvalWorkspace workspace, IReadOnlyList<MemoryAblationConfig> pooled, Func<SqliteConnection?> openMessages)
    {
        ArgumentNullException.ThrowIfNull(pooled);
        this.corpus = corpus;
        this.workspace = workspace;
        this.pooled = pooled.Where(c => c.RerankBudget() is null).ToList();
        this.openMessages = openMessages;
    }

    public string Tool => "memory-eval";

    public async Task<IReadOnlyList<LabelSeed>> SampleAsync(int count, double realShare, CancellationToken cancellationToken)
    {
        var all = await this.MemoriesAsync(cancellationToken).ConfigureAwait(false);
        using var connection = this.openMessages();
        IReadOnlyList<LabelSeed> real = connection is null ? [] : RecallRequestMiner.Read(connection, "memory_search");
        return LabelSeeds.Memory(all.Values.OrderBy(m => m.MemoryId, StringComparer.Ordinal).ToList(), real, count, realShare);
    }

    public async Task<SeedMaterial?> MaterialAsync(LabelSeed seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Stratum == "real")
        {
            using var connection = this.openMessages();
            return connection is not null && seed.RequestMessageId is { } id
                ? RecallRequestMiner.Material(connection, long.Parse(id, CultureInfo.InvariantCulture), HistoryLabelSource.ContextTurns)
                : null;
        }

        var items = await this.ReadAsync(seed.SourceIds, cancellationToken).ConfigureAwait(false);
        return items.Count == seed.SourceIds.Count ? new SeedMaterial(seed.SourceIds.Select(id => items[id]).ToList()) : null;
    }

    public async Task<IReadOnlyList<string>> PoolAsync(DraftedQuery query, int depth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var probe = new MemoryEvalQuery
        {
            Id = query.Id,
            Text = query.Text,
            Category = query.Category,
            Tags = query.Category == "tag_filtered" ? query.Tags : null,
            ModelQuery = query.ModelText is null ? null : new MemoryModelQuery { Text = query.ModelText, Tags = query.ModelTags },
        };
        var lists = new List<IReadOnlyList<string>>();
        foreach (var config in this.pooled)
        {
            var run = await this.corpus.SearchAsync(config, probe, cancellationToken).ConfigureAwait(false);
            lists.Add(run.Memories.Take(depth).Select(m => m.MemoryId).ToList());
        }

        return Enumerable.Range(0, depth).SelectMany(rank => lists.Where(l => rank < l.Count).Select(l => l[rank])).Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyDictionary<string, MaterialItem>> ReadAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var all = await this.MemoriesAsync(cancellationToken).ConfigureAwait(false);
        return ids.Where(all.ContainsKey).Distinct(StringComparer.Ordinal).ToDictionary(
            id => id,
            id => new MaterialItem(id, all[id].Content, Title: all[id].Title) { Tags = all[id].Tags },
            StringComparer.Ordinal);
    }

    public bool ParaphraseOk(string text, IReadOnlyList<string> goldTexts)
    {
        var frequency = this.documentFrequency ?? throw new InvalidOperationException("Sample the memories before checking paraphrases.");
        return ParaphraseGuard.Check("draft", text, goldTexts, term => frequency.GetValueOrDefault(term), this.memories!.Count).Passed;
    }

    public void WriteDataset(IReadOnlyList<LabelledQuery> queries, string queryFamily, string judgeFamily)
    {
        ArgumentNullException.ThrowIfNull(queries);
        this.workspace.Protection.WriteLines(this.workspace.DatasetPath, queries.Select(q => MemoryEvalDataset.ToLine(DatasetFinaliser.ToMemory(q, queryFamily, judgeFamily))));
    }

    private async Task<Dictionary<string, MemorySeedSource>> MemoriesAsync(CancellationToken cancellationToken)
    {
        if (this.memories is null)
        {
            this.memories = (await this.corpus.ListMemoriesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(m => m.MemoryId, StringComparer.Ordinal);
            this.documentFrequency = this.memories.Values
                .SelectMany(m => KeywordTokenizer.Tokenize(TextFolder.Fold(m.Title + "\n" + m.Content)).Distinct(StringComparer.Ordinal))
                .GroupBy(t => t, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        }

        return this.memories;
    }
}
