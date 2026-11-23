using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Orchestration;

public sealed class GradingServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"grading-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
    private readonly Dictionary<string, string> workspaces;
    private readonly GradingService service;

    public GradingServiceTests()
    {
        this.workspaces = new(StringComparer.Ordinal) { ["history-eval"] = Path.Combine(this.root, "hybrid"), ["memory-eval"] = Path.Combine(this.root, "memory") };
        foreach (var directory in this.workspaces.Values)
        {
            Directory.CreateDirectory(directory);
        }

        KappaSampleFile.Write(this.workspaces["history-eval"], this.protection,
        [
            Item("history-eval", "h0001", "101", 2, "zqtext ferry"),
            Item("history-eval", "h0002", "102", 0, "zqtext garden"),
        ]);
        KappaSampleFile.Write(this.workspaces["memory-eval"], this.protection, [Item("memory-eval", "m0001", "mem-a", 1, "zqtext note")]);
        this.service = new GradingService(this.workspaces, () => this.protection);
    }

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private static KappaItem Item(string tool, string query, string candidate, int grade, string text) =>
        new() { Tool = tool, QueryId = query, CandidateId = candidate, LlmGrade = grade, Query = "q " + query, Candidate = text };

    [Fact]
    public void Item_HistoryFirstThenMemoryWithoutTheJudgesGrade()
    {
        var first = this.service.Item(0)!;
        var last = this.service.Item(2)!;

        Assert.Equal((0, 3, "history-eval", "zqtext ferry"), (first.Index, first.Total, first.Tool, first.Candidate));
        Assert.Equal(("memory-eval", "q m0001"), (last.Tool, last.Query));
        Assert.Null(this.service.Item(3));
        Assert.DoesNotContain("LlmGrade", System.Text.Json.JsonSerializer.Serialize(first), StringComparison.Ordinal);
    }

    [Fact]
    public void Grade_AllItems_StoresSealedAndReportsNoKappaWhileRegradingIsPossible()
    {
        this.service.Grade(0, 2);
        var partial = this.service.Grade(1, 1);
        Assert.Equal((3, 2, false), (partial.Total, partial.Graded, partial.Complete));

        var done = this.service.Grade(2, 1);

        Assert.True(done.Complete);
        Assert.False(done.Submitted);
        Assert.Null(done.Kappa);
        Assert.Null(done.HistoryKappa);
        Assert.False(File.Exists(Path.Combine(this.workspaces["history-eval"], "judge-agreement.csv")));
        Assert.True(EvalProtection.IsSealed(Path.Combine(this.workspaces["history-eval"], KappaSampleFile.FileName)));
        Assert.Equal(2, new GradingService(this.workspaces, () => this.protection).Item(0)!.HumanGrade);
        Assert.Null(this.service.Grade(2, 0).Kappa);
        Assert.Null(this.service.Progress().Kappa);
    }

    [Fact]
    public void Submit_AllGraded_WritesAgreementFilesComputesKappaAndLocksTheGrades()
    {
        this.service.Grade(0, 2);
        this.service.Grade(1, 1);
        this.service.Grade(2, 1);

        var submitted = this.service.Submit();

        Assert.True(submitted.Submitted);
        Assert.NotNull(submitted.Kappa);
        Assert.NotNull(submitted.HistoryKappa);
        Assert.Null(submitted.MemoryKappa);
        Assert.Equal(["101,2,2", "102,0,1"], File.ReadAllLines(Path.Combine(this.workspaces["history-eval"], "judge-agreement.csv")));
        Assert.Equal(["mem-a,1,1"], File.ReadAllLines(Path.Combine(this.workspaces["memory-eval"], "judge-agreement.csv")));
        Assert.Equal(submitted, this.service.Progress());
        Assert.Equal(submitted, new GradingService(this.workspaces, () => this.protection).Progress());
        Assert.Throws<InvalidOperationException>(() => this.service.Grade(0, 0));
        Assert.Equal(2, this.service.Item(0)!.HumanGrade);
        Assert.Equal(submitted, this.service.Submit());
    }

    [Fact]
    public void Submit_ItemsStillUngraded_Refused()
    {
        this.service.Grade(0, 2);

        Assert.Throws<InvalidOperationException>(() => this.service.Submit());
        Assert.False(this.service.Progress().Submitted);
        Assert.False(File.Exists(Path.Combine(this.workspaces["history-eval"], "judge-agreement.csv")));
    }
    [Fact]
    public void Grade_ClearAndOutOfRange()
    {
        this.service.Grade(0, 2);
        Assert.Equal(0, this.service.Grade(0, null).Graded);
        Assert.Throws<ArgumentOutOfRangeException>(() => this.service.Grade(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => this.service.Grade(9, 1));
    }
}
