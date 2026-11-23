using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// One sampled seed: the material a query is written from (ids only; the text is read again from the corpus copy).
/// <see cref="Category"/> is the planned category, or <c>real</c> for a real recall request whose category the
/// writer classifies.
/// </summary>
public sealed record LabelSeed
{
    public required string SeedId { get; init; }

    public required string Category { get; init; }

    /// <summary><c>synthetic</c> or <c>real</c>.</summary>
    public string Stratum { get; init; } = "synthetic";

    /// <summary>Messages or memories shown to the writer.</summary>
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>False for abstention seeds: the source is only a topic hint, not an answer.</summary>
    public bool SourceIsGold { get; init; } = true;

    /// <summary>Real requests: the user message that asked.</summary>
    public string? RequestMessageId { get; init; }

    /// <summary>Real requests: when the owner asked (the request message's time); it and everything after it is never pooled.</summary>
    public DateTimeOffset? RequestTime { get; init; }

    /// <summary>Real requests: the search arguments the agent wrote (JSON), i.e. the model-written query.</summary>
    public string? ModelArgs { get; init; }
}

/// <summary>A query drafted from a seed, before judging (oracle text, filters and the main model's version).</summary>
public sealed record DraftedQuery
{
    public required string Id { get; init; }

    public required string SeedId { get; init; }

    public required string Text { get; init; }

    public required string Category { get; init; }

    public string Stratum { get; init; } = "synthetic";

    /// <summary>Sources that answer the query (always pooled).</summary>
    public IReadOnlyList<string> GoldSourceIds { get; init; } = [];

    /// <summary>History: oracle filters.</summary>
    public EvalFilters? GoldFilters { get; init; }

    /// <summary>Memory: oracle tag filter.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    public string? ModelText { get; init; }

    public EvalFilters? ModelFilters { get; init; }

    public IReadOnlyList<string>? ModelTags { get; init; }

    /// <summary>Real requests: candidates at or after this time are not pooled (they post-date the question).</summary>
    public DateTimeOffset? NotAfter { get; init; }

    /// <summary>Real requests: the owner's request message, never pooled or judged.</summary>
    public string? RequestMessageId { get; init; }
}

/// <summary>The resumable progress of one label job (sealed: it holds query text).</summary>
public sealed class LabellingState
{
    public string Tool { get; set; } = string.Empty;

    /// <summary>Models the progress was made with (a resume with other models would mix two labellers in one dataset).</summary>
    public string QueryModel { get; set; } = string.Empty;

    public string JudgeModel { get; set; } = string.Empty;

    public string MainModel { get; set; } = string.Empty;
    public List<LabelSeed> Seeds { get; set; } = [];

    /// <summary>Drafts by seed id.</summary>
    public Dictionary<string, DraftedQuery> Drafts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Seeds the writer could not turn into a usable query.</summary>
    public List<string> FailedSeeds { get; set; } = [];

    /// <summary>Pooled candidate ids by query id.</summary>
    public Dictionary<string, List<string>> Pools { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Judge grades by query id, then candidate id.</summary>
    public Dictionary<string, Dictionary<string, int>> Grades { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Queries the judge could not grade completely (retried by the next runs).</summary>
    public List<string> FailedJudging { get; set; } = [];

    /// <summary>Runs in which the judge gave no valid answer for a query, by query id.</summary>
    public Dictionary<string, int> JudgeFailures { get; set; } = new(StringComparer.Ordinal);

    /// <summary>LLM calls made so far by every run of this job.</summary>
    public int Calls { get; set; }
}

/// <summary>Loads and saves <see cref="LabellingState"/> through <see cref="EvalProtection"/> (atomic, sealed on an encrypted install).</summary>
public sealed class LabellingStateStore
{
    /// <summary>File name in the workspace.</summary>
    public const string FileName = "label-state.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly EvalProtection protection;

    public LabellingStateStore(string directory, EvalProtection protection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.Path = System.IO.Path.Combine(directory, FileName);
        this.protection = protection;
    }

    public string Path { get; }

    /// <summary>The saved state, or null when there is none.</summary>
    public LabellingState? Load() =>
        File.Exists(this.Path) ? JsonSerializer.Deserialize<LabellingState>(this.protection.ReadText(this.Path), Json) : null;

    public void Save(LabellingState state) => this.protection.WriteText(this.Path, JsonSerializer.Serialize(state, Json));

    public void Delete() => File.Delete(this.Path);
}
