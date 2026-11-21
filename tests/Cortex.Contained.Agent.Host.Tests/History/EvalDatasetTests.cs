using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class EvalDatasetTests
{
    private static EvalQuery Query(string id, string category = "lookup", params EvalJudgment[] judgments) => new()
    {
        Id = id,
        Text = "placeholder query",
        Category = category,
        Judgments = judgments.Length == 0 ? [new EvalJudgment(1, 2)] : judgments,
    };

    [Fact]
    public void ReadAndValidate_ExampleLine_RoundTripsWithoutErrors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dataset-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                """{"id":"e001","text":"night train TownB","category":"lookup","stratum":"real","judgments":[{"messageId":12,"grade":2},{"messageId":13,"grade":0}],"goldFilters":{"channel":"discord-dm"},"modelQuery":{"text":"train TownB"},"queryModelFamily":"gpt","judgeModelFamily":"claude"}""",
                "",
            ]);

            var query = Assert.Single(EvalDataset.Read(path));

            Assert.Equal("real", query.Stratum);
            Assert.Equal([12L], query.Relevant);
            Assert.Equal("discord-dm", query.GoldFilters!.Channel);
            Assert.Empty(EvalDataset.Validate([query]));
            Assert.Contains("\"judgments\"", EvalDataset.ToLine(query), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_ReportsEveryRuleById()
    {
        var errors = EvalDataset.Validate(
        [
            Query("dup"),
            Query("dup"),
            Query("cat", "gossip"),
            Query("grade", "lookup", new EvalJudgment(1, 3)),
            Query("abstain", "abstention", new EvalJudgment(1, 1)),
            Query("none", "lookup", new EvalJudgment(1, 0)),
            Query("enum", "enumeration", new EvalJudgment(1, 2)),
            Query("judge") with { QueryModelFamily = "gpt", JudgeModelFamily = "GPT" },
        ]);

        Assert.Contains(errors, e => e.StartsWith("dup:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("cat:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("grade:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("abstain:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("none:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("enum:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("judge:", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("placeholder", StringComparison.Ordinal));
    }

    [Fact]
    public void ParaphraseGuard_LowOverlapPasses_SharedRareTokenFails()
    {
        var gold = new[] { "we booked the railway journey to TownB" };

        var paraphrase = ParaphraseGuard.Check("p1", "when is my train trip", gold, _ => 1, 1000);
        var leaky = ParaphraseGuard.Check("p2", "railway trip", gold, t => t == "railway" ? 2 : 500, 1000);
        var overlapping = ParaphraseGuard.Check("p3", "booked the railway journey", gold, _ => 500, 1000);

        Assert.True(paraphrase.Passed);
        Assert.Equal(0, paraphrase.Jaccard);
        Assert.False(leaky.Passed);
        Assert.Equal(1, leaky.SharedRareTokens);
        Assert.False(overlapping.Passed);
        Assert.True(overlapping.Jaccard > ParaphraseGuard.MaxJaccard);
    }
}
