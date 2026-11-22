using System.Diagnostics;
using System.Globalization;
using Cortex.Contained.Agent.Host.History.Evaluation.Smoke;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Memory.Evaluation;

/// <summary>
/// The reranker evaluation of <c>memory-eval</c> (§4.3, Gate R for memory): <c>rerank-curve</c> over the
/// TopN × MaxDocChars grid under one condition and <c>gate-r</c> against the same hybrid configuration
/// without reranking on the test split. Runs on the self-deleting store copy; outputs hold ids and numbers only.
/// </summary>
internal static class MemoryRerankEval
{
    /// <summary>The hybrid configuration the curve reranks when neither <c>--base</c> nor a holdout choice is given.</summary>
    public const string DefaultBase = "hybrid-rrf60";

    private const int ColdReadyTimeoutSeconds = 180;

    /// <summary><c>rerank-curve [--split dev] [--condition normal|embedder-busy|cold] [--base CONFIG] [--reranker URL] [--timeout-ms N]</c>.</summary>
    public static async Task<int> CurveAsync(
        MemoryEvalWorkspace workspace,
        MemoryMcpOptions live,
        Func<MemoryMcpOptions, IEmbeddingService> embedderFactory,
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
        var baseConfig = plan.Configs.FirstOrDefault(c => c.Name == baseName && c.Mode == MemorySearchMode.Hybrid && c.RerankBudget() is null)
            ?? throw new InvalidOperationException($"ablation.json has no hybrid configuration '{baseName}' without reranking to use as the curve's base (--base).");
        var queries = ReadQueries(workspace, split);
        var (reranker, stage) = CreateStage(rerankerFactory, Option(args, "--reranker"), RerankEvalSetup.IntOption(Option(args, "--timeout-ms"), RerankEvalSetup.CurveTimeoutMs));

        var runDir = NewRunDirectory(workspace, $"rerank-curve-{condition}-{split}");
        double? readyMs = null;
        double? firstCallMs = null;
        IReadOnlyList<RerankCurveRow> rows;
        AblationReport report;
        int? loadCompleted = null;
        int? loadFailed = null;
        await using (var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDir, live, embedderFactory(live), cancellationToken, stage).ConfigureAwait(false))
        {
            if (condition == RerankConditions.Cold)
            {
                readyMs = await WaitReadyAsync(reranker, cancellationToken).ConfigureAwait(false);
            }

            await using var load = condition == RerankConditions.EmbedderBusy ? StartEmbedderLoad(embedderFactory, live) : null;
            (rows, report) = await RerankEvaluation.CurveAsync(
                queries,
                RerankGrid.Cells(),
                condition,
                async (name, budget, query, ct) =>
                {
                    var config = baseConfig with { Name = name, RerankTopN = budget?.TopN ?? 0, RerankMaxDocChars = budget?.MaxDocChars ?? baseConfig.RerankMaxDocChars };
                    var run = await corpus.SearchAsync(config, query, ct).ConfigureAwait(false);
                    firstCallMs ??= run.Rerank is { Status: not RerankStatus.Skipped } called ? called.LatencyMs : null;
                    return new RerankRun(MemoryAblationRunner.Score(name, query, run), run.Rerank);
                },
                cancellationToken).ConfigureAwait(false);
            loadCompleted = load?.Completed;
            loadFailed = load?.Failed;
        }

        var coldNote = condition == RerankConditions.Cold
            ? string.Create(CultureInfo.InvariantCulture, $"Cold model: the reranker answered /health after {readyMs:F0} ms; the first rerank call took {firstCallMs ?? 0:F0} ms (later calls are warm).")
            : null;
        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "curve.json"), new { condition, split, Base = baseName, rows, readyMs, firstCallMs, loadCompleted, loadFailed });
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "curve.md"),
            RerankEvaluation.Markdown(rows, string.Create(CultureInfo.InvariantCulture, $"memory rerank curve: {condition} on {split} ({queries.Count} queries, base {baseName})"), coldNote),
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Curve: {Path.Combine(runDir, "curve.md")}").ConfigureAwait(false);
        return 0;
    }

    /// <summary><c>gate-r --config CONFIG [--reranker URL] [--timeout-ms N]</c> on the test split (registration required).</summary>
    public static async Task<int> GateAsync(
        MemoryEvalWorkspace workspace,
        MemoryMcpOptions live,
        Func<MemoryMcpOptions, IEmbeddingService> embedderFactory,
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
        AblationReport report;
        var busy = new List<double>();
        await using (var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDir, live, embedderFactory(live), cancellationToken, stage).ConfigureAwait(false))
        {
            report = await MemoryAblationRunner.RunAsync([unreranked, candidate], queries, corpus.SearchAsync, unreranked.Name, cancellationToken).ConfigureAwait(false);
            await using (StartEmbedderLoad(embedderFactory, live))
            {
                foreach (var query in queries)
                {
                    busy.Add((await corpus.SearchAsync(candidate, query, cancellationToken).ConfigureAwait(false)).LatencyMs);
                }
            }
        }

        var verdict = RerankEvaluation.Gate(
            report.Comparisons.Single(c => c.Config == name && c.Slice == "all"),
            report.Summaries.Single(s => s.Config == name).P95LatencyMs,
            busy.Count == 0 ? 0 : EvalStatistics.Percentile(busy, 95));
        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "verdict.json"), verdict);
        await File.WriteAllTextAsync(Path.Combine(runDir, "report.md"), AblationReporting.Markdown(report, $"Gate R (memory): {name} on test ({queries.Count} queries)"), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(RerankEvaluation.Describe(verdict)).ConfigureAwait(false);
        return 0;
    }

    private static List<MemoryEvalQuery> ReadQueries(MemoryEvalWorkspace workspace, string split)
    {
        var all = MemoryEvalDataset.Read(workspace.DatasetPath);
        if (MemoryEvalDataset.Validate(all) is { Count: > 0 } errors)
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

    /// <summary>Keeps the embedder busy with batches of synthetic documents (no memory text).</summary>
    private static BackgroundLoad StartEmbedderLoad(Func<MemoryMcpOptions, IEmbeddingService> embedderFactory, MemoryMcpOptions live)
    {
        var embedder = embedderFactory(live);
        IReadOnlyList<string> batch = [.. Enumerable.Repeat(EmbedderCheck.Synthetic(3000), 16)];
        return BackgroundLoad.Start(ct => embedder.EmbedBatchStrictAsync(batch, ct));
    }

    private static string NewRunDirectory(MemoryEvalWorkspace workspace, string kind) =>
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
}
