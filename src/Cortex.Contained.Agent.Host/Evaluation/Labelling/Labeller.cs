using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// What the labelling pipeline needs from a corpus (a private copy of the history or the memory store): seeds,
/// their material, the pooled candidates of every system in the ablation grid, texts by id, the paraphrase guard and
/// the dataset writer. <c>HistoryLabelSource</c> and <c>MemoryLabelSource</c> implement it; tests use a fake.
/// </summary>
public interface ILabelSource
{
    /// <summary><c>history-eval</c> or <c>memory-eval</c>.</summary>
    string Tool { get; }

    /// <summary>Stratified seeds, real requests included (<see cref="LabelSeeds"/>).</summary>
    Task<IReadOnlyList<LabelSeed>> SampleAsync(int count, double realShare, CancellationToken cancellationToken);

    /// <summary>The snippet or request of <paramref name="seed"/>; null when its sources no longer exist.</summary>
    Task<SeedMaterial?> MaterialAsync(LabelSeed seed, CancellationToken cancellationToken);

    /// <summary>The union of the top <paramref name="depth"/> of every pooled configuration (best ranks first), nothing at or after <see cref="DraftedQuery.NotAfter"/>.</summary>
    Task<IReadOnlyList<string>> PoolAsync(DraftedQuery query, int depth, CancellationToken cancellationToken);

    /// <summary>Items by id (missing ids are left out).</summary>
    Task<IReadOnlyDictionary<string, MaterialItem>> ReadAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken);

    /// <summary>The paraphrase guard of §4.2 (Jaccard ≤ 0.2, no shared rare token) against <paramref name="goldTexts"/>.</summary>
    bool ParaphraseOk(string text, IReadOnlyList<string> goldTexts);

    /// <summary>Writes the sealed <c>dataset.jsonl</c> of the workspace.</summary>
    void WriteDataset(IReadOnlyList<LabelledQuery> queries, string queryFamily, string judgeFamily);
}

/// <summary>Expected LLM calls and duration of a label job (printed before it starts; the cap is enforced).</summary>
public sealed record LabelEstimate(int WriterCalls, int MainCalls, int JudgeCalls, double Hours)
{
    public int Total => this.WriterCalls + this.MainCalls + this.JudgeCalls;
}

/// <summary>
/// The <c>label</c> job (§4.2 automated): seeds → query writer (paraphrase guard and copy check, rewritten on failure)
/// → the main model's version → candidate pool (union of the grid's top-k plus the gold source: judged@10) → batched
/// judge → pre-declared finalisation → sealed <c>dataset.jsonl</c>, the owner's κ sample and a counts-only report. Every
/// stage checkpoints to the sealed <see cref="LabellingState"/>, so a crash, a cancel or the call cap resumes where it
/// stopped. Output holds ids and numbers only.
/// </summary>
public sealed class Labeller
{
    /// <summary>Report file (counts only).</summary>
    public const string ReportFileName = "label-report.json";

    // Seconds per call assumed by the estimate (measured order of magnitude for large models), and candidates per query.
    private const double WriterSeconds = 5;
    private const double MainSeconds = 4;
    private const double JudgeSeconds = 15;
    private const int AssumedPoolSize = 30;
    private const int CheckpointEvery = 5;

    // Candidates reach the judge in a seeded hash order, so the gold source is not always the first of the first batch.
    private const int JudgeOrderSeed = 20261122;

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILabelSource source;
    private readonly LabellingLlm llm;
    private readonly LabellingOptions options;
    private readonly string mainModel;
    private readonly LabellingStateStore store;
    private readonly string workspace;
    private readonly EvalProtection protection;
    private readonly TextWriter output;
    private readonly TimeProvider time;

    public Labeller(
        ILabelSource source,
        LabellingLlm llm,
        LabellingOptions options,
        string mainModel,
        string workspace,
        EvalProtection protection,
        TextWriter output,
        TimeProvider? time = null)
    {
        this.source = source;
        this.llm = llm;
        this.options = options;
        this.mainModel = mainModel;
        this.workspace = workspace;
        this.protection = protection;
        this.output = output;
        this.time = time ?? TimeProvider.System;
        this.store = new LabellingStateStore(workspace, protection);
    }

    /// <summary>Calls and hours for <paramref name="queries"/> queries (real requests need no main-model call).</summary>
    public static LabelEstimate Estimate(int queries, LabellingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var real = (int)Math.Floor(queries * options.RealShare);
        var writer = (int)Math.Ceiling(queries * 1.3);
        var main = queries - real;
        var judge = (int)Math.Ceiling(queries * Math.Ceiling(Math.Min(options.MaxCandidatesPerQuery, AssumedPoolSize) / (double)options.JudgeBatchSize) * 1.1);
        var seconds = (writer * WriterSeconds) + (main * MainSeconds) + (judge * JudgeSeconds) + ((writer + main + judge) * options.MinCallIntervalMs / 1000.0);
        return new LabelEstimate(writer, main, judge, Math.Round(seconds / 3600, 1));
    }

    /// <summary>Runs (or resumes) the job: 0 done, 2 refused, 3 stopped at the call cap (run again to continue).</summary>
    public async Task<int> RunAsync(int queries, int kappaItems, CancellationToken cancellationToken)
    {
        var estimate = Estimate(queries, this.options);
        await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Estimate for {queries} {this.source.Tool} queries: ~{estimate.Total} LLM calls (writer {estimate.WriterCalls} on {this.options.QueryModel}, main model {estimate.MainCalls} on {this.mainModel}, judge {estimate.JudgeCalls} on {this.options.JudgeModel}), ~{estimate.Hours} h; cap {this.options.MaxCalls} calls.")).ConfigureAwait(false);
        if (estimate.Total > this.options.MaxCalls)
        {
            await this.output.WriteLineAsync("error: the estimate exceeds Evaluation:Labelling:MaxCalls; raise the cap or lower the query counts.").ConfigureAwait(false);
            return 2;
        }

        this.llm.EnsureServed(this.options.QueryModel, this.options.JudgeModel, this.mainModel);
        var state = this.store.Load() is { } saved && saved.Tool == this.source.Tool ? saved : new LabellingState { Tool = this.source.Tool };

        // A judge failure may have been transient (provider outage): a new run grades those queries again.
        state.FailedJudging.Clear();
        try
        {
            if (state.Seeds.Count == 0)
            {
                state.Seeds = [.. await this.source.SampleAsync(queries, this.options.RealShare, cancellationToken).ConfigureAwait(false)];
                this.store.Save(state);
            }

            await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{state.Seeds.Count} seeds ({state.Seeds.Count(s => s.Stratum == "real")} real requests); resuming with {state.Drafts.Count} drafted, {state.Pools.Count} pooled, {state.Grades.Count} judged.")).ConfigureAwait(false);
            await this.DraftAsync(state, cancellationToken).ConfigureAwait(false);
            await this.PoolAsync(state, cancellationToken).ConfigureAwait(false);
            await this.JudgeAsync(state, cancellationToken).ConfigureAwait(false);
            await this.FinishAsync(state, kappaItems, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (LabellingBudgetExceededException ex)
        {
            await this.output.WriteLineAsync(ex.Message + " Progress is saved: raise the cap and run the job again to continue.").ConfigureAwait(false);
            return 3;
        }
        finally
        {
            state.Calls = this.llm.Calls;
            this.store.Save(state);
        }
    }

    /// <summary>A candidate as the judge and the owner see it.</summary>
    public static string Render(MaterialItem item, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(item);
        var text = item.Text.Length <= maxChars ? item.Text : string.Concat(item.Text.AsSpan(0, maxChars), "…");
        return item.Time is { } time
            ? string.Create(CultureInfo.InvariantCulture, $"{time:yyyy-MM-dd HH:mm} UTC · {item.Channel} · {item.Role}: {text}")
            : $"title: {item.Title ?? "-"} · tags: {(item.Tags.Count == 0 ? "-" : string.Join(", ", item.Tags))}\n{text}";
    }

    // A real request's oracle filters never reach past the moment it was asked.
    private static EvalFilters? Clamp(EvalFilters? filters, DateTimeOffset? requestTime) =>
        requestTime is not { } asked ? filters
        : (filters ?? new EvalFilters()) with { Until = filters?.Until is { } until && until < asked ? until : asked };

    private async Task DraftAsync(LabellingState state, CancellationToken cancellationToken)
    {
        var drafter = new QueryDrafter(this.llm, this.options.QueryModel, this.source.Tool, this.options.MaxDraftAttempts, this.options.MaxSnippetChars, this.time);
        var writer = new MainModelQueryWriter(this.llm, this.mainModel, this.time);
        var prefix = this.source.Tool == "memory-eval" ? "m" : "h";
        var pending = 0;
        foreach (var seed in state.Seeds.Where(s => !state.Drafts.ContainsKey(s.SeedId) && !state.FailedSeeds.Contains(s.SeedId)).ToList())
        {
            var material = await this.source.MaterialAsync(seed, cancellationToken).ConfigureAwait(false);
            var answer = material is null ? null : await drafter.DraftAsync(seed, material, this.source.ParaphraseOk, cancellationToken).ConfigureAwait(false);
            if (answer is null)
            {
                state.FailedSeeds.Add(seed.SeedId);
            }
            else
            {
                var model = seed.Stratum == "real"
                    ? MainModelQueryWriter.FromArgs(seed.ModelArgs)
                    : await writer.WriteAsync(this.source.Tool, answer.Text!, cancellationToken).ConfigureAwait(false);
                state.Drafts[seed.SeedId] = new DraftedQuery
                {
                    Id = prefix + seed.SeedId[1..],
                    SeedId = seed.SeedId,
                    Text = answer.Text!,
                    Category = answer.Category!,
                    Stratum = seed.Stratum,
                    GoldSourceIds = seed.SourceIsGold ? seed.SourceIds : [],
                    GoldFilters = Clamp(answer.GoldFilters, seed.RequestTime),
                    Tags = answer.Tags,
                    ModelText = model?.Text,
                    ModelFilters = model?.Filters,
                    ModelTags = model?.Tags,
                    NotAfter = seed.RequestTime,
                };
            }

            if (++pending % CheckpointEvery == 0)
            {
                state.Calls = this.llm.Calls;
                this.store.Save(state);
                await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"drafted {state.Drafts.Count} / {state.Seeds.Count} ({state.FailedSeeds.Count} given up), {this.llm.Calls} calls")).ConfigureAwait(false);
            }
        }

        this.store.Save(state);
    }

    private async Task PoolAsync(LabellingState state, CancellationToken cancellationToken)
    {
        foreach (var draft in state.Drafts.Values.Where(d => !state.Pools.ContainsKey(d.Id)).ToList())
        {
            var pooled = await this.source.PoolAsync(draft, this.options.PoolDepth, cancellationToken).ConfigureAwait(false);
            state.Pools[draft.Id] = draft.GoldSourceIds.Concat(pooled).Distinct(StringComparer.Ordinal).Take(this.options.MaxCandidatesPerQuery).ToList();
        }

        this.store.Save(state);
        await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"pooled {state.Pools.Count} queries, {state.Pools.Values.Sum(p => p.Count)} candidates (depth {this.options.PoolDepth})")).ConfigureAwait(false);
    }

    private async Task JudgeAsync(LabellingState state, CancellationToken cancellationToken)
    {
        var judge = new RelevanceJudge(this.llm, this.options.JudgeModel, this.options.JudgeBatchSize, this.source.Tool);
        var done = 0;
        foreach (var draft in state.Drafts.Values.Where(d => !state.Grades.ContainsKey(d.Id) && !state.FailedJudging.Contains(d.Id)).ToList())
        {
            var pool = state.Pools[draft.Id];
            var items = await this.source.ReadAsync(pool, cancellationToken).ConfigureAwait(false);
            var candidates = pool.Where(items.ContainsKey)
                .OrderBy(id => SeededHash.Rank(JudgeOrderSeed, draft.Id + "|" + id), StringComparer.Ordinal)
                .Select(id => new JudgeCandidate(id, Render(items[id], this.options.MaxSnippetChars))).ToList();
            var grades = await judge.GradeAsync(draft.Text, candidates, cancellationToken).ConfigureAwait(false);
            if (grades is null)
            {
                state.FailedJudging.Add(draft.Id);
            }
            else
            {
                state.Grades[draft.Id] = new Dictionary<string, int>(grades, StringComparer.Ordinal);
            }

            state.Calls = this.llm.Calls;
            this.store.Save(state);
            if (++done % 25 == 0)
            {
                await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"judged {state.Grades.Count} / {state.Drafts.Count} ({state.FailedJudging.Count} failed), {this.llm.Calls} calls")).ConfigureAwait(false);
            }
        }
    }

    private async Task FinishAsync(LabellingState state, int kappaItems, CancellationToken cancellationToken)
    {
        var judged = state.Seeds
            .Where(s => state.Drafts.ContainsKey(s.SeedId) && state.Grades.ContainsKey(state.Drafts[s.SeedId].Id))
            .Select(s => state.Drafts[s.SeedId])
            .Select(d => (Draft: d, Grades: (IReadOnlyDictionary<string, int>)state.Grades[d.Id]))
            .ToList();
        var goldTexts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (draft, grades) in judged.Where(j => j.Draft.Category == "paraphrase"))
        {
            var relevant = grades.Where(g => g.Value > 0).Select(g => g.Key).ToList();
            goldTexts[draft.Id] = (await this.source.ReadAsync(relevant, cancellationToken).ConfigureAwait(false)).Values.Select(i => i.GuardText).ToList();
        }

        var (queries, report) = DatasetFinaliser.Finalise(judged, (draft, _) => this.source.ParaphraseOk(draft.Text, goldTexts[draft.Id]));
        var queryFamily = ModelFamily.Of(this.options.QueryModel);
        var judgeFamily = ModelFamily.Of(this.options.JudgeModel);
        this.source.WriteDataset(queries, queryFamily, judgeFamily);

        var picks = KappaSampler.Select(queries, kappaItems);
        var texts = await this.source.ReadAsync(picks.Select(p => p.CandidateId).Distinct(StringComparer.Ordinal).ToList(), cancellationToken).ConfigureAwait(false);
        var byId = queries.ToDictionary(q => q.Draft.Id, StringComparer.Ordinal);
        var sample = picks
            .Where(p => texts.ContainsKey(p.CandidateId))
            .Select(p => new KappaItem
            {
                Tool = this.source.Tool,
                QueryId = p.QueryId,
                CandidateId = p.CandidateId,
                LlmGrade = p.LlmGrade,
                Query = byId[p.QueryId].Draft.Text,
                Candidate = Render(texts[p.CandidateId], this.options.MaxSnippetChars),
            })
            .ToList();
        KappaSampleFile.Write(this.workspace, this.protection, sample);

        var summary = new
        {
            tool = this.source.Tool,
            queryModel = this.options.QueryModel,
            judgeModel = this.options.JudgeModel,
            mainModel = this.mainModel,
            seeds = state.Seeds.Count,
            realSeeds = state.Seeds.Count(s => s.Stratum == "real"),
            drafted = state.Drafts.Count,
            draftsGivenUp = state.FailedSeeds.Count,
            judgingFailed = state.FailedJudging.Count,
            report.Judged,
            report.Kept,
            report.Dropped,
            report.Recategorised,
            categories = queries.GroupBy(q => q.Category, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            strata = queries.GroupBy(q => q.Draft.Stratum, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            candidatesJudged = queries.Sum(q => q.Grades.Count),
            kappaItems = sample.Count,
            calls = this.llm.Calls,
        };
        await File.WriteAllTextAsync(Path.Combine(this.workspace, ReportFileName), JsonSerializer.Serialize(summary, ReportJson), cancellationToken).ConfigureAwait(false);
        await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Labelled {queries.Count} {this.source.Tool} queries ({summary.strata.GetValueOrDefault("real")} real; {report.Dropped} dropped, {report.Recategorised.Values.Sum()} recategorised) with {summary.candidatesJudged} judgments; {sample.Count} items to grade by hand; {this.llm.Calls} LLM calls. Report: {Path.Combine(this.workspace, ReportFileName)}")).ConfigureAwait(false);
    }
}
