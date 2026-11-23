using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class DatasetFinaliserTests
{
    private static DraftedQuery Draft(string id, string category, string stratum = "synthetic", IReadOnlyList<string>? tags = null) =>
        new() { Id = id, SeedId = "s" + id, Text = "query " + id, Category = category, Stratum = stratum, GoldSourceIds = ["1"], Tags = tags };

    private static Dictionary<string, int> Grades(params (string Id, int Grade)[] grades) =>
        grades.ToDictionary(g => g.Id, g => g.Grade, StringComparer.Ordinal);

    [Fact]
    public void Finalise_AppliesThePreDeclaredRules()
    {
        (DraftedQuery, IReadOnlyDictionary<string, int>)[] judged =
        [
            (Draft("h1", "lookup"), Grades(("1", 2), ("2", 0))),
            (Draft("h2", "lookup"), Grades(("1", 0))),
            (Draft("h3", "lookup", "real"), Grades(("5", 0))),
            (Draft("h4", "abstention"), Grades(("7", 1))),
            (Draft("h5", "abstention"), Grades(("7", 0))),
            (Draft("h6", "enumeration"), Grades(("1", 2), ("2", 0))),
            (Draft("h7", "paraphrase"), Grades(("1", 2))),
            (Draft("h8", "paraphrase"), Grades(("3", 2))),
        ];

        var (queries, report) = DatasetFinaliser.Finalise(judged, (draft, relevant) => draft.Id != "h7");

        Assert.Equal(["h1", "h3", "h4", "h5", "h6", "h7", "h8"], queries.Select(q => q.Draft.Id));
        Assert.Equal(("abstention", true), (queries[1].Category, queries[1].ExpectAbstain));
        Assert.Equal("lookup", queries[2].Category);
        Assert.Equal(("abstention", true), (queries[3].Category, queries[3].ExpectAbstain));
        Assert.Equal("lookup", queries[4].Category);
        Assert.Equal("lookup", queries[5].Category);
        Assert.Equal("paraphrase", queries[6].Category);
        Assert.Equal((8, 7, 1), (report.Judged, report.Kept, report.Dropped));
        Assert.Equal(1, report.Recategorised["paraphrase→lookup"]);
        Assert.Equal(1, report.Recategorised["lookup→abstention"]);
    }

    [Fact]
    public void ToHistory_ValidDatasetLineWithAllJudgments()
    {
        var draft = Draft("h1", "temporal") with
        {
            GoldFilters = new EvalFilters { Since = DateTimeOffset.UnixEpoch },
            ModelText = "ferry",
            ModelFilters = new EvalFilters { Channel = "ch" },
        };
        var (queries, _) = DatasetFinaliser.Finalise([(draft, Grades(("12", 2), ("3", 0)))], (_, _) => true);

        var line = DatasetFinaliser.ToHistory(queries[0], "gpt", "claude");

        Assert.Empty(EvalDataset.Validate([line]));
        Assert.Equal([3L, 12L], line.Judgments.Select(j => j.MessageId));
        Assert.Equal(("gpt", "claude"), (line.QueryModelFamily, line.JudgeModelFamily));
        Assert.Equal("ch", line.ModelQuery!.Filters!.Channel);
    }

    [Fact]
    public void ToMemory_TagsOnlyForTagFiltered()
    {
        var (queries, _) = DatasetFinaliser.Finalise(
            [(Draft("m1", "tag_filtered", tags: ["finance"]), Grades(("mem-a", 2))), (Draft("m2", "lookup", tags: ["finance"]), Grades(("mem-a", 1)))],
            (_, _) => true);

        var lines = queries.Select(q => DatasetFinaliser.ToMemory(q, "gpt", "claude")).ToList();

        Assert.Empty(MemoryEvalDataset.Validate(lines));
        Assert.Equal(["finance"], lines[0].Tags);
        Assert.Null(lines[1].Tags);
        Assert.Equal("1", lines[0].SourceMemoryId);
    }

    [Fact]
    public void Select_StratifiedByGradeAndSpreadOverQueries()
    {
        var queries = Enumerable.Range(0, 40)
            .Select(i => new LabelledQuery(Draft($"h{i}", "lookup"), "lookup", false, Grades(($"{i}0", 0), ($"{i}1", 1), ($"{i}2", 2))))
            .ToList();

        var picks = KappaSampler.Select(queries, 75);

        Assert.Equal(75, picks.Count);
        Assert.Equal([25, 25, 25], picks.GroupBy(p => p.LlmGrade).OrderBy(g => g.Key).Select(g => g.Count()));
        Assert.Equal(75, picks.Distinct().Count());
        Assert.Equal(picks, KappaSampler.Select(queries, 75));
    }

    [Fact]
    public void Select_ScarceGrade_ToppedUpFromTheOthers()
    {
        var queries = Enumerable.Range(0, 10)
            .Select(i => new LabelledQuery(Draft($"h{i}", "lookup"), "lookup", false, Grades(($"{i}0", 0), ($"{i}1", 0), ($"{i}2", i == 0 ? 2 : 0))))
            .ToList();

        var picks = KappaSampler.Select(queries, 9);

        Assert.Equal(9, picks.Count);
        Assert.Equal(1, picks.Count(p => p.LlmGrade == 2));
    }
}
