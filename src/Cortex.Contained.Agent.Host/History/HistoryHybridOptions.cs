namespace Cortex.Contained.Agent.Host.History;

/// <summary>Keyword index variant (§4.2 keyword quality ablation).</summary>
public enum HistoryKeywordVariant
{
    /// <summary>Folded <c>unicode61</c> tokens only (increment 1 behaviour).</summary>
    Plain,

    /// <summary>Plain tokens plus a Snowball-stemmed shadow column (ru/da/en).</summary>
    Stemmed,

    /// <summary>A trigram table instead of word tokens (substring matching).</summary>
    Trigram,
}

/// <summary>How the keyword and dense ranked lists are fused.</summary>
public enum HistoryFusionMethod
{
    /// <summary>Reciprocal rank fusion with one <c>k</c> and equal weights.</summary>
    Rrf,

    /// <summary>Reciprocal rank fusion with per-list weights.</summary>
    WeightedRrf,

    /// <summary>Convex combination of min-max normalised scores.</summary>
    Convex,
}

/// <summary>
/// Dense retrieval settings, bound from <c>History:Dense</c>. Chunking values are part of the
/// index fingerprint (changing them rebuilds the index); the rest apply at query or job time.
/// </summary>
public sealed class HistoryDenseOptions
{
    /// <summary>Chunks segments and embeds them; enables <c>mode=hybrid</c>. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Target words per transcript chunk (same window as curated memory chunks).</summary>
    public int ChunkWords { get; set; } = 512;

    /// <summary>Words repeated from the end of one chunk at the start of the next.</summary>
    public int ChunkOverlapWords { get; set; } = 64;

    /// <summary>Hard character cap per chunk, so one long token can never overflow <c>num_ctx</c>.</summary>
    public int MaxChunkChars { get; set; } = 6000;

    /// <summary>Qwen3 query instruction (task description); documents are embedded without one.</summary>
    public string QueryInstruction { get; set; } =
        "Given a question about past conversations, retrieve the conversation passages that answer it";

    /// <summary>Sends <see cref="QueryInstruction"/> with queries (ablated with and without).</summary>
    public bool UseQueryInstruction { get; set; } = true;

    /// <summary>Comma-separated host names history text may be sent to without an API key (the in-compose sidecar).</summary>
    public string AllowedEndpointHosts { get; set; } = "embeddings";

    /// <summary>The open (last) segment of a channel is embedded once this many messages arrived since it was queued…</summary>
    public int OpenSegmentDebounceMessages { get; set; } = 10;

    /// <summary>…or once its job has waited this many seconds.</summary>
    public int OpenSegmentDebounceSeconds { get; set; } = 120;

    /// <summary>Chunks per embedding request.</summary>
    public int EmbedBatchSize { get; set; } = 16;

    /// <summary>Budget for embedding one query before falling back to keyword-only.</summary>
    public int QueryTimeoutMs { get; set; } = 3000;

    /// <summary>Nearest chunks fetched per query before collapsing to segments.</summary>
    public int Candidates { get; set; } = 100;

    /// <summary>Above this many embedded chunks the scan uses the int8 copy plus a full-precision rescore.</summary>
    public int Int8Threshold { get; set; } = 100_000;

    /// <summary>Int8 candidates kept per requested result before the full-precision rescore.</summary>
    public int Int8Oversample { get; set; } = 8;

    /// <summary>Parsed <see cref="AllowedEndpointHosts"/>.</summary>
    public IReadOnlyList<string> AllowedHosts() =>
        this.AllowedEndpointHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Returns validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (this.ChunkWords < 32)
        {
            errors.Add("History:Dense:ChunkWords must be at least 32.");
        }

        if (this.ChunkOverlapWords < 0 || this.ChunkOverlapWords >= this.ChunkWords / 2)
        {
            errors.Add("History:Dense:ChunkOverlapWords must be in [0, ChunkWords/2).");
        }

        if (this.MaxChunkChars < 200)
        {
            errors.Add("History:Dense:MaxChunkChars must be at least 200.");
        }

        if (this.EmbedBatchSize < 1 || this.Candidates < 1 || this.Int8Oversample < 1 || this.Int8Threshold < 1)
        {
            errors.Add("History:Dense:EmbedBatchSize, Candidates, Int8Oversample and Int8Threshold must be positive.");
        }

        if (this.OpenSegmentDebounceMessages < 1 || this.OpenSegmentDebounceSeconds < 0 || this.QueryTimeoutMs < 100)
        {
            errors.Add("History:Dense debounce and timeout values are out of range.");
        }

        return errors;
    }
}

/// <summary>Hybrid ranking settings, bound from <c>History:Ranking</c>. All apply at query time.</summary>
public sealed class HistoryRankingOptions
{
    /// <summary>Fusion method; RRF unless the dev set shows otherwise.</summary>
    public HistoryFusionMethod Fusion { get; set; } = HistoryFusionMethod.Rrf;

    /// <summary>RRF constant <c>k</c> (ablated over 5, 10, 20, 60).</summary>
    public int RrfK { get; set; } = 60;

    /// <summary>Weight of the keyword list (weighted RRF and convex).</summary>
    public double KeywordWeight { get; set; } = 1.0;

    /// <summary>Weight of the dense list (weighted RRF and convex).</summary>
    public double DenseWeight { get; set; } = 1.0;

    /// <summary>Keyword-list weight multiplier for quoted phrases and identifier-like tokens (BM25 priority).</summary>
    public double ExactIntentKeywordBoost { get; set; } = 2.0;

    /// <summary>MMR trade-off between relevance (1) and novelty (0) among same-author worker segments.</summary>
    public double MmrLambda { get; set; } = 0.7;

    /// <summary>Same-author worker segments at least this similar are collapsed into the better one.</summary>
    public double DuplicateSimilarity { get; set; } = 0.95;

    /// <summary>Applies MMR / duplicate collapse to worker segments (ablated).</summary>
    public bool CollapseWorkerDuplicates { get; set; } = true;

    /// <summary>Weight of the optional recency prior; 0 disables it (ablated).</summary>
    public double RecencyWeight { get; set; }

    /// <summary>Half-life of the recency prior in days.</summary>
    public double RecencyHalfLifeDays { get; set; } = 30;

    /// <summary>The fusion parameters of the shared engine (<see cref="RankFusion"/>).</summary>
    public FusionSettings ToFusionSettings() => new()
    {
        Method = this.Fusion switch
        {
            HistoryFusionMethod.WeightedRrf => FusionMethod.WeightedRrf,
            HistoryFusionMethod.Convex => FusionMethod.Convex,
            _ => FusionMethod.Rrf,
        },
        RrfK = this.RrfK,
        KeywordWeight = this.KeywordWeight,
        DenseWeight = this.DenseWeight,
        ExactIntentKeywordBoost = this.ExactIntentKeywordBoost,
    };

    /// <summary>Returns validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (this.RrfK < 1)
        {
            errors.Add("History:Ranking:RrfK must be at least 1.");
        }

        if (this.KeywordWeight < 0 || this.DenseWeight < 0 || this.KeywordWeight + this.DenseWeight <= 0 || this.ExactIntentKeywordBoost < 1)
        {
            errors.Add("History:Ranking weights must be non-negative, not both zero, and ExactIntentKeywordBoost at least 1.");
        }

        if (this.MmrLambda is < 0 or > 1 || this.DuplicateSimilarity is <= 0 or > 1)
        {
            errors.Add("History:Ranking:MmrLambda must be in [0, 1] and DuplicateSimilarity in (0, 1].");
        }

        if (this.RecencyWeight < 0 || this.RecencyHalfLifeDays <= 0)
        {
            errors.Add("History:Ranking:RecencyWeight must be non-negative and RecencyHalfLifeDays positive.");
        }

        return errors;
    }
}
