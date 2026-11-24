using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Evaluation.Calibration;

/// <summary>What the <c>judge-calibration</c> command works with (supplied by <see cref="EvaluationRegistration"/>).</summary>
/// <param name="Options">The labelling options (call cap, pacing, attempts, the query writer's model).</param>
/// <param name="Llm">The agent's LLM client.</param>
/// <param name="ProviderFor">Provider that lists a model, or null.</param>
/// <param name="CalibrationDirectory">The calibration root (<c>/app/state/eval/calibration</c>).</param>
/// <param name="Workspaces">Workspace directory per tool (their locked samples join the archived ones).</param>
/// <param name="Time">Clock (report name, pacing, credential wait).</param>
/// <param name="CredentialsReady">True once the Bridge pushed the LLM credentials (null: always).</param>
/// <param name="CredentialsTimeout">How long to wait for them.</param>
/// <param name="CredentialsPoll">How often to look.</param>
/// <param name="Delay">Waits between attempts (tests pass a no-op).</param>
public sealed record CalibrationContext(
    LabellingOptions Options,
    ILlmClient Llm,
    Func<string, string?> ProviderFor,
    string CalibrationDirectory,
    IReadOnlyDictionary<string, string> Workspaces,
    TimeProvider Time,
    Func<bool>? CredentialsReady = null,
    TimeSpan? CredentialsTimeout = null,
    TimeSpan? CredentialsPoll = null,
    Func<TimeSpan, CancellationToken, Task>? Delay = null);

/// <summary>Agreement of one judge (model × rubric) with the owner on the calibration set (ids and numbers only).</summary>
/// <param name="Judge">The model id, or <see cref="JudgeCalibrationCli.Baseline"/>.</param>
/// <param name="Rubric">The rubric name (<c>default</c> or a file name), or <c>as labelled</c> for the baseline.</param>
/// <param name="RubricId">The rubric's id (<see cref="RelevanceJudge.RubricId"/>), or null for the baseline.</param>
/// <param name="Items">Items graded (failed ones excluded).</param>
/// <param name="Failed">Items the judge gave no valid answer for.</param>
/// <param name="Calls">LLM calls made.</param>
/// <param name="Kappa">Unweighted Cohen's κ (as the gate computes it).</param>
/// <param name="LinearKappa">Linear-weighted κ.</param>
/// <param name="QuadraticKappa">Quadratic-weighted κ.</param>
/// <param name="Agreement">Share of exact agreement (0–1).</param>
/// <param name="HistoryKappa">κ over the history items.</param>
/// <param name="MemoryKappa">κ over the memory items.</param>
/// <param name="JudgeHigher">Items the judge graded higher than the owner.</param>
/// <param name="OwnerHigher">Items the owner graded higher than the judge.</param>
/// <param name="Confusion">Counts: rows the judge's grade 0–2, columns the owner's.</param>
public sealed record CalibrationStats(
    string Judge,
    string Rubric,
    string? RubricId,
    int Items,
    int Failed,
    int Calls,
    double? Kappa,
    double? LinearKappa,
    double? QuadraticKappa,
    double? Agreement,
    double? HistoryKappa,
    double? MemoryKappa,
    int JudgeHigher,
    int OwnerHigher,
    int[][] Confusion);

/// <summary>
/// <c>judge-calibration calibrate --models m1,m2 [--rubrics r1,r2] [--estimate]</c> (an evaluation job): pilots judge
/// models × rubric variants on the calibration set — every locked κ sample (<see cref="CalibrationArchive"/> plus the
/// current workspaces) — grading each item on its own, and compares each with the owner's grades: κ as the gate computes
/// it, weighted κ, agreement, κ per tool and the confusion matrix, next to the stored judge's grades of the run. Output
/// and the plaintext report hold model ids, rubric names and ids and numbers only, never item text.
/// </summary>
public static partial class JudgeCalibrationCli
{
    public const string Usage = "usage: judge-calibration calibrate --models <m1,m2,...> [--rubrics <r1,r2,...>] [--estimate]";

    /// <summary>Label of the row with the grades the run's judge stored (batched judging).</summary>
    public const string Baseline = "stored judge (run)";

    public const int MaxModels = 6;

    public const int MaxRubrics = 4;

    public const int MaxRubricBytes = 8 * 1024;

    private const string DefaultRubricName = "default";

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Runs the command; 0 done, 1 stopped by the provider or the call cap (completed combinations are reported), 2 refused.</summary>
    public static async Task<int> RunAsync(EvaluationRun run, CalibrationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(context);
        var output = run.Output;
        if (run.Args.Count == 0 || !string.Equals(run.Args[0], "calibrate", StringComparison.Ordinal))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        var archive = new CalibrationArchive(context.CalibrationDirectory);
        var (models, rubrics, error) = Parse(run.Args, archive);
        if (error is not null)
        {
            await output.WriteLineAsync("error: " + error).ConfigureAwait(false);
            return 2;
        }

        IReadOnlyList<KappaItem> items;
        try
        {
            items = CalibrationArchive.Dedupe(CalibrationArchive.Locked(context.Workspaces, run.Protection).Concat(archive.Load(run.Protection)));
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
            return 2;
        }

        if (items.Count == 0)
        {
            await output.WriteLineAsync("error: no locked κ sample to calibrate on").ConfigureAwait(false);
            return 2;
        }

        var combos = models.Count * rubrics.Count;
        var calls = (long)items.Count * combos;
        var options = context.Options;
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Calibration set: {items.Count} items ({Count(items, GradingService.Tools[0])} history, {Count(items, GradingService.Tools[1])} memory) from the locked κ samples; {models.Count} models × {rubrics.Count} rubrics = {combos} combinations, {calls} LLM calls (one item per call; invalid answers are asked again); cap {options.MaxCalls}.")).ConfigureAwait(false);
        foreach (var model in models.Where(m => string.Equals(ModelFamily.Of(m), ModelFamily.Of(options.QueryModel), StringComparison.Ordinal)))
        {
            await output.WriteLineAsync($"note: {model} is of the query writer's family ({ModelFamily.Of(model)}): informational only, it cannot be the judge (§4.2).").ConfigureAwait(false);
        }

        if (run.Args.Contains("--estimate", StringComparer.Ordinal))
        {
            return 0;
        }

        if (calls > options.MaxCalls)
        {
            await output.WriteLineAsync("error: the calibration needs more LLM calls than Evaluation:Labelling:MaxCalls; pass fewer models or rubrics, or raise the cap.").ConfigureAwait(false);
            return 2;
        }

        if (!await CredentialsGate.WaitAsync(context.CredentialsReady, context.CredentialsTimeout, context.CredentialsPoll, context.Time, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync("error: the agent has no LLM credentials yet (they arrive from the Bridge after a restart); try again in a minute.").ConfigureAwait(false);
            return 2;
        }

        var llm = new LabellingLlm(context.Llm, context.ProviderFor, options, delay: context.Delay, time: context.Time);
        try
        {
            llm.EnsureServed([.. models]);
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
            return 2;
        }

        var results = new List<CalibrationStats>();
        string? stopped = null;
        try
        {
            foreach (var model in models)
            {
                foreach (var (name, text) in rubrics)
                {
                    results.Add(await JudgeAsync(llm, model, name, text, items, cancellationToken).ConfigureAwait(false));
                    var last = results[^1];
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"judged {model} / {name}: {last.Items} items, {last.Failed} failed, {last.Calls} calls")).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is LabellingTransportException or LabellingBudgetExceededException)
        {
            // The messages hold the model id and a status only.
            stopped = ex.Message;
            await output.WriteLineAsync("stopped: " + ex.Message + " The completed combinations are reported.").ConfigureAwait(false);
        }

        var baseline = Stats(Baseline, "as labelled", null, items.Select(i => (i, (int?)i.LlmGrade)).ToList(), 0);
        var ranked = results.OrderByDescending(r => r.Kappa ?? double.NegativeInfinity).ThenBy(r => r.Judge, StringComparer.Ordinal).ThenBy(r => r.Rubric, StringComparer.Ordinal).ToList();
        await output.WriteAsync(Table(baseline, ranked)).ConfigureAwait(false);
        var report = await WriteReportAsync(archive, context.Time, items, models, rubrics, baseline, ranked, stopped, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Report: " + report).ConfigureAwait(false);
        return stopped is null ? 0 : 1;
    }

    private static int Count(IReadOnlyList<KappaItem> items, string tool) => items.Count(i => i.Tool == tool);

    private static (IReadOnlyList<string> Models, IReadOnlyList<(string Name, string Text)> Rubrics, string? Error) Parse(IReadOnlyList<string> args, CalibrationArchive archive)
    {
        var models = List(Option(args, "--models"));
        if (models.Count == 0)
        {
            return ([], [], "--models is required. " + Usage);
        }

        if (models.Count > MaxModels)
        {
            return ([], [], string.Create(CultureInfo.InvariantCulture, $"at most {MaxModels} models per calibration."));
        }

        if (models.FirstOrDefault(m => !ModelPattern().IsMatch(m)) is { } badModel)
        {
            return ([], [], $"'{Shorten(badModel)}' is not a model id.");
        }

        var names = Option(args, "--rubrics") is { } value ? List(value) : [DefaultRubricName];
        if (names.Count is 0 or > MaxRubrics)
        {
            return ([], [], string.Create(CultureInfo.InvariantCulture, $"pass 1 to {MaxRubrics} rubrics."));
        }

        var rubrics = new List<(string, string)>();
        foreach (var name in names)
        {
            if (string.Equals(name, DefaultRubricName, StringComparison.Ordinal))
            {
                rubrics.Add((name, RelevanceJudge.DefaultRubric));
                continue;
            }

            if (!RubricNamePattern().IsMatch(name))
            {
                return ([], [], $"'{Shorten(name)}' is not a rubric name (default, or lowercase letters, digits and dashes, at most 32).");
            }

            var path = Path.Combine(archive.RubricsDirectory, name + ".txt");
            if (!File.Exists(path))
            {
                return ([], [], $"rubric '{name}' not found: put it in {path} (plain text, invented examples only).");
            }

            if (new FileInfo(path).Length > MaxRubricBytes)
            {
                return ([], [], string.Create(CultureInfo.InvariantCulture, $"rubric '{name}' is larger than {MaxRubricBytes} bytes."));
            }

            // Rubrics carry no personal data, so they are plaintext (not sealed).
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                return ([], [], $"rubric '{name}' is empty.");
            }

            rubrics.Add((name, text));
        }

        return (models, rubrics, null);
    }

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

    private static List<string> List(string? value) =>
        value is null ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    private static string Shorten(string value) => value.Length <= 64 ? value : value[..64] + "…";

    private static async Task<CalibrationStats> JudgeAsync(
        LabellingLlm llm, string model, string rubricName, string rubric, IReadOnlyList<KappaItem> items, CancellationToken cancellationToken)
    {
        var before = llm.Calls;
        var graded = new List<(KappaItem Item, int? Grade)>(items.Count);
        foreach (var tool in items.Select(i => i.Tool).Distinct(StringComparer.Ordinal).ToList())
        {
            var judge = new RelevanceJudge(llm, model, batchSize: 1, tool, rubric);
            foreach (var item in items.Where(i => i.Tool == tool))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var grades = await judge.GradeAsync(item.Query, [new JudgeCandidate(item.CandidateId, item.Candidate)], cancellationToken).ConfigureAwait(false);
                graded.Add((item, grades?.GetValueOrDefault(item.CandidateId)));
            }
        }

        return Stats(model, rubricName, RelevanceJudge.RubricId(rubric), graded, llm.Calls - before);
    }

    private static CalibrationStats Stats(string judge, string rubric, string? rubricId, IReadOnlyList<(KappaItem Item, int? Grade)> graded, int calls)
    {
        var valid = graded.Where(g => g.Grade is not null).Select(g => (g.Item.Tool, Judge: g.Grade!.Value, Owner: g.Item.HumanGrade!.Value)).ToList();
        double? Kappa(IEnumerable<(string Tool, int Judge, int Owner)> pairs)
        {
            var list = pairs.ToList();
            return list.Count < 2 ? null : EvalStatistics.CohensKappa(list.Select(p => p.Judge).ToList(), list.Select(p => p.Owner).ToList());
        }

        var judgeGrades = valid.Select(p => p.Judge).ToList();
        var ownerGrades = valid.Select(p => p.Owner).ToList();
        return new CalibrationStats(
            judge,
            rubric,
            rubricId,
            valid.Count,
            graded.Count - valid.Count,
            calls,
            Kappa(valid),
            valid.Count < 2 ? null : AgreementStatistics.WeightedKappa(judgeGrades, ownerGrades, KappaWeights.Linear),
            valid.Count < 2 ? null : AgreementStatistics.WeightedKappa(judgeGrades, ownerGrades, KappaWeights.Quadratic),
            valid.Count == 0 ? null : valid.Count(p => p.Judge == p.Owner) / (double)valid.Count,
            Kappa(valid.Where(p => p.Tool == GradingService.Tools[0])),
            Kappa(valid.Where(p => p.Tool == GradingService.Tools[1])),
            valid.Count(p => p.Judge > p.Owner),
            valid.Count(p => p.Owner > p.Judge),
            AgreementStatistics.Confusion(judgeGrades, ownerGrades));
    }

    private static string Table(CalibrationStats baseline, IReadOnlyList<CalibrationStats> ranked)
    {
        static string K(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-";
        static string Row(CalibrationStats s) => string.Create(CultureInfo.InvariantCulture,
            $"{s.Judge,-26} {s.Rubric,-12} {K(s.Kappa),7} {K(s.LinearKappa),7} {K(s.QuadraticKappa),7} {(s.Agreement is { } a ? (a * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "-"),7} {K(s.HistoryKappa),7} {K(s.MemoryKappa),7} {s.Failed,6} {s.Calls,6}");
        var rows = new[] { baseline }.Concat(ranked).ToList();
        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{"judge",-26} {"rubric",-12} {"κ",7} {"κ lin",7} {"κ quad",7} {"agree",7} {"hist κ",7} {"mem κ",7} {"failed",6} {"calls",6}"));
        foreach (var row in rows)
        {
            text.AppendLine(Row(row));
        }

        text.AppendLine();
        text.AppendLine("Confusion (rows: judge 0/1/2, columns: you 0/1/2):");
        foreach (var row in rows)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{row.Judge} / {row.Rubric}: {string.Join(" ", row.Confusion.Select(r => "[" + string.Join(" ", r) + "]"))}; judge higher {row.JudgeHigher}, you higher {row.OwnerHigher}"));
        }

        return text.ToString();
    }

    private static async Task<string> WriteReportAsync(
        CalibrationArchive archive,
        TimeProvider time,
        IReadOnlyList<KappaItem> items,
        IReadOnlyList<string> models,
        IReadOnlyList<(string Name, string Text)> rubrics,
        CalibrationStats baseline,
        IReadOnlyList<CalibrationStats> ranked,
        string? stopped,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(archive.ReportsDirectory);
        var now = time.GetUtcNow();
        var stem = string.Create(CultureInfo.InvariantCulture, $"calibration-{now:yyyyMMdd-HHmmss}");
        var path = Path.Combine(archive.ReportsDirectory, stem + ".json");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(archive.ReportsDirectory, string.Create(CultureInfo.InvariantCulture, $"{stem}-{i}.json"));
        }

        var report = new
        {
            createdAt = now,
            items = items.Count,
            historyItems = Count(items, GradingService.Tools[0]),
            memoryItems = Count(items, GradingService.Tools[1]),
            models,
            rubrics = rubrics.Select(r => new { name = r.Name, id = RelevanceJudge.RubricId(r.Text) }).ToList(),
            stopped,
            baseline,
            results = ranked,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, ReportJson), cancellationToken).ConfigureAwait(false);
        return path;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,63}\z")]
    private static partial Regex ModelPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}\z")]
    private static partial Regex RubricNamePattern();
}
