using System.Globalization;
using System.Text;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Reranking.Evaluation;

/// <summary>The §4.3 latency/quality grid: TopN ∈ {10, 15, 30} × MaxDocChars ∈ {600, 1200, 2000}.</summary>
public static class RerankGrid
{
    /// <summary>Candidate counts of the curve.</summary>
    public static IReadOnlyList<int> TopNs { get; } = [10, 15, 30];

    /// <summary>Characters per candidate of the curve.</summary>
    public static IReadOnlyList<int> MaxDocChars { get; } = [600, 1200, 2000];

    /// <summary>Every cell of the grid, smallest first.</summary>
    public static IReadOnlyList<RerankBudget> Cells() =>
        [.. TopNs.SelectMany(n => MaxDocChars.Select(c => new RerankBudget(n, c)))];
}

/// <summary>Measurement conditions of the curve (§4.3).</summary>
public static class RerankConditions
{
    /// <summary>Warm model, idle embedder.</summary>
    public const string Normal = "normal";

    /// <summary>The embeddings sidecar is kept busy with document batches while measuring.</summary>
    public const string EmbedderBusy = "embedder-busy";

    /// <summary>Measured right after the reranker (re)started: readiness time and first-call latency are reported.</summary>
    public const string Cold = "cold";

    /// <summary>True for a known condition.</summary>
    public static bool IsValid(string condition) => condition is Normal or EmbedderBusy or Cold;
}

/// <summary>One query of one configuration: its metrics and the rerank stage's outcome (null when not reranked).</summary>
public sealed record RerankRun(AblationScore Score, RerankOutcome? Outcome);

/// <summary>One cell of the curve under one condition (numbers only).</summary>
/// <param name="Condition">normal, embedder-busy or cold.</param>
/// <param name="Cell">Budget label (<c>topN-charsC</c>).</param>
/// <param name="Queries">Queries scored.</param>
/// <param name="DeltaNdcg">Mean paired nDCG@10 gain over no reranking.</param>
/// <param name="Low">Bootstrap 95% CI lower bound.</param>
/// <param name="High">Bootstrap 95% CI upper bound.</param>
/// <param name="PValue">Paired randomisation p.</param>
/// <param name="P50RerankMs">Median latency of the rerank call.</param>
/// <param name="P95RerankMs">95th percentile latency of the rerank call.</param>
/// <param name="P95EndToEndMs">95th percentile first-page search latency with reranking.</param>
/// <param name="Applied">Queries the reranker reordered.</param>
/// <param name="Failed">Queries whose rerank failed or timed out (fused order kept).</param>
/// <param name="Skipped">Queries not sent (fewer than two candidates, not hybrid).</param>
public sealed record RerankCurveRow(
    string Condition, string Cell, int Queries, double DeltaNdcg, double Low, double High, double? PValue,
    double P50RerankMs, double P95RerankMs, double P95EndToEndMs, int Applied, int Failed, int Skipped);

/// <summary>Gate R (§4 roadmap row 5): a significant paired gain and p95 ≤ 2.5 s, also with the embedder busy.</summary>
public sealed record RerankGateVerdict(
    string Config, string Baseline, int Queries, double Mean, double Low, double High, double? PValue,
    double P95NormalMs, double P95EmbedderBusyMs, bool SignificantGain, bool LatencyOk, bool Passed);

/// <summary>Builds the latency/quality curve and the Gate R verdict from per-query runs.</summary>
public static class RerankEvaluation
{
    /// <summary>Name of the configuration without reranking in curve reports.</summary>
    public const string NoRerank = "no-rerank";

    /// <summary>Gate R latency bound on the first-page p95.</summary>
    public const double MaxP95Ms = 2500;

    /// <summary>Gate R significance level of the paired randomisation test.</summary>
    public const double Alpha = 0.05;

    /// <summary>
    /// Runs every query without reranking and with each budget of <paramref name="cells"/> under one
    /// condition. <paramref name="run"/> gets the configuration name to score under and the budget (null = off).
    /// </summary>
    public static async Task<(IReadOnlyList<RerankCurveRow> Rows, AblationReport Report)> CurveAsync<TQuery>(
        IReadOnlyList<TQuery> queries,
        IReadOnlyList<RerankBudget> cells,
        string condition,
        Func<string, RerankBudget?, TQuery, CancellationToken, Task<RerankRun>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(run);
        var scores = new List<AblationScore>();
        var outcomes = new Dictionary<string, List<RerankRun>>(StringComparer.Ordinal);
        foreach (var (name, budget) in new[] { (NoRerank, (RerankBudget?)null) }.Concat(cells.Select(c => (c.Label, (RerankBudget?)c))))
        {
            var runs = outcomes[name] = [];
            foreach (var query in queries)
            {
                var result = await run(name, budget, query, cancellationToken).ConfigureAwait(false);
                runs.Add(result);
                scores.Add(result.Score);
            }
        }

        var report = AblationReporting.Build(scores, NoRerank);
        var rows = cells.Select(cell =>
        {
            var runs = outcomes[cell.Label];
            var overall = report.Comparisons.SingleOrDefault(c => c.Config == cell.Label && c.Slice == "all");
            var called = runs.Where(r => r.Outcome is { Status: not RerankStatus.Skipped }).Select(r => r.Outcome!.LatencyMs).ToList();
            return new RerankCurveRow(
                condition,
                cell.Label,
                runs.Count,
                overall?.Mean ?? 0,
                overall?.Low ?? 0,
                overall?.High ?? 0,
                overall?.PValue,
                called.Count == 0 ? 0 : EvalStatistics.Percentile(called, 50),
                called.Count == 0 ? 0 : EvalStatistics.Percentile(called, 95),
                runs.Count == 0 ? 0 : EvalStatistics.Percentile(runs.Select(r => r.Score.LatencyMs).ToList(), 95),
                runs.Count(r => r.Outcome?.Status == RerankStatus.Applied),
                runs.Count(r => r.Outcome?.Status == RerankStatus.Failed),
                runs.Count(r => r.Outcome is null || r.Outcome.Status == RerankStatus.Skipped));
        }).ToList();
        return (rows, report);
    }

    /// <summary>Gate R from the paired comparison on the test split and the p95 under both conditions.</summary>
    public static RerankGateVerdict Gate(PairedComparison overall, double p95NormalMs, double p95EmbedderBusyMs)
    {
        ArgumentNullException.ThrowIfNull(overall);
        var significant = overall.Low > 0 && overall.PValue is < Alpha;
        var latencyOk = Math.Max(p95NormalMs, p95EmbedderBusyMs) <= MaxP95Ms;
        return new RerankGateVerdict(
            overall.Config, overall.Baseline, overall.Queries, overall.Mean, overall.Low, overall.High, overall.PValue,
            p95NormalMs, p95EmbedderBusyMs, significant, latencyOk, significant && latencyOk);
    }

    /// <summary>A Markdown table of curve rows (numbers only).</summary>
    public static string Markdown(IReadOnlyList<RerankCurveRow> rows, string title, string? coldNote = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {title}");
        text.AppendLine();
        if (coldNote is not null)
        {
            text.AppendLine(coldNote);
            text.AppendLine();
        }

        text.AppendLine("| condition | cell | n | Δ nDCG@10 | 95% CI | p | rerank p50 ms | rerank p95 ms | first page p95 ms | applied | failed | skipped |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Condition} | {r.Cell} | {r.Queries} | {r.DeltaNdcg:+0.000;-0.000;0.000} | [{r.Low:+0.000;-0.000;0.000}, {r.High:+0.000;-0.000;0.000}] | {r.PValue?.ToString("F4", CultureInfo.InvariantCulture) ?? "–"} | {r.P50RerankMs:F0} | {r.P95RerankMs:F0} | {r.P95EndToEndMs:F0} | {r.Applied} | {r.Failed} | {r.Skipped} |");
        }

        text.AppendLine();
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Gate R needs a significant paired gain (CI above 0, p < {Alpha}) and first-page p95 ≤ {MaxP95Ms:F0} ms, also with the embedder busy; it is the owner's decision."));
        return text.ToString();
    }

    /// <summary>The verdict as one line.</summary>
    public static string Describe(RerankGateVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return string.Create(CultureInfo.InvariantCulture,
            $"Gate R {verdict.Config} vs {verdict.Baseline} ({verdict.Queries} queries): Δ nDCG@10 {verdict.Mean:+0.000;-0.000} [{verdict.Low:+0.000;-0.000}, {verdict.High:+0.000;-0.000}], p={verdict.PValue?.ToString("F4", CultureInfo.InvariantCulture) ?? "–"} → significant gain: {(verdict.SignificantGain ? "yes" : "no")}; first-page p95 {verdict.P95NormalMs:F0} ms (embedder busy {verdict.P95EmbedderBusyMs:F0} ms) ≤ {MaxP95Ms:F0} ms: {(verdict.LatencyOk ? "yes" : "no")}. Gate R {(verdict.Passed ? "PASSES" : "does not pass")}; enabling the reranker is the owner's decision.");
    }
}

/// <summary>Keeps a worker loop running while measuring (the "embedder busy" condition). Failures are counted, never thrown.</summary>
public sealed class BackgroundLoad : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task[] workers;
    private int completed;
    private int failed;

    private BackgroundLoad(Func<CancellationToken, Task> work, int concurrency)
    {
        this.workers = [.. Enumerable.Range(0, concurrency).Select(_ => Task.Run(() => this.LoopAsync(work)))];
    }

    /// <summary>Calls that completed.</summary>
    public int Completed => Volatile.Read(ref this.completed);

    /// <summary>Calls that failed.</summary>
    public int Failed => Volatile.Read(ref this.failed);

    /// <summary>Starts <paramref name="concurrency"/> loops of <paramref name="work"/>.</summary>
    public static BackgroundLoad Start(Func<CancellationToken, Task> work, int concurrency = 2)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        return new BackgroundLoad(work, concurrency);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(this.workers).ConfigureAwait(false);
        this.stop.Dispose();
    }

    private async Task LoopAsync(Func<CancellationToken, Task> work)
    {
        while (!this.stop.IsCancellationRequested)
        {
            try
            {
                await work(this.stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref this.completed);

                // A short breath between calls keeps a fast (fake or cached) worker from spinning a core.
                await Task.Delay(5, this.stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (this.stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref this.failed);
            }
        }
    }
}

/// <summary>
/// The live reranker for the evaluation and smoke CLIs (run with <c>docker exec</c>, so the Bridge-held
/// key is read from <c>Reranker__ApiKey</c>; see <c>docs/reranker.md</c>). The stage uses a generous
/// timeout by default so the curve sees true latency, and a breaker that never opens.
/// </summary>
public static class RerankEvalSetup
{
    /// <summary>Default per-call timeout of curve runs (the true latency is the measurement).</summary>
    public const int CurveTimeoutMs = 30_000;

    // One client for the whole command: the reranker never disposes what the factory returns.
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Reranker options from the environment (<c>Reranker__*</c>) with an optional endpoint override.</summary>
    public static RerankerOptions Options(string? endpoint, int timeoutMs)
    {
        var options = new RerankerOptions
        {
            Endpoint = endpoint ?? Environment.GetEnvironmentVariable("Reranker__Endpoint") ?? "http://reranker:8080",
            Model = Environment.GetEnvironmentVariable("Reranker__Model") ?? "qwen3-reranker-0.6b",
            TimeoutMs = timeoutMs,
            FailureThreshold = 1_000_000,
        };
        if (Environment.GetEnvironmentVariable("Reranker__AllowedEndpointHosts") is { Length: > 0 } hosts)
        {
            options.AllowedEndpointHosts = hosts;
        }

        return options;
    }

    /// <summary>The live llama-server client with the key from <c>Reranker__ApiKey</c>.</summary>
    public static LlamaServerReranker LiveReranker(RerankerOptions options) => LiveReranker(options, SharedClient);

    /// <summary>The llama-server client over <paramref name="client"/> with the key from <c>Reranker__ApiKey</c>.</summary>
    public static LlamaServerReranker LiveReranker(RerankerOptions options, HttpClient client) =>
        new(new SingleClientFactory(client), new FixedOptions(options), () => Environment.GetEnvironmentVariable("Reranker__ApiKey"));

    /// <summary>The live llama-server client with the key the Bridge granted (evaluation jobs inside the agent process).</summary>
    public static LlamaServerReranker LiveReranker(RerankerOptions options, Func<string?> apiKey) =>
        new(new SingleClientFactory(SharedClient), new FixedOptions(options), apiKey);

    /// <summary>A rerank stage over <paramref name="reranker"/> for evaluation runs.</summary>
    public static RerankStage Stage(IReranker reranker, RerankerOptions options)
    {
        var access = new RerankerAccess();
        access.Apply(new RerankerGrant { Enabled = true, ApiKey = "evaluation" });
        return new RerankStage(reranker, new FixedOptions(options), access, NullLogger<RerankStage>.Instance);
    }

    /// <summary>Parses a positive integer option, or returns <paramref name="fallback"/>.</summary>
    public static int IntOption(string? text, int fallback) =>
        text is not null && int.TryParse(text, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FixedOptions(RerankerOptions value) : IOptionsMonitor<RerankerOptions>
    {
        public RerankerOptions CurrentValue => value;

        public RerankerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<RerankerOptions, string?> listener) => null;
    }
}
