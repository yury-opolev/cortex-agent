using System.Globalization;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>
/// Evaluation-protocol CLI for hybrid history search (§4.2), run inside the agent container on a copy
/// of the raw <c>messages.db</c> (D6): <c>dotnet Cortex.Contained.Agent.Host.dll history-eval
/// &lt;init|validate|guard|register|ablate|holdout|kappa|rerank-curve|gate-r&gt; [--dir D] [--source DB] [--tz ZONE] [--split dev|test|all] [--embedder URL]</c>.
/// Outputs hold ids and numbers only and stay under the workspace. The Gate H decision is the owner's.
/// </summary>
public static class HistoryEvalCli
{
    private const string Usage =
        "usage: history-eval <init|validate|guard|register|ablate|holdout|kappa|rerank-curve|gate-r> [--dir /app/state/eval/hybrid] [--source /app/state/messages.db] " +
        "[--tz <IANA zone, default: local>] [--split dev|test|all] [--embedder http://embeddings:11434] [--labels judge-agreement.csv]\n" +
        "rerank-curve [--condition normal|embedder-busy|cold] [--base CONFIG] [--reranker http://reranker:8080] [--timeout-ms 30000]; " +
        "gate-r --config CONFIG [--timeout-ms 2000]; both read the reranker key from Reranker__ApiKey.";

    /// <summary>Minimum LLM-vs-human agreement for LLM relevance labels (§4.2).</summary>
    private const double MinKappa = 0.6;

    private const string AnalysisPlanTemplate = """
        # Hybrid history search — pre-registered analysis plan

        Fill in every item, then run `history-eval register` before the first `holdout` run. Changing this
        file, `dataset.jsonl` or `ablation.json` afterwards makes `holdout` refuse to run.

        ## Data
        - Snapshot: date, message count, share of messages captured live with events (≥ 4 weeks needed for a non-provisional v2 run).
        - Dataset: number of queries per category and stratum (`real` = real recall requests, co-primary).
        - Labels: graded 0–2 per message; judge model family ≠ query model family; LLM-vs-human κ on a sample (must be ≥ 0.6).
        - Paraphrase guard (`history-eval guard`): all paraphrase queries pass (Jaccard ≤ 0.2, no shared rare token).
        - Split: dev/test by `EvalSplits.Assign` (seed 20261121, 30% dev), or 5-fold CV with the grid fixed in `ablation.json`.

        ## Hypotheses and metrics
        - Primary: nDCG@10 of `holdout.config` vs `keyword-plain` on the test split; non-inferiority margin = `holdout.margin`.
        - Co-primary: the same on the `stratum:real` slice.
        - Secondary: recall@10, MRR, words to first relevant line (locator cost), judged@10 (must be ≥ 0.8 or the pool is deepened), enumeration set F1, abstention Brier/ECE, p95 latency.
        - Oracle vs model-written queries and filters: `hybrid-model-queries` and `keyword-model-queries`.
        - Raw-data run and sanitised-copy run: report both and the difference.

        ## Statistics
        - Paired bootstrap 95% CI (10,000 resamples, seed 20261121) and a paired randomisation test.
        - Per-category results as CIs only; no per-category significance claims.
        - Multiple comparisons: dev-split ablations are exploratory; only the single pre-registered holdout comparison is confirmatory.

        ## Decision rule (owner)
        - Gate H passes when the primary comparison is non-inferior and the behaviour gate thresholds hold.
        - A result on a mostly-legacy (pre-event-log) snapshot is provisional; re-run after ≥ 4 weeks of live capture.
        """;

    /// <summary>Entry point with the live Ollama embedder; returns a process exit code.</summary>
    public static Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        return RunAsync(args, output, history => LiveEmbedder(Option(args, "--embedder"), history), cancellationToken);
    }

    /// <summary>Entry point with an injected embedder (tests use a deterministic fake).</summary>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
        CancellationToken cancellationToken,
        HistoryOptions? configured = null,
        Func<RerankerOptions, IReranker>? rerankerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        if (args.Count == 0)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        configured ??= HistoryCliConfiguration.Load();
        rerankerFactory ??= RerankEvalSetup.LiveReranker;
        var workspace = new EvalWorkspace(Option(args, "--dir") ?? configured.EvalArtifactsDirectory);
        var source = Option(args, "--source") ?? "/app/state/messages.db";
        var baseline = new HistoryOptions { Enabled = true, TimeZone = Option(args, "--tz") ?? string.Empty };
        Directory.CreateDirectory(workspace.Directory);
        if (HistoryCliConfiguration.ArtifactsWarning(workspace.Directory, configured.EvalArtifactsDirectory, "History:EvalArtifactsDirectory") is { } warning)
        {
            await output.WriteLineAsync(warning).ConfigureAwait(false);
        }
        try
        {
            switch (args[0])
            {
                case "init":
                    Init(workspace);
                    await output.WriteLineAsync($"Workspace ready: {workspace.Directory}. Label dataset.jsonl, fill in analysis-plan.md, then 'validate'.").ConfigureAwait(false);
                    return 0;
                case "validate":
                    return await ValidateAsync(workspace, output).ConfigureAwait(false);
                case "guard":
                    return await GuardAsync(workspace, source, baseline, embedderFactory, output, cancellationToken).ConfigureAwait(false);
                case "register":
                    workspace.Register();
                    await output.WriteLineAsync($"Registered: {workspace.PreregistrationPath}").ConfigureAwait(false);
                    return 0;
                case "ablate":
                    var split = Option(args, "--split") ?? EvalSplits.Dev;
                    var plan = workspace.ReadPlan();
                    var (runDir, _) = await RunAsync(workspace, source, baseline, plan.Configs, plan.Baseline, split, "ablate", embedderFactory, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync($"Report: {Path.Combine(runDir, "report.md")}").ConfigureAwait(false);
                    return 0;
                case "holdout":
                    return await HoldoutAsync(workspace, source, baseline, embedderFactory, output, cancellationToken).ConfigureAwait(false);
                case "kappa":
                    return await KappaAsync(Option(args, "--labels") ?? Path.Combine(workspace.Directory, "judge-agreement.csv"), output).ConfigureAwait(false);
                case "rerank-curve":
                    return await HistoryRerankEval.CurveAsync(workspace, source, baseline, embedderFactory, rerankerFactory, args, output, cancellationToken).ConfigureAwait(false);
                case "gate-r":
                    return await HistoryRerankEval.GateAsync(workspace, source, baseline, embedderFactory, rerankerFactory, args, output, cancellationToken).ConfigureAwait(false);
                default:
                    await output.WriteLineAsync(Usage).ConfigureAwait(false);
                    return 2;
            }
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
            return 2;
        }
    }

    private static void Init(EvalWorkspace workspace)
    {
        File.WriteAllLines(Path.Combine(workspace.Directory, "dataset.example.jsonl"),
        [
            """{"id":"e001","text":"when does the night train to TownB leave","category":"lookup","stratum":"real","judgments":[{"messageId":123,"grade":2},{"messageId":124,"grade":0}],"goldFilters":{"channel":"discord-dm"},"modelQuery":{"text":"night train TownB"},"queryModelFamily":"family-a","judgeModelFamily":"family-b"}""",
            """{"id":"e002","text":"did PersonA ever mention a boat trip","category":"abstention","expectAbstain":true,"judgments":[{"messageId":200,"grade":0}]}""",
        ]);
        if (!File.Exists(workspace.AnalysisPlanPath))
        {
            File.WriteAllText(workspace.AnalysisPlanPath, AnalysisPlanTemplate);
        }

        if (!File.Exists(workspace.AblationPlanPath))
        {
            workspace.WritePlan(new AblationPlan());
        }
    }

    private static async Task<int> ValidateAsync(EvalWorkspace workspace, TextWriter output)
    {
        var queries = EvalDataset.Read(workspace.DatasetPath);
        var errors = EvalDataset.Validate(queries);
        foreach (var error in errors)
        {
            await output.WriteLineAsync("invalid " + error).ConfigureAwait(false);
        }

        var dev = queries.Count(q => EvalSplits.Assign(q.Id) == EvalSplits.Dev);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{queries.Count} queries: {dev} dev / {queries.Count - dev} test; {queries.Count(q => q.Stratum == "real")} real requests; {errors.Count} problems")).ConfigureAwait(false);
        return errors.Count == 0 ? 0 : 1;
    }

    private static async Task<int> GuardAsync(
        EvalWorkspace workspace, string source, HistoryOptions baseline, Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory, TextWriter output, CancellationToken cancellationToken)
    {
        var queries = EvalDataset.Read(workspace.DatasetPath).Where(q => q.Category == "paraphrase").ToList();
        var runDir = NewRunDirectory(workspace, "guard");
        AblationConfig[] keywordOnly = [new AblationConfig { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }];
        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, keywordOnly, embedderFactory, cancellationToken).ConfigureAwait(false);
        var segments = corpus.SegmentCount();
        var checks = new List<ParaphraseCheck>();
        foreach (var query in queries)
        {
            var gold = await corpus.ReadRowsAsync(query.Relevant.ToList(), cancellationToken).ConfigureAwait(false);
            checks.Add(ParaphraseGuard.Check(query.Id, query.Text, gold.Select(r => r.Content), corpus.DocumentFrequency, segments));
        }

        EvalWorkspace.WriteJson(Path.Combine(runDir, "guard.json"), checks);
        var failed = checks.Count(c => !c.Passed);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Paraphrase guard: {checks.Count - failed} passed, {failed} failed ({Path.Combine(runDir, "guard.json")})")).ConfigureAwait(false);
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> HoldoutAsync(
        EvalWorkspace workspace, string source, HistoryOptions baseline, Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory, TextWriter output, CancellationToken cancellationToken)
    {
        workspace.VerifyRegistration();
        var plan = workspace.ReadPlan();
        var holdout = plan.Holdout!;
        var configs = plan.Configs.Where(c => c.Name == plan.Baseline || c.Name == holdout.Config).ToList();
        if (configs.Count != 2)
        {
            throw new InvalidOperationException($"ablation.json must contain both '{plan.Baseline}' and '{holdout.Config}'.");
        }

        var (runDir, report) = await RunAsync(workspace, source, baseline, configs, plan.Baseline, EvalSplits.Test, "holdout", embedderFactory, cancellationToken).ConfigureAwait(false);
        var overall = report.Comparisons.Single(c => c.Config == holdout.Config && c.Slice == "all");
        var nonInferior = EvalStatistics.NonInferior(overall.Low, holdout.Margin);
        EvalWorkspace.WriteJson(Path.Combine(runDir, "verdict.json"), new { holdout.Config, plan.Baseline, holdout.Margin, overall.Mean, overall.Low, overall.High, overall.PValue, NonInferior = nonInferior });
        var verdict = string.Create(CultureInfo.InvariantCulture,
            $"Holdout {holdout.Config} vs {plan.Baseline}: Δ nDCG@10 {overall.Mean:+0.000;-0.000} [{overall.Low:+0.000;-0.000}, {overall.High:+0.000;-0.000}], non-inferior (margin {holdout.Margin}): {(nonInferior ? "yes" : "no")}.");
        await output.WriteLineAsync(verdict + " Gate H is the owner's decision; a run on a mostly-legacy snapshot is provisional.").ConfigureAwait(false);
        return 0;
    }

    private static async Task<(string RunDirectory, AblationReport Report)> RunAsync(
        EvalWorkspace workspace,
        string source,
        HistoryOptions baseline,
        IReadOnlyList<AblationConfig> configs,
        string baselineConfig,
        string split,
        string kind,
        Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
        CancellationToken cancellationToken)
    {
        var all = EvalDataset.Read(workspace.DatasetPath);
        var errors = EvalDataset.Validate(all);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"dataset.jsonl has {errors.Count} problems; run 'validate'.");
        }

        var queries = split == "all" ? all : all.Where(q => EvalSplits.Assign(q.Id) == split).ToList();
        var runDir = NewRunDirectory(workspace, kind + "-" + split);
        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, configs, embedderFactory, cancellationToken).ConfigureAwait(false);
        var report = await AblationRunner.RunAsync(configs, queries, corpus.SearchAsync, baselineConfig, cancellationToken).ConfigureAwait(false);
        EvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "report.md"),
            AblationRunner.Markdown(report, string.Create(CultureInfo.InvariantCulture, $"{kind} on {split} ({queries.Count} queries)")),
            cancellationToken).ConfigureAwait(false);
        return (runDir, report);
    }

    private static async Task<int> KappaAsync(string path, TextWriter output)
    {
        // judge-agreement.csv: one "messageId,llmGrade,humanGrade" line per double-labelled item (ids and grades only).
        var pairs = File.ReadLines(path)
            .Select(line => line.Split(','))
            .Where(cells => cells.Length == 3 && long.TryParse(cells[0], CultureInfo.InvariantCulture, out _))
            .Select(cells => (Llm: int.Parse(cells[1], CultureInfo.InvariantCulture), Human: int.Parse(cells[2], CultureInfo.InvariantCulture)))
            .ToList();
        var kappa = EvalStatistics.CohensKappa(pairs.Select(p => p.Llm).ToList(), pairs.Select(p => p.Human).ToList());
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"LLM-vs-human κ = {kappa:F3} on {pairs.Count} items: {(kappa >= MinKappa ? "usable" : "below 0.6, LLM labels may not be used")}")).ConfigureAwait(false);
        return kappa >= MinKappa ? 0 : 1;
    }

    private static OllamaHistoryEmbedder LiveEmbedder(string? endpoint, IOptionsMonitor<HistoryOptions> history)
    {
        var memory = new MemoryMcpOptions();
        memory.Ollama.Endpoint = endpoint ?? Environment.GetEnvironmentVariable("MemoryMcp__Ollama__Endpoint") ?? "http://embeddings:11434";
        memory.Ollama.Model = Environment.GetEnvironmentVariable("MemoryMcp__Ollama__Model") ?? memory.Ollama.Model;
        var memoryMonitor = new StaticOptionsMonitor<MemoryMcpOptions>(memory);
        return new OllamaHistoryEmbedder(new OllamaEmbeddingService(memoryMonitor, NullLogger<OllamaEmbeddingService>.Instance), memoryMonitor, history);
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

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
