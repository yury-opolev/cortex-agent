using System.Globalization;
using System.Text;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.History.Evaluation.Smoke;

/// <summary>
/// Local smoke-evaluation CLI (§14.2), run inside the agent container so it reads the live
/// <c>messages.db</c> (via a backup copy) and writes only under <c>/app/state/eval/smoke</c>:
/// <c>dotnet Cortex.Contained.Agent.Host.dll history-smoke &lt;init|generate-queries|pool|register|run&gt; [--dir D] [--source DB] [--tz ZONE]</c>.
/// Run outputs contain ids and numbers only; the <c>pool</c> command writes message text for
/// labelling, inside the workspace only. Nothing here is ever committed.
/// </summary>
public static class HistorySmokeCli
{
    private const string Usage =
        "usage: history-smoke <init|generate-queries|pool|register|run> [--dir /app/state/eval/smoke] [--source /app/state/messages.db] [--tz Europe/Copenhagen]\n" +
        "generate-queries needs SMOKE_LLM_ENDPOINT, SMOKE_LLM_API_KEY, SMOKE_LLM_MODEL and optional SMOKE_LLM_API (default openai-completions).";

    /// <summary>Entry point; returns a process exit code.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        if (args.Count == 0)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        var workspace = new SmokeWorkspace(Option(args, "--dir") ?? "/app/state/eval/smoke");
        var source = Option(args, "--source") ?? "/app/state/messages.db";
        var options = new HistoryOptions { Enabled = true, TimeZone = Option(args, "--tz") ?? "Europe/Copenhagen" };
        Directory.CreateDirectory(workspace.Directory);

        try
        {
            switch (args[0])
            {
                case "init":
                    Init(workspace);
                    await output.WriteLineAsync($"Workspace ready: {workspace.Directory}. Write queries.jsonl and analysis-plan.md.").ConfigureAwait(false);
                    return 0;
                case "generate-queries":
                    await GenerateQueriesAsync(workspace, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync($"Wrote {workspace.ModelQueriesPath}").ConfigureAwait(false);
                    return 0;
                case "pool":
                    var poolDir = await PoolAsync(workspace, source, options, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync($"Pooled candidates for labelling in {poolDir}").ConfigureAwait(false);
                    return 0;
                case "register":
                    workspace.Register();
                    await output.WriteLineAsync($"Registered: {workspace.PreregistrationPath}").ConfigureAwait(false);
                    return 0;
                case "run":
                    var (runDir, gate) = await RunEvaluationAsync(workspace, source, options, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"Report: {Path.Combine(runDir, "report.md")} — gate {(gate.Passed ? "PASS" : "FAIL")} (wins {gate.Wins}, losses {gate.Losses}, p={gate.PValue:F4})")).ConfigureAwait(false);
                    return gate.Passed ? 0 : 1;
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

    /// <summary>Scores every query for systems a, b and c and writes <c>results.json</c> and <c>report.md</c>.</summary>
    public static async Task<(string RunDirectory, SmokeGateResult Gate)> RunEvaluationAsync(
        SmokeWorkspace workspace, string sourceDb, HistoryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.VerifyRegistration();
        var queries = workspace.ReadQueries();
        var modelQueries = workspace.ReadModelQueries();
        var runDir = NewRunDirectory(workspace, "run");
        await using var corpus = await SmokeCorpus.CreateAsync(sourceDb, runDir, options, cancellationToken).ConfigureAwait(false);

        var scores = new List<SmokeScoreRow>();
        var latency = new List<SmokeLatencyRow>();
        var maxBudget = SmokeReport.Budgets.Max();
        foreach (var query in queries)
        {
            var gold = query.Gold.ToHashSet();
            var runs = new Dictionary<string, SystemRun>(StringComparer.Ordinal)
            {
                [SmokeReport.Oracle] = await corpus.SearchAsync(query.Oracle, maxBudget, cancellationToken).ConfigureAwait(false),
            };
            if (modelQueries.TryGetValue(query.Id, out var modelSpec))
            {
                runs[SmokeReport.Model] = await corpus.SearchAsync(modelSpec, maxBudget, cancellationToken).ConfigureAwait(false);
                runs[SmokeReport.Like] = await corpus.LikeBaselineAsync(modelSpec.Query, 500, cancellationToken).ConfigureAwait(false);
            }

            foreach (var (system, run) in runs)
            {
                latency.Add(new SmokeLatencyRow(query.Id, system, run.FirstPageMs));
                foreach (var budget in SmokeReport.Budgets)
                {
                    var score = ReadingBudgetMetrics.Score(run.Hits, gold, budget);
                    scores.Add(new SmokeScoreRow(query.Id, system, budget, score.LocatorHit, score.ExcerptHit, score.LinesToFirstGold, score.WordsRead));
                }
            }
        }

        SmokeWorkspace.WriteJson(Path.Combine(runDir, "results.json"), new { scores, latency });
        await File.WriteAllTextAsync(Path.Combine(runDir, "report.md"), SmokeReport.Markdown(scores, latency, corpus.SegmentSizes()), cancellationToken).ConfigureAwait(false);
        return (runDir, SmokeReport.Gate(scores));
    }

    private static void Init(SmokeWorkspace workspace)
    {
        var example = Path.Combine(workspace.Directory, "queries.example.jsonl");
        File.WriteAllText(example,
            "{\"id\":\"q01\",\"request\":\"When did PersonA say the train to TownB leaves?\",\"oracle\":{\"query\":\"train TownB\",\"order\":\"relevance\"},\"gold\":[123],\"goldFoundBy\":\"browse\"}\n");
        if (!File.Exists(workspace.AnalysisPlanPath))
        {
            File.WriteAllText(workspace.AnalysisPlanPath,
                "# Analysis plan (fill in before `register`)\n\n- Primary: (b) vs (c) excerpt recall at 1,500 words, two-sided sign test, alpha 0.05.\n- Loss categories: inflection, language mismatch, tokenisation, coverage.\n");
        }
    }

    private static async Task GenerateQueriesAsync(SmokeWorkspace workspace, CancellationToken cancellationToken)
    {
        var endpoint = Required("SMOKE_LLM_ENDPOINT");
        var apiKey = Required("SMOKE_LLM_API_KEY");
        var model = Required("SMOKE_LLM_MODEL");
        var api = Environment.GetEnvironmentVariable("SMOKE_LLM_API") ?? "openai-completions";

        using var client = new DirectLlmClient(new SimpleHttpClientFactory(), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(new LlmCredentials
        {
            Providers =
            [
                new LlmProviderCredential
                {
                    Name = "smoke-llm", Api = api, BaseUrl = endpoint, Kind = CredentialKind.ApiKey,
                    ApiKey = apiKey, Models = [model], DefaultModel = model,
                },
            ],
        });

        var writer = new ModelQueryWriter(client, model);
        var written = new List<ModelQuery>();
        foreach (var query in workspace.ReadQueries())
        {
            var spec = await writer.WriteAsync(query.Request, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false)
                ?? new SmokeQuerySpec { Query = query.Request };
            written.Add(new ModelQuery(query.Id, spec));
        }

        workspace.WriteModelQueries(written);
    }

    private static async Task<string> PoolAsync(SmokeWorkspace workspace, string sourceDb, HistoryOptions options, CancellationToken cancellationToken)
    {
        var runDir = NewRunDirectory(workspace, "pool");
        var modelQueries = workspace.ReadModelQueries();
        await using var corpus = await SmokeCorpus.CreateAsync(sourceDb, runDir, options, cancellationToken).ConfigureAwait(false);
        var poolDir = Path.Combine(runDir, "pool");
        Directory.CreateDirectory(poolDir);
        foreach (var query in workspace.ReadQueries())
        {
            var runs = new List<SystemRun> { await corpus.SearchAsync(query.Oracle, 3000, cancellationToken).ConfigureAwait(false) };
            if (modelQueries.TryGetValue(query.Id, out var spec))
            {
                runs.Add(await corpus.SearchAsync(spec, 3000, cancellationToken).ConfigureAwait(false));
                runs.Add(await corpus.LikeBaselineAsync(spec.Query, 100, cancellationToken).ConfigureAwait(false));
            }

            var ids = runs.SelectMany(r => r.Hits).SelectMany(h => h.Lines).Select(l => l.MessageId).ToHashSet();
            var rows = await corpus.ReadRowsAsync(ids, cancellationToken).ConfigureAwait(false);
            var page = new StringBuilder();
            page.AppendLine(CultureInfo.InvariantCulture, $"# {query.Id}: {query.Request}");
            foreach (var row in rows)
            {
                page.AppendLine(CultureInfo.InvariantCulture, $"- [{row.Id}] {row.ChannelId} {row.Timestamp:yyyy-MM-dd HH:mm} {row.Role}: {row.Content.ReplaceLineEndings(" ")}");
            }

            await File.WriteAllTextAsync(Path.Combine(poolDir, query.Id + ".md"), page.ToString(), cancellationToken).ConfigureAwait(false);
        }

        return poolDir;
    }

    private static string NewRunDirectory(SmokeWorkspace workspace, string kind) =>
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

    private static string Required(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{variable} is not set.");

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
