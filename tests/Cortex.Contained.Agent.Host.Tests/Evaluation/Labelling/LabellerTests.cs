using System.Globalization;
using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed partial class LabellerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"labeller-{Guid.NewGuid():N}");
    private readonly FakeLabellingLlm fake = new();
    private readonly FakeSource source = new();
    private readonly StringWriter output = new();
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());

    public LabellerTests()
    {
        Directory.CreateDirectory(this.root);
        this.fake.Respond = Respond;
    }

    public void Dispose()
    {
        this.output.Dispose();
        Directory.Delete(this.root, recursive: true);
    }

    // Writer: a fresh query per seed; main model: a search call; judge: grade 2 for the item named in the query, else 0.
    private static string Respond(string model, string system, string user) => model switch
    {
        "gpt-6.1-sol" when user.Contains("Request (", StringComparison.Ordinal) => """{"text":"zqquery about item 4","category":"lookup","goldFilters":{"until":"2027-01-01T00:00:00Z"}}""",
        "gpt-6.1-sol" => $"{{\"text\":\"zqquery about item {Item().Match(user).Groups[1].Value}\",\"category\":\"lookup\"}}",
        "main-model" => """{"query":"zqsearch"}""",
        _ => "{\"grades\":{" + string.Join(',', user.Split('\n').Where(l => l.StartsWith("[c", StringComparison.Ordinal)).Select(l =>
            $"\"{l[1..l.IndexOf(']', StringComparison.Ordinal)]}\":{(l.Contains($"zqitem {Target(user)} text", StringComparison.Ordinal) ? 2 : 0)}")) + "}}",
    };

    private static string Target(string user) => QueryItem().Match(user).Groups[1].Value;

    [GeneratedRegex(@"zqitem (\d+) ")]
    private static partial Regex Item();

    [GeneratedRegex(@"Query: zqquery about item (\d+)")]
    private static partial Regex QueryItem();

    private Labeller Create(LabellingOptions options) =>
        new(this.source, new LabellingLlm(this.fake, FakeLabellingLlm.ServesAll, options, new LabellingStateStore(this.root, this.protection).Load()?.Calls ?? 0, (_, _) => Task.CompletedTask),
            options, "main-model", this.root, this.protection, this.output);

    private static LabellingOptions Options(int maxCalls = 1000) =>
        new() { MinCallIntervalMs = 0, MaxCalls = maxCalls, PoolDepth = 3, JudgeBatchSize = 2, MaxCandidatesPerQuery = 6, RealShare = 0.2 };

    [Fact]
    public async Task RunAsync_Pipeline_WritesSealedDatasetKappaSampleAndCountsOnlyOutput()
    {
        var code = await this.Create(Options()).RunAsync(5, kappaItems: 4, CancellationToken.None);

        Assert.Equal(0, code);
        var written = this.source.Written!;
        Assert.Equal(5, written.Count);
        Assert.Equal("real", written[0].Draft.Stratum);
        Assert.Equal(T0, written[0].Draft.GoldFilters!.Until);
        Assert.Equal("zqrecorded", written[0].Draft.ModelText);
        Assert.All(written.Skip(1), q => Assert.Equal("zqsearch", q.Draft.ModelText));
        Assert.All(written, q => Assert.Single(q.Relevant));
        Assert.Equal(2, written[1].Grades[written[1].Draft.GoldSourceIds[0]]);
        Assert.Equal(4, this.fake.Requests.Count(r => r.Model == "main-model"));

        var sample = KappaSampleFile.Read(this.root, this.protection);
        Assert.Equal(4, sample.Count);
        Assert.All(sample, s => Assert.Null(s.HumanGrade));
        Assert.True(EvalProtection.IsSealed(Path.Combine(this.root, KappaSampleFile.FileName)));
        Assert.True(EvalProtection.IsSealed(Path.Combine(this.root, LabellingStateStore.FileName)));
        Assert.DoesNotContain("zq", this.output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("zq", await File.ReadAllTextAsync(Path.Combine(this.root, Labeller.ReportFileName)), StringComparison.Ordinal);
        Assert.Contains("Estimate for 5 history-eval queries", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CapReached_StopsAndTheNextRunContinuesWithoutRedrafting()
    {
        this.fake.Respond = (model, system, user) => model == "claude-opus-5.5" ? "{}" : Respond(model, system, user);
        Assert.Equal(3, await this.Create(Options(maxCalls: 30)).RunAsync(5, 4, CancellationToken.None));
        Assert.Contains("raise the cap", this.output.ToString(), StringComparison.Ordinal);
        Assert.Null(this.source.Written);

        this.fake.Requests.Clear();
        this.fake.Respond = Respond;
        Assert.Equal(0, await this.Create(Options()).RunAsync(5, 4, CancellationToken.None));

        Assert.Equal(5, this.source.Written!.Count);
        Assert.DoesNotContain(this.fake.Requests, r => r.Model == "gpt-6.1-sol");
    }

    [Fact]
    public async Task RunAsync_Judging_GoldSourcePositionVariesAcrossQueries()
    {
        Assert.Equal(0, await this.Create(Options()).RunAsync(9, kappaItems: 4, CancellationToken.None));

        var positions = this.fake.Requests
            .Where(r => r.Model == "claude-opus-5.5")
            .Select(r => (Query: QueryItem().Match(r.User).Groups[1].Value, Items: r.User.Split('\n').Where(l => l.StartsWith("[c", StringComparison.Ordinal)).ToList()))
            .GroupBy(r => r.Query)
            .Where(g => g.Key != "4")
            .Select(g => g.SelectMany(r => r.Items).ToList().FindIndex(l => l.Contains($"zqitem {g.Key} text", StringComparison.Ordinal)))
            .ToList();

        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.True(positions.Distinct().Count() > 1, "the gold source is always at the same position");
    }

    [Fact]
    public async Task RunAsync_RealRequest_NeverPoolsOrJudgesTheOwnersRequestMessage()
    {
        Assert.Equal(0, await this.Create(Options()).RunAsync(5, kappaItems: 4, CancellationToken.None));

        var written = this.source.Written!;
        Assert.Equal("real", written[0].Draft.Stratum);
        Assert.DoesNotContain("99", written[0].Grades.Keys);
        Assert.All(written.Skip(1), q => Assert.Contains("99", q.Grades.Keys));
    }

    [Fact]
    public async Task RunAsync_WriterProviderDown_StopsResumableWithoutMarkingSeedsFailed()
    {
        this.fake.Respond = (model, system, user) => model == "gpt-6.1-sol" ? null : Respond(model, system, user);

        var code = await this.Create(Options()).RunAsync(5, kappaItems: 4, CancellationToken.None);

        Assert.Equal(4, code);
        Assert.Null(this.source.Written);
        var state = new LabellingStateStore(this.root, this.protection).Load()!;
        Assert.Empty(state.FailedSeeds);
        Assert.Empty(state.Drafts);
        Assert.Contains("provider", this.output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zq", this.output.ToString(), StringComparison.Ordinal);

        this.fake.Respond = Respond;
        Assert.Equal(0, await this.Create(Options()).RunAsync(5, 4, CancellationToken.None));
        Assert.Equal(5, this.source.Written!.Count);
    }

    [Fact]
    public async Task RunAsync_JudgeProviderDown_StopsResumableAndTheNextRunJudgesThoseQueries()
    {
        this.fake.Respond = (model, system, user) => model == "claude-opus-5.5" ? null : Respond(model, system, user);

        Assert.Equal(4, await this.Create(Options()).RunAsync(5, kappaItems: 4, CancellationToken.None));

        Assert.Null(this.source.Written);
        var state = new LabellingStateStore(this.root, this.protection).Load()!;
        Assert.Equal(5, state.Drafts.Count);
        Assert.Empty(state.FailedJudging);

        this.fake.Respond = Respond;
        Assert.Equal(0, await this.Create(Options()).RunAsync(5, 4, CancellationToken.None));
        Assert.Equal(5, this.source.Written!.Count);
    }

    [Fact]
    public async Task RunAsync_JudgeGivesNoValidAnswerForAQuery_RefusesToFinishUntilTheRunsThatRetryItAreUsedUp()
    {
        this.fake.Respond = (model, system, user) =>
            model == "claude-opus-5.5" && user.Contains("Query: zqquery about item 3", StringComparison.Ordinal) ? "{}" : Respond(model, system, user);

        Assert.Equal(5, await this.Create(Options()).RunAsync(5, kappaItems: 4, CancellationToken.None));
        Assert.Null(this.source.Written);
        Assert.False(File.Exists(Path.Combine(this.root, Labeller.ReportFileName)));
        Assert.Contains("run the job again", this.output.ToString(), StringComparison.Ordinal);

        Assert.Equal(5, await this.Create(Options()).RunAsync(5, 4, CancellationToken.None));
        Assert.Equal(0, await this.Create(Options()).RunAsync(5, 4, CancellationToken.None));

        Assert.Equal(4, this.source.Written!.Count);
        Assert.Contains("\"judgingFailed\": 1", await File.ReadAllTextAsync(Path.Combine(this.root, Labeller.ReportFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_JudgeRecoversOnTheNextRun_FinishesWithEveryQuery()
    {
        this.fake.Respond = (model, system, user) =>
            model == "claude-opus-5.5" && user.Contains("Query: zqquery about item 3", StringComparison.Ordinal) ? "{}" : Respond(model, system, user);
        Assert.Equal(5, await this.Create(Options()).RunAsync(5, kappaItems: 4, CancellationToken.None));

        this.fake.Respond = Respond;
        Assert.Equal(0, await this.Create(Options()).RunAsync(5, 4, CancellationToken.None));

        Assert.Equal(5, this.source.Written!.Count);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("judge")]
    [InlineData("main")]
    [InlineData("rubric")]
    public async Task RunAsync_SavedProgressMadeWithOtherModels_RefusesWithoutCalls(string changed)
    {
        this.fake.Respond = (model, system, user) => model == "claude-opus-5.5" ? "{}" : Respond(model, system, user);
        Assert.Equal(3, await this.Create(Options(maxCalls: 30)).RunAsync(5, 4, CancellationToken.None));
        this.fake.Requests.Clear();
        this.fake.Respond = Respond;
        var options = Options();
        var main = "main-model";
        switch (changed)
        {
            case "query":
                options.QueryModel = "gpt-7.0-sol";
                break;
            case "judge":
                options.JudgeModel = "gemini-4-pro";
                break;
            case "rubric":
                options.JudgeRubric = "Grade 2 only for an exact answer.";
                break;
            default:
                main = "other-main-model";
                break;
        }

        var llm = new LabellingLlm(this.fake, FakeLabellingLlm.ServesAll, options, 0, (_, _) => Task.CompletedTask);
        var code = await new Labeller(this.source, llm, options, main, this.root, this.protection, this.output).RunAsync(5, 4, CancellationToken.None);

        Assert.Equal(2, code);
        Assert.Empty(this.fake.Requests);
        Assert.Contains("fresh run", this.output.ToString(), StringComparison.Ordinal);
        Assert.Contains(changed switch
        {
            "query" => "gpt-7.0-sol",
            "judge" => "gemini-4-pro",
            "rubric" => RelevanceJudge.RubricId(options.JudgeRubric),
            _ => "other-main-model",
        }, this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SavedProgressWithoutARubric_ResumesWithTheDefaultAndRefusesAnother()
    {
        this.fake.Respond = (model, system, user) => model == "claude-opus-5.5" ? "{}" : Respond(model, system, user);
        Assert.Equal(3, await this.Create(Options(maxCalls: 30)).RunAsync(5, 4, CancellationToken.None));
        var store = new LabellingStateStore(this.root, this.protection);
        var saved = store.Load()!;
        Assert.Equal("default", saved.JudgeRubric);
        saved.JudgeRubric = string.Empty;
        store.Save(saved);
        this.fake.Respond = Respond;

        var custom = Options();
        custom.JudgeRubric = "Grade 2 only for an exact answer.";
        Assert.Equal(2, await this.Create(custom).RunAsync(5, 4, CancellationToken.None));
        Assert.Equal(0, await this.Create(Options()).RunAsync(5, 4, CancellationToken.None));
        Assert.Contains("\"judgeRubric\": \"default\"", await File.ReadAllTextAsync(Path.Combine(this.root, Labeller.ReportFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CustomRubric_JudgesWithItAndRecordsItsId()
    {
        var options = Options();
        options.JudgeRubric = "Grade 2 only for an exact answer, 1 for zqpartial context, 0 otherwise.";

        Assert.Equal(0, await this.Create(options).RunAsync(5, kappaItems: 4, CancellationToken.None));

        Assert.All(this.fake.Requests.Where(r => r.Model == "claude-opus-5.5"), r => Assert.Contains("Grade 2 only for an exact answer", r.System, StringComparison.Ordinal));
        Assert.Equal(RelevanceJudge.RubricId(options.JudgeRubric), new LabellingStateStore(this.root, this.protection).Load()!.JudgeRubric);
        Assert.Contains($"\"judgeRubric\": \"{RelevanceJudge.RubricId(options.JudgeRubric)}\"", await File.ReadAllTextAsync(Path.Combine(this.root, Labeller.ReportFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FewerQueriesSurviveThanTheMinimum_SamplesExtraSeedsUntilItIsMet()
    {
        this.source.MinimumQueries = 6;
        this.fake.Respond = (model, system, user) =>
            model == "claude-opus-5.5" && user.Contains("Query: zqquery about item 3", StringComparison.Ordinal)
                ? "{\"grades\":{" + string.Join(',', user.Split('\n').Where(l => l.StartsWith("[c", StringComparison.Ordinal)).Select(l => $"\"{l[1..l.IndexOf(']', StringComparison.Ordinal)]}\":0")) + "}}"
                : Respond(model, system, user);

        var code = await this.Create(Options()).RunAsync(5, kappaItems: 4, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.True(this.source.Written!.Count >= 6, $"{this.source.Written.Count} queries");
        Assert.DoesNotContain(this.source.Written, q => q.Draft.Text.Contains("item 3", StringComparison.Ordinal));
        Assert.Equal(this.source.Written.Count, this.source.Written.Select(q => q.Draft.Id).Distinct().Count());
        Assert.Contains("extra seeds", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MinimumCannotBeMetWithinTheTopUpRounds_StopsResumableWithoutWriting()
    {
        this.source.MinimumQueries = 40;

        var code = await this.Create(Options()).RunAsync(5, kappaItems: 4, CancellationToken.None);

        Assert.Equal(6, code);
        Assert.Null(this.source.Written);
        Assert.False(File.Exists(Path.Combine(this.root, Labeller.ReportFileName)));
        Assert.Contains("at least 40", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_EstimateAboveTheCap_RefusedWithoutCalls()
    {
        Assert.Equal(2, await this.Create(Options(maxCalls: 5)).RunAsync(5, 4, CancellationToken.None));

        Assert.Empty(this.fake.Requests);
        Assert.Contains("MaxCalls", this.output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Ten items; seeds: one real request and lookups of items 1, 2, 3, ….</summary>
    private sealed class FakeSource : ILabelSource
    {
        private readonly Dictionary<string, MaterialItem> items = Enumerable.Range(1, 10).Append(99)
            .ToDictionary(
                i => i.ToString(CultureInfo.InvariantCulture),
                i => new MaterialItem(i.ToString(CultureInfo.InvariantCulture), $"zqitem {i} text", "ch", T0.AddHours(i - 20), "user"));

        public string Tool => "history-eval";

        public int MinimumQueries { get; set; }

        public IReadOnlyList<LabelledQuery>? Written { get; private set; }

        public Task<IReadOnlyList<LabelSeed>> SampleAsync(int count, double realShare, CancellationToken cancellationToken)
        {
            var real = new LabelSeed { SeedId = "s0001", Category = "real", Stratum = "real", RequestMessageId = "99", RequestTime = T0, ModelArgs = """{"query":"zqrecorded"}""" };
            var synthetic = Enumerable.Range(2, Math.Min(count, 11) - 1)
                .Select(i => new LabelSeed { SeedId = $"s{i:0000}", Category = "lookup", SourceIds = [(i - 1).ToString(CultureInfo.InvariantCulture)] });
            return Task.FromResult<IReadOnlyList<LabelSeed>>([real, .. synthetic]);
        }

        public Task<SeedMaterial?> MaterialAsync(LabelSeed seed, CancellationToken cancellationToken) =>
            Task.FromResult<SeedMaterial?>(seed.Stratum == "real"
                ? new SeedMaterial([], "zqwhat about item 4")
                : new SeedMaterial(seed.SourceIds.Select(id => this.items[id]).ToList()));

        public Task<IReadOnlyList<string>> PoolAsync(DraftedQuery query, int depth, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["4", "5", "6", "7", "99"]);

        public Task<IReadOnlyDictionary<string, MaterialItem>> ReadAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, MaterialItem>>(ids.Where(this.items.ContainsKey).ToDictionary(id => id, id => this.items[id]));

        public bool ParaphraseOk(string text, IReadOnlyList<string> goldTexts) => true;

        public void WriteDataset(IReadOnlyList<LabelledQuery> queries, string queryFamily, string judgeFamily)
        {
            Assert.Equal(("gpt", "claude"), (queryFamily, judgeFamily));
            this.Written = queries;
        }
    }
}
