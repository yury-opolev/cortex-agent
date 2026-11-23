using System.Globalization;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// Automated labelling of the evaluation datasets (section <c>Evaluation:Labelling</c>): which models write the
/// queries and judge relevance (different families, §4.2), how many queries, the owner's κ sample and the hard
/// caps on LLM calls. Both models go through the configured provider (D5); nothing else sees the text.
/// </summary>
public sealed class LabellingOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Evaluation:Labelling";

    /// <summary>Writes the queries and gold filters from sampled snippets.</summary>
    public string QueryModel { get; set; } = "gpt-6.1-sol";

    /// <summary>Grades pooled candidates 0–2; must be another model family than <see cref="QueryModel"/>.</summary>
    public string JudgeModel { get; set; } = "claude-opus-5.5";

    /// <summary>History queries to draft (some are dropped after judging; §4.2 plans ~150 dev / 350 test).</summary>
    public int HistoryQueries { get; set; } = 520;

    /// <summary>Memory queries to draft (Gate H for memory needs at least 250 after judging).</summary>
    public int MemoryQueries { get; set; } = 270;

    /// <summary>Largest share of real recall requests (co-primary stratum) among the drafted queries.</summary>
    public double RealShare { get; set; } = 0.3;

    /// <summary>Items the owner grades by hand for the LLM-vs-human κ (split between the tools by query count).</summary>
    public int KappaSample { get; set; } = 75;

    /// <summary>Top-k of every pooled configuration that the judge grades (judged@10).</summary>
    public int PoolDepth { get; set; } = 10;

    /// <summary>Most candidates graded per query.</summary>
    public int MaxCandidatesPerQuery { get; set; } = 60;

    /// <summary>Candidates graded per judge call.</summary>
    public int JudgeBatchSize { get; set; } = 10;

    /// <summary>Drafts per seed before it is given up (paraphrase guard, copy check, filters).</summary>
    public int MaxDraftAttempts { get; set; } = 3;

    /// <summary>Hard cap on LLM calls of one label job (all models); the job stops, resumable, when reached.</summary>
    public int MaxCalls { get; set; } = 5000;

    /// <summary>Minimum time between two LLM calls (provider rate limits).</summary>
    public int MinCallIntervalMs { get; set; } = 500;

    /// <summary>Attempts per call (failures back off exponentially; unparsable answers are asked again).</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Characters of one snippet or candidate shown to a model.</summary>
    public int MaxSnippetChars { get; set; } = 1500;

    /// <summary>Problems that make a labelling run impossible (no text).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(this.QueryModel) || string.IsNullOrWhiteSpace(this.JudgeModel))
        {
            errors.Add("Evaluation:Labelling:QueryModel and JudgeModel are required.");
        }
        else if (string.Equals(ModelFamily.Of(this.QueryModel), ModelFamily.Of(this.JudgeModel), StringComparison.Ordinal))
        {
            errors.Add($"QueryModel and JudgeModel are both of the {ModelFamily.Of(this.QueryModel)} family; §4.2 needs different families.");
        }

        if (this.HistoryQueries < 1 || this.MemoryQueries < 1 || this.KappaSample < 3 || this.PoolDepth < 1 || this.JudgeBatchSize < 1
            || this.MaxCandidatesPerQuery < this.PoolDepth || this.MaxDraftAttempts < 1 || this.MaxCalls < 1 || this.MaxAttempts < 1)
        {
            errors.Add("Evaluation:Labelling counts must be positive (KappaSample ≥ 3, MaxCandidatesPerQuery ≥ PoolDepth).");
        }

        if (this.RealShare is < 0 or > 1)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"RealShare {this.RealShare} is not between 0 and 1."));
        }

        return errors;
    }
}

/// <summary>The model family of a model id (the generator/judge separation of §4.2 compares families, not ids).</summary>
public static class ModelFamily
{
    private static readonly (string Prefix, string Family)[] Known =
    [
        ("claude", "claude"), ("gpt", "gpt"), ("o1", "gpt"), ("o3", "gpt"), ("o4", "gpt"), ("chatgpt", "gpt"),
        ("gemini", "gemini"), ("grok", "grok"), ("llama", "llama"), ("mistral", "mistral"), ("qwen", "qwen"),
    ];

    /// <summary><c>claude</c> for <c>claude-opus-5.5</c>, <c>gpt</c> for <c>gpt-6.1-sol</c>; otherwise the id up to its first dash.</summary>
    public static string Of(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var id = model.Trim().ToLowerInvariant();
        var slash = id.LastIndexOf('/');
        if (slash >= 0)
        {
            id = id[(slash + 1)..];
        }

        foreach (var (prefix, family) in Known)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                return family;
            }
        }

        var dash = id.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? id[..dash] : id;
    }
}
