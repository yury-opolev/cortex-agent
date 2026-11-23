using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed partial class RelevanceJudgeTests
{
    private readonly FakeLabellingLlm fake = new();

    private RelevanceJudge Judge(int batchSize, int attempts = 2) =>
        new(new LabellingLlm(this.fake, FakeLabellingLlm.ServesAll, new LabellingOptions { MinCallIntervalMs = 0, MaxAttempts = attempts }, delay: (_, _) => Task.CompletedTask),
            "claude-opus-5.5", batchSize, "history-eval");

    private static List<JudgeCandidate> Candidates(int count) =>
        Enumerable.Range(1, count).Select(i => new JudgeCandidate($"10{i}", $"2026-03-0{i} · user: zqtext {i}")).ToList();

    // Grades every alias of the prompt with (alias number % 3).
    private static string GradeAll(string user) =>
        "{\"grades\":{" + string.Join(',', Alias().Matches(user).Select(m => $"\"c{m.Groups[1].Value}\":{int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) % 3}")) + "}}";

    [GeneratedRegex(@"\[c(\d+)\]")]
    private static partial Regex Alias();

    [Fact]
    public async Task GradeAsync_Batches_MapsAliasesBackToIds()
    {
        this.fake.Respond = (_, _, user) => GradeAll(user);

        var grades = await this.Judge(batchSize: 2).GradeAsync("when was the ferry booked", Candidates(3), CancellationToken.None);

        Assert.Equal(2, this.fake.Requests.Count);
        Assert.Equal(1, grades!["101"]);
        Assert.Equal(2, grades["102"]);
        Assert.Equal(1, grades["103"]);
        Assert.All(this.fake.Requests, r => Assert.Equal("claude-opus-5.5", r.Model));
        Assert.Contains("Query: when was the ferry booked", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("[c1] 2026-03-01 · user: zqtext 1", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.DoesNotContain("101", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("2 = the item directly answers", this.fake.Requests[0].System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GradeAsync_BatchNeverValid_SplitsUntilSingleItemsSucceed()
    {
        this.fake.Respond = (_, _, user) => Alias().Count(user) > 1 ? """{"grades":{"c1":2}}""" : GradeAll(user);

        var grades = await this.Judge(batchSize: 4).GradeAsync("q", Candidates(4), CancellationToken.None);

        Assert.Equal(4, grades!.Count);
        Assert.Equal(2 + 2 + 2 + 4, this.fake.Requests.Count);
    }

    [Fact]
    public async Task GradeAsync_OutOfRangeGradeEverywhere_Null()
    {
        this.fake.Respond = (_, _, _) => """{"grades":{"c1":3}}""";

        Assert.Null(await this.Judge(batchSize: 1).GradeAsync("q", Candidates(1), CancellationToken.None));
    }
}
