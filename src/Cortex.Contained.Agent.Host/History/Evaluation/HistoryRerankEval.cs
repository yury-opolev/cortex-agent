using System.Diagnostics;
using System.Globalization;
using Cortex.Contained.Agent.Host.History.Evaluation.Smoke;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>
/// The reranker evaluation of <c>history-eval</c> (§4.3, Gate R): <c>rerank-curve</c> measures quality
/// and latency over the TopN × MaxDocChars grid under one condition (normal, embedder busy, cold model);
/// <c>gate-r</c> runs the pre-registered reranked configuration against the same configuration without
/// reranking on the test split. Outputs hold ids and numbers only; the decision is the owner's.
/// </summary>
internal static class HistoryRerankEval
{
    /// <summary>The hybrid configuration the curve reranks when neither <c>--base</c> nor a holdout choice is given.</summary>
    public const string DefaultBase = "hybrid-rrf60";

    private const int ColdReadyTimeoutSeconds = 180;

    /// <summary><c>rerank-curve [--split dev] [--condition normal|embedder-busy|cold] [--base CONFIG] [--reranker URL] [--timeout-ms N]</c>.</summary>
    public static async Task<int> CurveAsync(
        EvalWorkspace workspace,
        string source,
        HistoryOptions baseline,
        Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
        Func<RerankerOptions, IReranker> rerankerFactory,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var condition = Option(args, "--condition") ?? RerankConditions.Normal;
        if (!RerankConditions.IsValid(condition))
        {
            throw new InvalidOperationException($"--condition must be {RerankConditions.Normal}, {RerankConditions.EmbedderBusy} or {RerankConditions.Cold}.");
        }

        var split = Option(args, "--split") ?? EvalSplits.Dev;
        var plan = workspace.ReadPlan();
        var baseName = Option(args, "--base") ?? plan.Holdout?.Config ?? DefaultBase;
        var baseConfig = plan.Configs.FirstOrDefault(c => c.Name == baseName && c.Mode != HistorySearchMode.Keyword && c.RerankBudget() is null)
            ?? throw new InvalidOperationException($"ablation.json has no hybrid configuration '{baseName}' without reranking to use as the curve's base (--base).");
        var queries = ReadQueries(workspace, split);
        var (reranker, stage) = CreateStage(rerankerFactory, Option(args, "--reranker"), RerankEvalSetup.IntOption(Option(args, "--timeout-ms"), RerankEvalSetup.CurveTimeoutMs));

        var runDir = NewRunDirectory(workspace, $"rerank-curve-{condition}-{split}");
        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, [baseConfig], embedderFactory, cancellationToken, stage).ConfigureAwait(false);
        string? coldNote = null;
        double? readyMs = null;
        if (condition == RerankConditions.Cold)
        {
            readyMs = await WaitReadyAsync(reranker, cancellationToken).ConfigureAwait(false);
        }

        double? firstCallMs = null;
        await using var load = condition == RerankConditions.EmbedderBusy ? StartEmbedderLoad(embedderFactory, baseline) : null;
        var (rows, report) = await RerankEvaluation.CurveAsync(
            queries,
            RerankGrid.Cells(),
            condition,
            async (name, budget, query, ct) =>
            {
                var config = baseConfig with { Name = name, RerankTopN = budget?.TopN ?? 0, RerankMaxDocChars = budget?.MaxDocChars ?? baseConfig.RerankMaxDocChars };
                var run = await corpus.SearchAsync(config, query, ct).ConfigureAwait(false);
                firstCallMs ??= run.Rerank is { Status: not RerankStatus.Skipped } called ? called.LatencyMs : null;
                return new RerankRun(AblationRunner.Score(name, query, run), run.Rerank);
            },
            cancellationToken).ConfigureAwait(false);

        if (condition == RerankConditions.Cold)
        {
            coldNote = string.Create(CultureInfo.InvariantCulture,
                $"Cold model: the reranker answered /health after {readyMs:F0} ms; the first rerank call took {firstCallMs ?? 0:F0} ms (later calls are warm).");
        }

        EvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
        EvalWorkspace.WriteJson(Path.Combine(runDir, "curve.json"), new { condition, split, Base = baseName, rows, readyMs, firstCallMs, loadCompleted = load?.Completed, loadFailed = load?.Failed });
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "curve.md"),
            RerankEvaluation.Markdown(rows, string.Create(CultureInfo.InvariantCulture, $"history rerank curve: {condition} on {split} ({queries.Count} queries, base {baseName})"), coldNote),
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Curve: {Path.Combine(runDir, "curve.md")}").ConfigureAwait(false);
        return 0;
    }

    /// <summary><c>gate-r --config CONFIG [--reranker URL] [--timeout-ms N]</c> on the test split (registration required).</summary>
    public static async Task<int> GateAsync(
        EvalWorkspace workspace,
        string source,
        HistoryOptions baseline,
        Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
        Func<RerankerOptions, IReranker> rerankerFactory,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        workspace.VerifyRegistration();
        var plan = workspace.ReadPlan();
        var name = Option(args, "--config") ?? throw new InvalidOperationException("gate-r needs --config: the registered reranked configuration.");
        var candidate = plan.Configs.FirstOrDefault(c => c.Name == name && c.RerankBudget() is not null)
            ?? throw new InvalidOperationException($"ablation.json has no reranked configuration '{name}' (rerankTopN > 0).");
        var unreranked = candidate with { Name = name + "-no-rerank", RerankTopN = 0 };
        var queries = ReadQueries(workspace, EvalSplits.Test);
        var (_, stage) = CreateStage(rerankerFactory, Option(args, "--reranker"), RerankEvalSetup.IntOption(Option(args, "--timeout-ms"), new RerankerOptions().TimeoutMs));

        var runDir = NewRunDirectory(workspace, "gate-r");
        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, [candidate], embedderFactory, cancellationToken, stage).ConfigureAwait(false);
        var report = await AblationRunner.RunAsync([unreranked, candidate], queries, corpus.SearchAsync, unreranked.Name, cancellationToken).ConfigureAwait(false);
        var busy = new List<double>();
        await using (StartEmbedderLoad(embedderFactory, baseline))
        {
            foreach (var query in queries)
            {
                busy.Add((await corpus.SearchAsync(candidate, query, cancellationToken).ConfigureAwait(false)).LatencyMs);
            }
        }

        var verdict = RerankEvaluation.Gate(
            report.Comparisons.Single(c => c.Config == name && c.Slice == "all"),
            report.Summaries.Single(s => s.Config == name).P95LatencyMs,
            busy.Count == 0 ? 0 : EvalStatistics.Percentile(busy, 95));
        EvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
        EvalWorkspace.WriteJson(Path.Combine(runDir, "verdict.json"), verdict);
        await File.WriteAllTextAsync(Path.Combine(runDir, "report.md"), AblationRunner.Markdown(report, $"Gate R: {name} on test ({queries.Count} queries)"), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(RerankEvaluation.Describe(verdict)).ConfigureAwait(false);
        return 0;
    }

    private static List<EvalQuery> ReadQueries(EvalWorkspace workspace, string split)
    {
        var all = EvalDataset.Read(workspace.DatasetPath);
        if (EvalDataset.Validate(all) is { Count: > 0 } errors)
        {
            throw new InvalidOperationException($"dataset.jsonl has {errors.Count} problems; run 'validate'.");
        }

        return split == "all" ? [.. all] : [.. all.Where(q => EvalSplits.Assign(q.Id) == split)];
    }

    private static (IReranker Reranker, RerankStage Stage) CreateStage(Func<RerankerOptions, IReranker> factory, string? endpoint, int timeoutMs)
    {
        var options = RerankEvalSetup.Options(endpoint, timeoutMs);
        var reranker = factory(options);
        if (reranker.UnavailableReason is { } reason)
        {
            throw new InvalidOperationException("the reranker cannot be used: " + reason + " (pass the key as Reranker__ApiKey; see docs/reranker.md).");
        }

        return (reranker, RerankEvalSetup.Stage(reranker, options));
    }

    private static async Task<double> WaitReadyAsync(IReranker reranker, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        if (reranker is not LlamaServerReranker live)
        {
            return 0;
        }

        while (watch.Elapsed < TimeSpan.FromSeconds(ColdReadyTimeoutSeconds))
        {
            if (await live.ProbeHealthAsync(cancellationToken).ConfigureAwait(false))
            {
                return watch.Elapsed.TotalMilliseconds;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"the reranker did not become ready within {ColdReadyTimeoutSeconds} s.");
    }

    /// <summary>Keeps the embedder busy with batches of maximum-size synthetic chunks (no history text).</summary>
    private static BackgroundLoad StartEmbedderLoad(Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory, HistoryOptions baseline)
    {
        var embedder = embedderFactory(new FixedOptions(baseline));
        IReadOnlyList<string> batch = [.. Enumerable.Repeat(EmbedderCheck.Synthetic(baseline.Dense.MaxChunkChars), baseline.Dense.EmbedBatchSize)];
        return BackgroundLoad.Start(ct => embedder.EmbedDocumentsAsync(batch, ct));
    }

    private static string NewRunDirectory(EvalWorkspace workspace, string kind) =>
        Path.Combine(workspace.RunsDirectory, string.Create(CultureInfo.InvariantCulture, $"{kind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}"));

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private sealed class FixedOptions(HistoryOptions value) : IOptionsMonitor<HistoryOptions>
    {
        public HistoryOptions CurrentValue => value;

        public HistoryOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<HistoryOptions, string?> listener) => null;
    }
}
