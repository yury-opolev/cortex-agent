using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Calibration;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Calibration;

/// <summary><c>judge-calibration calibrate</c> end to end: fake LLM, encrypted locked samples, a temporary calibration root.</summary>
public sealed partial class JudgeCalibrationCliTests : IDisposable
{
    private const string Secret = "zqsecret";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"judge-calibration-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
    private readonly FakeLabellingLlm fake = new();
    private readonly StringWriter output = new();
    private readonly Dictionary<string, string> workspaces;
    private readonly List<KappaItem> items = [];

    public JudgeCalibrationCliTests()
    {
        this.workspaces = new(StringComparer.Ordinal) { ["history-eval"] = Path.Combine(this.root, "hybrid"), ["memory-eval"] = Path.Combine(this.root, "memory") };
        foreach (var directory in this.workspaces.Values)
        {
            Directory.CreateDirectory(directory);
        }

        // The 75 items of the failed gate (κ 0.521): history first (50), then memory (25).
        var (judge, owner) = AgreementStatisticsTests.Pairs(AgreementStatisticsTests.Matrix);
        for (var i = 0; i < judge.Count; i++)
        {
            var tool = i < 50 ? "history-eval" : "memory-eval";
            this.items.Add(new KappaItem
            {
                Tool = tool,
                QueryId = (tool == "history-eval" ? "h" : "m") + i.ToString(CultureInfo.InvariantCulture),
                CandidateId = "c" + i.ToString(CultureInfo.InvariantCulture),
                LlmGrade = judge[i],
                HumanGrade = owner[i],
                Query = $"{Secret} query {i} about the ferry",
                Candidate = $"{Secret} candidate {i} text",
            });
        }

        this.fake.Respond = this.Respond;
    }

    public void Dispose()
    {
        this.output.Dispose();
        Directory.Delete(this.root, recursive: true);
    }

    private string CalibrationDirectory => Path.Combine(this.root, "eval", "calibration");

    [GeneratedRegex(@"Query: zqsecret query (\d+) ")]
    private static partial Regex QueryIndex();

    // judge-stored repeats the run's judge, judge-owner agrees with the owner; judge-flaky never answers item 0 validly.
    private string? Respond(string model, string system, string user)
    {
        var item = this.items[int.Parse(QueryIndex().Match(user).Groups[1].Value, CultureInfo.InvariantCulture)];
        return model switch
        {
            "judge-down" => null,
            "judge-flaky" when item.QueryId == "h0" => "{}",
            "judge-stored" => $"{{\"grades\":{{\"c1\":{item.LlmGrade}}}}}",
            _ => $"{{\"grades\":{{\"c1\":{item.HumanGrade}}}}}",
        };
    }

    private void LockWorkspaces()
    {
        foreach (var group in this.items.GroupBy(i => i.Tool))
        {
            KappaSampleFile.Write(this.workspaces[group.Key], this.protection, group.ToList());
            File.WriteAllLines(Path.Combine(this.workspaces[group.Key], CalibrationArchive.AgreementFileName), group.Select(i => $"{i.CandidateId},{i.LlmGrade},{i.HumanGrade}"));
        }
    }

    private CalibrationContext Context(LabellingOptions? options = null, Func<string, string?>? providerFor = null) => new(
        options ?? new LabellingOptions { MinCallIntervalMs = 0, MaxAttempts = 2 },
        this.fake,
        providerFor ?? FakeLabellingLlm.ServesAll,
        this.CalibrationDirectory,
        this.workspaces,
        TimeProvider.System,
        Delay: (_, _) => Task.CompletedTask);

    private Task<int> RunAsync(CalibrationContext context, params string[] args) =>
        JudgeCalibrationCli.RunAsync(new EvaluationRun(["calibrate", .. args], this.output, this.protection, null), context, CancellationToken.None);

    private string ReportText() => File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(this.CalibrationDirectory, "reports"), "calibration-*.json")));

    private static JsonElement Result(JsonElement report, string judge) =>
        report.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("judge").GetString() == judge);

    [Fact]
    public async Task Calibrate_KnownGrades_RanksTheCombosByKappaNextToTheStoredJudgeWithoutText()
    {
        this.LockWorkspaces();

        var code = await this.RunAsync(this.Context(), "--models", "judge-stored,judge-owner,judge-flaky");

        Assert.Equal(0, code);
        Assert.Equal(75 * 3 + 1, this.fake.Requests.Count);
        Assert.All(this.fake.Requests, r => Assert.Single(Regex.Matches(r.User, @"\[c\d+\]")));
        var text = this.output.ToString();
        Assert.Contains("Calibration set: 75 items (50 history, 25 memory)", text, StringComparison.Ordinal);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var baseline = lines.First(l => l.StartsWith(JudgeCalibrationCli.Baseline, StringComparison.Ordinal));
        Assert.Contains("0.521", baseline, StringComparison.Ordinal);
        Assert.Contains("0.618", baseline, StringComparison.Ordinal);
        Assert.Contains("0.712", baseline, StringComparison.Ordinal);
        Assert.Contains("68.0%", baseline, StringComparison.Ordinal);
        var ranked = lines.Where(l => l.StartsWith("judge-", StringComparison.Ordinal) && l.Contains(" default ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["judge-flaky", "judge-owner", "judge-stored"], ranked.Select(l => l.Split(' ')[0]).Order(StringComparer.Ordinal));
        Assert.Contains("1.000", ranked[0], StringComparison.Ordinal);
        Assert.Contains("1.000", ranked[1], StringComparison.Ordinal);
        Assert.StartsWith("judge-stored", ranked[^1], StringComparison.Ordinal);
        Assert.Contains("0.521", ranked[^1], StringComparison.Ordinal);
        Assert.Contains("judge-stored / default: [19 5 2] [6 12 7] [0 4 20]; judge higher 10, you higher 14", text, StringComparison.Ordinal);

        var reportText = this.ReportText();
        using var report = JsonDocument.Parse(reportText);
        Assert.Equal(0.521, Result(report.RootElement, "judge-stored").GetProperty("kappa").GetDouble(), 3);
        Assert.Equal(0.618, Result(report.RootElement, "judge-stored").GetProperty("linearKappa").GetDouble(), 3);
        Assert.Equal((74, 1), (Result(report.RootElement, "judge-flaky").GetProperty("items").GetInt32(), Result(report.RootElement, "judge-flaky").GetProperty("failed").GetInt32()));
        Assert.Equal("default", report.RootElement.GetProperty("rubrics")[0].GetProperty("id").GetString());
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, reportText, StringComparison.Ordinal);
        Assert.DoesNotContain("ferry", reportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calibrate_RubricFile_JudgesWithItAndReportsItsId()
    {
        this.LockWorkspaces();
        Directory.CreateDirectory(Path.Combine(this.CalibrationDirectory, "rubrics"));
        const string Rubric = "Grade 2 only when the item states the asked fact; 1 when it is a needed step; else 0.";
        await File.WriteAllTextAsync(Path.Combine(this.CalibrationDirectory, "rubrics", "v2.txt"), Rubric);

        Assert.Equal(0, await this.RunAsync(this.Context(), "--models", "judge-owner", "--rubrics", "default,v2"));

        Assert.Equal(75, this.fake.Requests.Count(r => r.System.Contains(Rubric, StringComparison.Ordinal)));
        Assert.Equal(75, this.fake.Requests.Count(r => r.System.Contains(RelevanceJudge.DefaultRubric, StringComparison.Ordinal)));
        Assert.Contains(RelevanceJudge.RubricId(Rubric), this.ReportText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Rubric, this.output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../evil", null)]
    [InlineData("Upper", null)]
    [InlineData("missing", null)]
    [InlineData("blank", "  \n ")]
    [InlineData("huge", "x")]
    public async Task Calibrate_BadRubric_RefusedWithoutCalls(string name, string? content)
    {
        this.LockWorkspaces();
        Directory.CreateDirectory(Path.Combine(this.CalibrationDirectory, "rubrics"));
        if (content is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(this.CalibrationDirectory, "rubrics", name + ".txt"), name == "huge" ? new string('x', JudgeCalibrationCli.MaxRubricBytes + 1) : content);
        }

        Assert.Equal(2, await this.RunAsync(this.Context(), "--models", "judge-owner", "--rubrics", name));

        Assert.StartsWith("error: ", this.output.ToString(), StringComparison.Ordinal);
        Assert.Empty(this.fake.Requests);
    }

    [Theory]
    [InlineData("a,b,c,d,e,f,g")]
    [InlineData("-bad")]
    [InlineData("")]
    public async Task Calibrate_BadModels_Refused(string models)
    {
        this.LockWorkspaces();

        Assert.Equal(2, await this.RunAsync(this.Context(), "--models", models));

        Assert.StartsWith("error: ", this.output.ToString(), StringComparison.Ordinal);
        Assert.Empty(this.fake.Requests);
    }

    [Fact]
    public async Task Calibrate_UnlistedModel_RefusedBeforeAnyCall()
    {
        this.LockWorkspaces();

        var code = await this.RunAsync(this.Context(providerFor: m => m == "unlisted-model" ? null : FakeLabellingLlm.Provider), "--models", "judge-owner,unlisted-model");

        Assert.Equal(2, code);
        Assert.Contains("unlisted-model", this.output.ToString(), StringComparison.Ordinal);
        Assert.Empty(this.fake.Requests);
    }

    [Fact]
    public async Task Calibrate_MoreCallsThanTheCap_RefusedBeforeAnyCall()
    {
        this.LockWorkspaces();

        var code = await this.RunAsync(this.Context(new LabellingOptions { MinCallIntervalMs = 0, MaxCalls = 100 }), "--models", "judge-owner,judge-stored");

        Assert.Equal(2, code);
        Assert.Contains("MaxCalls", this.output.ToString(), StringComparison.Ordinal);
        Assert.Empty(this.fake.Requests);
    }

    [Fact]
    // The archived run (75) plus the regraded, not yet archived history sample (51); the unchanged memory sample counts once.
    public async Task Calibrate_Estimate_CountsArchivedSetsAndUnarchivedWorkspaceSamplesWithoutCredentialsOrCalls()
    {
        this.LockWorkspaces();
        new CalibrationArchive(this.CalibrationDirectory).Archive("run1", this.workspaces);
        var extra = this.items[0] with { QueryId = "h900", CandidateId = "c900" };
        KappaSampleFile.Write(this.workspaces["history-eval"], this.protection, [.. this.items.Where(i => i.Tool == "history-eval"), extra]);
        var context = this.Context() with { CredentialsReady = () => false, CredentialsTimeout = TimeSpan.Zero };

        Assert.Equal(0, await this.RunAsync(context, "--models", "claude-opus-5.5,gpt-6.1-sol", "--estimate"));

        var text = this.output.ToString();
        Assert.Contains("126 items (101 history, 25 memory)", text, StringComparison.Ordinal);
        Assert.Contains("= 2 combinations, 252 LLM calls", text, StringComparison.Ordinal);
        Assert.Contains("note: gpt-6.1-sol is of the query writer's family", text, StringComparison.Ordinal);
        Assert.Empty(this.fake.Requests);
        Assert.False(Directory.Exists(Path.Combine(this.CalibrationDirectory, "reports")));
    }

    [Fact]
    public async Task Calibrate_NoLockedSample_Refused()
    {
        KappaSampleFile.Write(this.workspaces["history-eval"], this.protection, this.items.Take(3).ToList());

        Assert.Equal(2, await this.RunAsync(this.Context(), "--models", "judge-owner"));

        Assert.Contains("error: no locked κ sample to calibrate on", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calibrate_ProviderKeepsFailing_StopsWithExit1AndReportsTheCompletedCombos()
    {
        this.LockWorkspaces();

        var code = await this.RunAsync(this.Context(), "--models", "judge-owner,judge-down");

        Assert.Equal(1, code);
        Assert.Contains("kept failing for model 'judge-down'", this.output.ToString(), StringComparison.Ordinal);
        using var report = JsonDocument.Parse(this.ReportText());
        Assert.Equal(1.0, Result(report.RootElement, "judge-owner").GetProperty("kappa").GetDouble(), 6);
        Assert.Single(report.RootElement.GetProperty("results").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, report.RootElement.GetProperty("stopped").ValueKind);
    }

    [Fact]
    public async Task Calibrate_Cancelled_Throws()
    {
        this.LockWorkspaces();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JudgeCalibrationCli.RunAsync(
            new EvaluationRun(["calibrate", "--models", "judge-owner"], this.output, this.protection, null), this.Context(), cancellation.Token));
    }
}
