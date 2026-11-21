using System.Globalization;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// How many fused candidates are reranked and how much of each one's text the reranker sees.
/// Only the first <see cref="TopN"/> candidates are reordered; the rest keep the fused order.
/// </summary>
/// <param name="TopN">Fused candidates sent to the reranker (1–50).</param>
/// <param name="MaxDocChars">Characters of raw best-matching lines per candidate (200–4000).</param>
public sealed record RerankBudget(int TopN, int MaxDocChars)
{
    /// <summary>Smallest accepted <see cref="TopN"/>.</summary>
    public const int MinTopN = 1;

    /// <summary>Largest accepted <see cref="TopN"/>.</summary>
    public const int MaxTopN = 50;

    /// <summary>Smallest accepted <see cref="MaxDocChars"/>.</summary>
    public const int MinDocChars = 200;

    /// <summary>Largest accepted <see cref="MaxDocChars"/>.</summary>
    public const int MaxDocCharsLimit = 4000;

    /// <summary>Stable text form, used in cursor bindings and reports.</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture, $"top{this.TopN}-chars{this.MaxDocChars}");

    /// <summary>Returns validation errors; <paramref name="prefix"/> names the configuration section.</summary>
    public IReadOnlyList<string> Validate(string prefix)
    {
        var errors = new List<string>();
        if (this.TopN is < MinTopN or > MaxTopN)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{prefix}:TopN must be in [{MinTopN}, {MaxTopN}]."));
        }

        if (this.MaxDocChars is < MinDocChars or > MaxDocCharsLimit)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{prefix}:MaxDocChars must be in [{MinDocChars}, {MaxDocCharsLimit}]."));
        }

        return errors;
    }
}

/// <summary>Per-surface reranking settings (<c>Reranker:History</c>, <c>Reranker:Memory</c>).</summary>
public sealed class RerankSurfaceOptions
{
    /// <summary>Reranks this surface's hybrid results while the Bridge has the reranker enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fused candidates reranked per search.</summary>
    public int TopN { get; set; } = 15;

    /// <summary>Characters of raw best-matching lines per candidate.</summary>
    public int MaxDocChars { get; set; } = 1200;

    /// <summary>The budget these settings describe.</summary>
    public RerankBudget Budget() => new(this.TopN, this.MaxDocChars);
}

/// <summary>
/// Agent-side reranker settings, bound from <c>Reranker</c>. Whether reranking runs at all is the
/// Bridge's decision (<c>Reranker:Enabled</c> in the Bridge configuration, default false): the Bridge
/// starts the sidecar and pushes the switch and the API key (<see cref="RerankerAccess"/>).
/// </summary>
public sealed class RerankerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Reranker";

    /// <summary>Base URL of the llama.cpp server (the in-compose sidecar by default).</summary>
    public string Endpoint { get; set; } = "http://reranker:8080";

    /// <summary>Model alias sent with every request (the sidecar's <c>--alias</c>).</summary>
    public string Model { get; set; } = "qwen3-reranker-0.6b";

    /// <summary>Comma-separated hosts candidate text may be sent to over plain HTTP (the in-compose sidecar).</summary>
    public string AllowedEndpointHosts { get; set; } = "reranker";

    /// <summary>Budget for one rerank call; on timeout the fused order is kept.</summary>
    public int TimeoutMs { get; set; } = 2000;

    /// <summary>Consecutive failures that open the circuit.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>How long an open circuit skips reranking before one trial call.</summary>
    public int BreakSeconds { get; set; } = 60;

    /// <summary><c>history_search</c> (hybrid relevance order).</summary>
    public RerankSurfaceOptions History { get; set; } = new();

    /// <summary><c>memory_search</c> (hybrid mode).</summary>
    public RerankSurfaceOptions Memory { get; set; } = new();

    /// <summary>Parsed <see cref="AllowedEndpointHosts"/>.</summary>
    public IReadOnlyList<string> AllowedHosts() =>
        this.AllowedEndpointHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Returns validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(this.Endpoint, UriKind.Absolute, out _))
        {
            errors.Add("Reranker:Endpoint must be an absolute URL.");
        }

        if (string.IsNullOrWhiteSpace(this.Model))
        {
            errors.Add("Reranker:Model must not be empty.");
        }

        if (this.TimeoutMs is < 100 or > 30_000)
        {
            errors.Add("Reranker:TimeoutMs must be in [100, 30000].");
        }

        if (this.FailureThreshold < 1 || this.BreakSeconds < 1)
        {
            errors.Add("Reranker:FailureThreshold and BreakSeconds must be positive.");
        }

        errors.AddRange(this.History.Budget().Validate("Reranker:History"));
        errors.AddRange(this.Memory.Budget().Validate("Reranker:Memory"));
        return errors;
    }
}
