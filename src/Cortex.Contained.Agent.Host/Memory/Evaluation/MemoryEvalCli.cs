using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using Cortex.Contained.Contracts.Config.Yaml;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Memory.Evaluation;

/// <summary>
/// Evaluation CLI for hybrid memory search (Gate H for memory), run by the owner inside the agent
/// container on a private copy of the raw memory store (D6):
/// <c>dotnet Cortex.Contained.Agent.Host.dll memory-eval &lt;init|sample|validate|guard|pool|register|ablate|holdout|kappa|rerank-curve|gate-r&gt;</c>.
/// Every search runs on a copy that is deleted when the command ends; reports hold ids and numbers
/// only. The Gate H decision and switching <c>MemoryMcp:Search:DefaultMode</c> are the owner's.
/// </summary>
public static class MemoryEvalCli
{
    private const string Usage =
        "usage: memory-eval <init|sample|validate|guard|pool|register|ablate|holdout|kappa|rerank-curve|gate-r> [--dir /app/state/eval/memory] " +
        "[--source /app/state/memory] [--count 300] [--depth 10] [--split dev|test|all] [--embedder http://embeddings:11434] [--labels judge-agreement.csv]\n" +
        "rerank-curve [--condition normal|embedder-busy|cold] [--base CONFIG] [--reranker http://reranker:8080] [--timeout-ms 30000]; " +
        "gate-r --config CONFIG [--timeout-ms 2000]; both read the reranker key from Reranker__ApiKey.";

    /// <summary>Minimum LLM-vs-human agreement for LLM relevance labels.</summary>
    private const double MinKappa = 0.6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string GenerationGuide = """
        # Writing memory evaluation queries

        `seeds.jsonl` holds one seed per line: a stored memory (raw text — keep this workspace local and delete
        `seeds.jsonl` once the dataset is written) and the category to write a query for. Abstention seeds have no
        memory: write a plausible question that no stored memory answers.

        For each seed, write one `dataset.jsonl` line (see `dataset.example.jsonl`):
        - `text`: the question as the user would ask it. `paraphrase`: no content word shared with the memory.
          `cross_language`: another language than the memory. `identifier`: the exact identifier from the memory.
          `tag_filtered`: also set `tags` to the filter an ideal searcher would use. `multi`: a question answered only
          by several memories together.
        - `sourceMemoryId`: the seed's memory id; `stratum`: `synthetic` (or `real` for real past recall requests).
        - `queryModelFamily`: the model family that wrote the query; it must differ from the judge's family.
        - Optional `modelQuery`: the query (and tags) the main model would write for the same need.

        Then run `memory-eval pool`, have a judge from another model family grade every pooled memory 0–2 per
        query (`judgments`, `judgeModelFamily`), double-label a sample by hand for `memory-eval kappa`, and run
        `memory-eval validate` until it reports no problems. Gate H needs at least 250 queries.
        """;

    private const string AnalysisPlanTemplate = """
        # Hybrid memory search — pre-registered analysis plan

        Fill in every item, then run `memory-eval register` before the first `holdout` run. Changing this file,
        `dataset.jsonl` or `ablation.json` afterwards makes `holdout` refuse to run.

        ## Data
        - Snapshot: date, number of memories, share created after the episodic-history increments.
        - Dataset: at least 250 queries; counts per category and stratum (`real` = real recall requests, co-primary).
        - Labels: graded 0–2 per memory from the pooled top-10 of every configuration (`memory-eval pool`); judge model
          family ≠ query model family; LLM-vs-human κ on a sample (must be ≥ 0.6).
        - Paraphrase guard (`memory-eval guard`): all paraphrase queries pass (Jaccard ≤ 0.2, no shared rare token).
        - Split: dev/test by `EvalSplits.Assign` (seed 20261121, 30% dev).

        ## Hypotheses and metrics
        - Primary: nDCG@10 of `holdout.config` vs `dense` (today's semantic search) on the test split;
          non-inferiority margin = `holdout.margin`.
        - Co-primary: the same on the `stratum:real` slice.
        - Secondary: recall@10, MRR, words to the first relevant memory, judged@10 (≥ 0.8 or the pool is deepened),
          `multi` set F1, abstention Brier/ECE, p95 latency; per-category CIs (identifier and exact-wording categories
          are where keyword search should help).
        - Oracle vs model-written queries: `hybrid-model-queries` and `dense-model-queries`.

        ## Statistics
        - Paired bootstrap 95% CI (10,000 resamples, seed 20261121) and a paired randomisation test.
        - Per-category results as CIs only; dev-split ablations are exploratory; only the single pre-registered
          holdout comparison is confirmatory.

        ## Decision rule (owner)
        - Gate H for memory passes when the primary comparison is non-inferior and no category's CI shows a clear loss
          the owner is unwilling to accept. Only then set `MemoryMcp:Search:DefaultMode=Hybrid` (and the chosen
          `MemoryMcp:Search` values) and restart.
        """;

    /// <summary>
    /// Entry point with the live embedder from the agent configuration; returns a process exit code. Ctrl+C and
    /// SIGTERM cancel the run instead of killing the process, so the private raw copy is deleted on the way out.
    /// </summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        var live = LiveOptions(args);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void Interrupt() => cancellation.Cancel();
        ConsoleCancelEventHandler onCancelKey = (_, e) =>
        {
            e.Cancel = true;
            Interrupt();
        };
        Console.CancelKeyPress += onCancelKey;
        using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Interrupt();
        });
        try
        {
            return await RunAsync(args, output, live, options => new OllamaEmbeddingService(new StaticOptionsMonitor(options), NullLogger<OllamaEmbeddingService>.Instance), cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKey;
        }
    }

    /// <summary>Entry point with explicit options and an injected embedder (tests use a deterministic fake).</summary>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        MemoryMcpOptions live,
        Func<MemoryMcpOptions, IEmbeddingService> embedderFactory,
        CancellationToken cancellationToken,
        Func<RerankerOptions, IReranker>? rerankerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        rerankerFactory ??= RerankEvalSetup.LiveReranker;
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(embedderFactory);
        if (args.Count == 0)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        var workspace = new MemoryEvalWorkspace(Option(args, "--dir") ?? MemoryEvalWorkspace.DefaultDirectory);
        Directory.CreateDirectory(workspace.Directory);
        if (MemoryEvalCorpus.SweepAbandoned(workspace.RunsDirectory) is var swept and > 0)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Removed {swept} leftover raw evaluation copies from interrupted runs.")).ConfigureAwait(false);
        }

        var context = new CommandContext(workspace, live, embedderFactory, output);
        try
        {
            switch (args[0])
            {
                case "init":
                    Init(workspace);
                    await output.WriteLineAsync($"Workspace ready: {workspace.Directory}. Run 'sample', write dataset.jsonl, fill in analysis-plan.md, then 'validate'.").ConfigureAwait(false);
                    return 0;
                case "sample":
                    return await SampleAsync(context, IntOption(args, "--count", 300), cancellationToken).ConfigureAwait(false);
                case "validate":
                    return await ValidateAsync(workspace, output).ConfigureAwait(false);
                case "guard":
                    return await GuardAsync(context, cancellationToken).ConfigureAwait(false);
                case "pool":
                    return await PoolAsync(context, IntOption(args, "--depth", 10), cancellationToken).ConfigureAwait(false);
                case "register":
                    workspace.Register();
                    await output.WriteLineAsync($"Registered: {workspace.PreregistrationPath}").ConfigureAwait(false);
                    return 0;
                case "ablate":
                    var plan = workspace.ReadPlan();
                    var (runDir, _) = await RunAsync(context, plan.Configs, plan.Baseline, Option(args, "--split") ?? EvalSplits.Dev, "ablate", cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync($"Report: {Path.Combine(runDir, "report.md")}").ConfigureAwait(false);
                    return 0;
                case "holdout":
                    return await HoldoutAsync(context, cancellationToken).ConfigureAwait(false);
                case "kappa":
                    return await KappaAsync(Option(args, "--labels") ?? Path.Combine(workspace.Directory, "judge-agreement.csv"), output).ConfigureAwait(false);
                case "rerank-curve":
                    return await MemoryRerankEval.CurveAsync(workspace, live, embedderFactory, rerankerFactory, args, output, cancellationToken).ConfigureAwait(false);
                case "gate-r":
                    return await MemoryRerankEval.GateAsync(workspace, live, embedderFactory, rerankerFactory, args, output, cancellationToken).ConfigureAwait(false);
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
        catch (OperationCanceledException)
        {
            await output.WriteLineAsync("cancelled: the raw evaluation copy was deleted.").ConfigureAwait(false);
            return 130;
        }
    }

    /// <summary>
    /// The agent's <c>MemoryMcp</c> configuration, loaded like <c>Program.cs</c> (appsettings, environment,
    /// then <c>cortex.yml</c>), with the CLI overrides.
    /// </summary>
    private static MemoryMcpOptions LiveOptions(IReadOnlyList<string> args)
    {
        var options = new MemoryMcpOptions();
        new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddYamlFile("cortex.yml", optional: true)
            .Build()
            .GetSection(MemoryMcpOptions.SectionName)
            .Bind(options);
        if (Option(args, "--source") is { } source)
        {
            options.DataDirectory = source;
        }

        if (Option(args, "--embedder") is { } endpoint)
        {
            options.Ollama.Endpoint = endpoint;
        }

        return options;
    }

    private static void Init(MemoryEvalWorkspace workspace)
    {
        File.WriteAllLines(Path.Combine(workspace.Directory, "dataset.example.jsonl"),
        [
            """{"id":"m001","text":"which editor theme does PersonA like","category":"paraphrase","stratum":"synthetic","judgments":[{"memoryId":"3f0c…","grade":2},{"memoryId":"9a1d…","grade":0}],"sourceMemoryId":"3f0c…","modelQuery":{"text":"PersonA editor theme"},"queryModelFamily":"family-a","judgeModelFamily":"family-b"}""",
            """{"id":"m002","text":"invoices tagged finance from TownB","category":"tag_filtered","tags":["finance"],"judgments":[{"memoryId":"77b2…","grade":2}],"queryModelFamily":"family-a","judgeModelFamily":"family-b"}""",
            """{"id":"m003","text":"did PersonB ever mention a boat trip","category":"abstention","expectAbstain":true,"judgments":[{"memoryId":"5e44…","grade":0}]}""",
        ]);
        File.WriteAllText(workspace.GenerationGuidePath, GenerationGuide);
        if (!File.Exists(workspace.AnalysisPlanPath))
        {
            File.WriteAllText(workspace.AnalysisPlanPath, AnalysisPlanTemplate);
        }

        if (!File.Exists(workspace.AblationPlanPath))
        {
            workspace.WritePlan(new MemoryAblationPlan());
        }
    }

    private static async Task<int> SampleAsync(CommandContext context, int count, CancellationToken cancellationToken)
    {
        await using var corpus = await context.OpenCorpusAsync("sample", cancellationToken).ConfigureAwait(false);
        var memories = await corpus.ListMemoriesAsync(cancellationToken).ConfigureAwait(false);
        var seeds = MemoryQuerySeeds.Select(memories, count);
        await File.WriteAllLinesAsync(context.Workspace.SeedsPath, seeds.Select(s => JsonSerializer.Serialize(s, Json)), cancellationToken).ConfigureAwait(false);
        await context.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{seeds.Count} seeds from {memories.Count} memories: {context.Workspace.SeedsPath}. It holds raw memory text: delete it once dataset.jsonl is written.")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ValidateAsync(MemoryEvalWorkspace workspace, TextWriter output)
    {
        var queries = MemoryEvalDataset.Read(workspace.DatasetPath);
        var errors = MemoryEvalDataset.ValidateForRegistration(queries);
        foreach (var error in errors)
        {
            await output.WriteLineAsync("invalid " + error).ConfigureAwait(false);
        }

        var dev = queries.Count(q => EvalSplits.Assign(q.Id) == EvalSplits.Dev);
        var categories = string.Join(", ", queries.GroupBy(q => q.Category, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{queries.Count} queries ({categories}): {dev} dev / {queries.Count - dev} test; {queries.Count(q => q.Stratum == "real")} real requests; {errors.Count} problems")).ConfigureAwait(false);
        return errors.Count == 0 ? 0 : 1;
    }

    private static async Task<int> GuardAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var queries = MemoryEvalDataset.Read(context.Workspace.DatasetPath).Where(q => q.Category == "paraphrase").ToList();
        var runDir = NewRunDirectory(context.Workspace, "guard");
        List<ParaphraseCheck> checks;
        await using (var corpus = await context.OpenCorpusAsync(runDir, cancellationToken).ConfigureAwait(false))
        {
            var memories = (await corpus.ListMemoriesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(m => m.MemoryId, StringComparer.Ordinal);
            var documentFrequency = memories.Values
                .SelectMany(m => KeywordTokenizer.Tokenize(TextFolder.Fold(m.Title + "\n" + m.Content)).Distinct(StringComparer.Ordinal))
                .GroupBy(t => t, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
            checks = queries
                .Select(q => ParaphraseGuard.Check(
                    q.Id,
                    q.Text,
                    q.Relevant.Where(memories.ContainsKey).Select(id => memories[id].Title + "\n" + memories[id].Content),
                    term => documentFrequency.GetValueOrDefault(term),
                    memories.Count))
                .ToList();
        }

        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "guard.json"), checks);
        var failed = checks.Count(c => !c.Passed);
        await context.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Paraphrase guard: {checks.Count - failed} passed, {failed} failed ({Path.Combine(runDir, "guard.json")})")).ConfigureAwait(false);
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> PoolAsync(CommandContext context, int depth, CancellationToken cancellationToken)
    {
        var queries = MemoryEvalDataset.Read(context.Workspace.DatasetPath);
        var plan = context.Workspace.ReadPlan();
        var lines = new List<string>();
        await using (var corpus = await context.OpenCorpusAsync("pool", cancellationToken).ConfigureAwait(false))
        {
            foreach (var query in queries)
            {
                var judged = query.Judgments.Select(j => j.MemoryId).ToHashSet(StringComparer.Ordinal);
                var pooled = new List<string>();
                foreach (var config in plan.Configs)
                {
                    var run = await corpus.SearchAsync(config, query, cancellationToken).ConfigureAwait(false);
                    pooled.AddRange(run.Memories.Take(depth).Select(m => m.MemoryId));
                }

                var unjudged = pooled.Distinct(StringComparer.Ordinal).Where(id => !judged.Contains(id)).ToList();
                lines.Add(JsonSerializer.Serialize(new { queryId = query.Id, memoryIds = unjudged }, Json));
            }
        }

        await File.WriteAllLinesAsync(context.Workspace.PoolPath, lines, cancellationToken).ConfigureAwait(false);
        await context.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Pool for {queries.Count} queries (depth {depth}, {plan.Configs.Count} configurations): {context.Workspace.PoolPath}")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> HoldoutAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.Workspace.VerifyRegistration();
        var plan = context.Workspace.ReadPlan();
        var holdout = plan.Holdout!;
        var configs = plan.Configs.Where(c => c.Name == plan.Baseline || c.Name == holdout.Config).ToList();
        if (configs.Count != 2)
        {
            throw new InvalidOperationException($"ablation.json must contain both '{plan.Baseline}' and '{holdout.Config}'.");
        }

        var (runDir, report) = await RunAsync(context, configs, plan.Baseline, EvalSplits.Test, "holdout", cancellationToken).ConfigureAwait(false);
        var overall = report.Comparisons.Single(c => c.Config == holdout.Config && c.Slice == "all");
        var nonInferior = EvalStatistics.NonInferior(overall.Low, holdout.Margin);
        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "verdict.json"), new { holdout.Config, plan.Baseline, holdout.Margin, overall.Mean, overall.Low, overall.High, overall.PValue, NonInferior = nonInferior });
        await context.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Holdout {holdout.Config} vs {plan.Baseline}: Δ nDCG@10 {overall.Mean:+0.000;-0.000} [{overall.Low:+0.000;-0.000}, {overall.High:+0.000;-0.000}], non-inferior (margin {holdout.Margin}): {(nonInferior ? "yes" : "no")}. Gate H for memory is the owner's decision.")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<(string RunDirectory, AblationReport Report)> RunAsync(
        CommandContext context, IReadOnlyList<MemoryAblationConfig> configs, string baseline, string split, string kind, CancellationToken cancellationToken)
    {
        var all = MemoryEvalDataset.Read(context.Workspace.DatasetPath);
        var errors = MemoryEvalDataset.Validate(all);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"dataset.jsonl has {errors.Count} problems; run 'validate'.");
        }

        var queries = split == "all" ? all : all.Where(q => EvalSplits.Assign(q.Id) == split).ToList();
        var runDir = NewRunDirectory(context.Workspace, kind + "-" + split);
        AblationReport report;
        await using (var corpus = await context.OpenCorpusAsync(runDir, cancellationToken).ConfigureAwait(false))
        {
            report = await MemoryAblationRunner.RunAsync(configs, queries, corpus.SearchAsync, baseline, cancellationToken).ConfigureAwait(false);
        }

        MemoryEvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "report.md"),
            AblationReporting.Markdown(report, string.Create(CultureInfo.InvariantCulture, $"memory {kind} on {split} ({queries.Count} queries)")),
            cancellationToken).ConfigureAwait(false);
        return (runDir, report);
    }

    private static async Task<int> KappaAsync(string path, TextWriter output)
    {
        // judge-agreement.csv: one "memoryId,llmGrade,humanGrade" line per double-labelled item (ids and grades only).
        var pairs = File.ReadLines(path)
            .Select(line => line.Split(','))
            .Where(cells => cells.Length == 3 && int.TryParse(cells[1], CultureInfo.InvariantCulture, out _) && int.TryParse(cells[2], CultureInfo.InvariantCulture, out _))
            .Select(cells => (Llm: int.Parse(cells[1], CultureInfo.InvariantCulture), Human: int.Parse(cells[2], CultureInfo.InvariantCulture)))
            .ToList();
        var kappa = EvalStatistics.CohensKappa(pairs.Select(p => p.Llm).ToList(), pairs.Select(p => p.Human).ToList());
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"LLM-vs-human κ = {kappa:F3} on {pairs.Count} items: {(kappa >= MinKappa ? "usable" : "below 0.6, LLM labels may not be used")}")).ConfigureAwait(false);
        return kappa >= MinKappa ? 0 : 1;
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

    private static int IntOption(IReadOnlyList<string> args, string name, int fallback) =>
        Option(args, name) is { } text && int.TryParse(text, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    private sealed record CommandContext(
        MemoryEvalWorkspace Workspace, MemoryMcpOptions Live, Func<MemoryMcpOptions, IEmbeddingService> EmbedderFactory, TextWriter Output)
    {
        /// <summary>Opens a copy of the live store under <paramref name="runDirectory"/> (a name is placed under <c>runs/</c>).</summary>
        public Task<MemoryEvalCorpus> OpenCorpusAsync(string runDirectory, CancellationToken cancellationToken)
        {
            var directory = Path.IsPathRooted(runDirectory) ? runDirectory : NewRunDirectory(this.Workspace, runDirectory);
            return MemoryEvalCorpus.CreateAsync(this.Live.DataDirectory, directory, this.Live, this.EmbedderFactory(this.Live), cancellationToken);
        }
    }

    private sealed class StaticOptionsMonitor(MemoryMcpOptions value) : IOptionsMonitor<MemoryMcpOptions>
    {
        public MemoryMcpOptions CurrentValue => value;

        public MemoryMcpOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<MemoryMcpOptions, string?> listener) => null;
    }
}
