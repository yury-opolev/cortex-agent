using Cortex.Contained.Agent.Host.Memory.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;

public sealed class MemoryEvalDatasetTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"memory-dataset-{Guid.NewGuid():N}.jsonl");

    public void Dispose() => File.Delete(this.path);

    private static MemoryEvalQuery Query(string id, string category = "lookup", params MemoryJudgment[] judgments) => new()
    {
        Id = id,
        Text = "placeholder query",
        Category = category,
        Judgments = judgments.Length == 0 ? [new MemoryJudgment("mem-a", 2)] : judgments,
    };

    [Fact]
    public void ReadAndValidate_ExampleLine_RoundTripsWithoutErrors()
    {
        File.WriteAllLines(this.path,
        [
            """{"id":"m001","text":"which editor do I prefer","category":"tag_filtered","stratum":"real","judgments":[{"memoryId":"mem-a","grade":2},{"memoryId":"mem-b","grade":0}],"tags":["preferences"],"modelQuery":{"text":"editor preference"},"sourceMemoryId":"mem-a","queryModelFamily":"family-a","judgeModelFamily":"family-b"}""",
            "",
        ]);

        var query = Assert.Single(MemoryEvalDataset.Read(this.path));

        Assert.Equal(["mem-a"], query.Relevant);
        Assert.Equal(["preferences"], query.Tags);
        Assert.Equal("mem-a", query.SourceMemoryId);
        Assert.Empty(MemoryEvalDataset.Validate([query]));
        Assert.Contains("\"memoryId\":\"mem-a\"", MemoryEvalDataset.ToLine(query), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ReportsEveryRuleById()
    {
        var errors = MemoryEvalDataset.Validate(
        [
            Query("dup"),
            Query("dup"),
            Query("cat", "gossip"),
            Query("grade", "lookup", new MemoryJudgment("mem-a", 3)),
            Query("abstain", "abstention", new MemoryJudgment("mem-a", 1)),
            Query("none", "lookup", new MemoryJudgment("mem-a", 0)),
            Query("multi", "multi", new MemoryJudgment("mem-a", 2)),
            Query("tagged", "tag_filtered"),
            Query("judge") with { QueryModelFamily = "gpt", JudgeModelFamily = "GPT" },
        ]);

        foreach (var id in new[] { "dup", "cat", "grade", "abstain", "none", "multi", "tagged", "judge" })
        {
            Assert.Contains(errors, e => e.StartsWith(id + ":", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(errors, e => e.Contains("placeholder", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateForRegistration_FewerThan250Queries_IsAnError()
    {
        var small = Enumerable.Range(0, 249).Select(i => Query($"m{i:D3}")).ToList();
        var enough = Enumerable.Range(0, 250).Select(i => Query($"m{i:D3}")).ToList();

        Assert.Contains(MemoryEvalDataset.ValidateForRegistration(small), e => e.Contains("250", StringComparison.Ordinal));
        Assert.Empty(MemoryEvalDataset.ValidateForRegistration(enough));
    }

    [Fact]
    public void Seeds_AreDeterministicStratifiedAndFitTheirCategory()
    {
        var memories = Enumerable.Range(0, 60).Select(i => new MemorySeedSource(
            $"mem-{i:D2}",
            null,
            i % 3 == 0 ? [] : [i % 3 == 1 ? "travel" : "work"],
            i % 4 == 0 ? $"invoice INV-{i} paid" : "a plain note")).ToList();

        var seeds = MemoryQuerySeeds.Select(memories, count: 40);

        Assert.Equal(seeds, MemoryQuerySeeds.Select(memories, count: 40));
        Assert.Equal(40, seeds.Count);
        Assert.Equal(4, seeds.Count(s => s.Category == "abstention" && s.MemoryId is null));
        Assert.Equal(seeds.Count, seeds.Select(s => s.SeedId).Distinct().Count());
        var chosen = seeds.Where(s => s.MemoryId is not null).ToList();
        Assert.Equal(12, chosen.Count(s => s.Tags.Count == 0));
        Assert.All(chosen.Where(s => s.Category == "tag_filtered"), s => Assert.NotEmpty(s.Tags));
        Assert.All(chosen.Where(s => s.Category == "identifier"), s => Assert.Contains("INV-", s.Content, StringComparison.Ordinal));
        Assert.Equal(6, chosen.Select(s => s.Category).Distinct().Count());
    }
}
