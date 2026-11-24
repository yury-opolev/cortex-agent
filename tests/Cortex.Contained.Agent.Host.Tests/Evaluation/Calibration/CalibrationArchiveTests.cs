using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Calibration;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Calibration;

public sealed class CalibrationArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"calibration-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
    private readonly Dictionary<string, string> workspaces;
    private readonly CalibrationArchive archive;

    public CalibrationArchiveTests()
    {
        this.workspaces = new(StringComparer.Ordinal) { ["history-eval"] = Path.Combine(this.root, "hybrid"), ["memory-eval"] = Path.Combine(this.root, "memory") };
        foreach (var directory in this.workspaces.Values)
        {
            Directory.CreateDirectory(directory);
        }

        this.archive = new CalibrationArchive(Path.Combine(this.root, "eval", "calibration"));
    }

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private static KappaItem Item(string tool, string query, string candidate, int llm, int? human) =>
        new() { Tool = tool, QueryId = query, CandidateId = candidate, LlmGrade = llm, Query = "zqquery " + query, Candidate = "zqcandidate " + candidate, HumanGrade = human };

    private void Lock(string tool, EvalProtection protection, params KappaItem[] items)
    {
        KappaSampleFile.Write(this.workspaces[tool], protection, items);
        File.WriteAllLines(Path.Combine(this.workspaces[tool], CalibrationArchive.AgreementFileName), items.Select(i => $"{i.CandidateId},{i.LlmGrade},{i.HumanGrade}"));
    }

    private string SetDirectory(string runId, string tool) => Path.Combine(this.archive.SetsDirectory, runId, tool);

    [Fact]
    public void Archive_OnlyLockedSamples_CopiesBothFilesByteForByte()
    {
        this.Lock("history-eval", this.protection, Item("history-eval", "h1", "7", 2, 1));
        KappaSampleFile.Write(this.workspaces["memory-eval"], this.protection, [Item("memory-eval", "m1", "a", 1, null)]);

        Assert.Equal(1, this.archive.Archive("run1", this.workspaces));

        foreach (var name in new[] { KappaSampleFile.FileName, CalibrationArchive.AgreementFileName })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(this.workspaces["history-eval"], name)), File.ReadAllBytes(Path.Combine(this.SetDirectory("run1", "history-eval"), name)));
        }

        Assert.True(EvalProtection.IsSealed(Path.Combine(this.SetDirectory("run1", "history-eval"), KappaSampleFile.FileName)));
        Assert.False(Directory.Exists(this.SetDirectory("run1", "memory-eval")));
    }

    [Fact]
    public void Archive_Twice_KeepsTheFirstCopy()
    {
        this.Lock("history-eval", this.protection, Item("history-eval", "h1", "7", 2, 1));
        this.archive.Archive("run1", this.workspaces);
        var first = File.ReadAllBytes(Path.Combine(this.SetDirectory("run1", "history-eval"), KappaSampleFile.FileName));

        this.Lock("history-eval", this.protection, Item("history-eval", "h1", "7", 2, 2));
        this.archive.Archive("run1", this.workspaces);

        Assert.Equal(first, File.ReadAllBytes(Path.Combine(this.SetDirectory("run1", "history-eval"), KappaSampleFile.FileName)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("RUN1")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789")]
    public void Archive_InvalidRunId_Throws(string runId) =>
        Assert.Throws<ArgumentException>(() => this.archive.Archive(runId, this.workspaces));

    [Fact]
    public void Load_EncryptedSets_ReturnsGradedItemsAndTheNewestSetWins()
    {
        this.Lock("history-eval", this.protection, Item("history-eval", "h1", "7", 2, 1), Item("history-eval", "h2", "8", 0, 0));
        this.Lock("memory-eval", this.protection, Item("memory-eval", "m1", "a", 1, 2));
        this.archive.Archive("old", this.workspaces);
        this.Lock("history-eval", this.protection, Item("history-eval", "h1", "7", 1, 2), Item("history-eval", "h3", "9", 0, 1));
        File.Delete(Path.Combine(this.workspaces["memory-eval"], CalibrationArchive.AgreementFileName));
        this.archive.Archive("new", this.workspaces);
        Directory.SetLastWriteTimeUtc(Path.Combine(this.archive.SetsDirectory, "old"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(Path.Combine(this.archive.SetsDirectory, "new"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var items = this.archive.Load(this.protection);

        Assert.Equal(4, items.Count);
        Assert.Equal((1, 2), items.Where(i => i.QueryId == "h1").Select(i => (i.LlmGrade, i.HumanGrade!.Value)).Single());
        Assert.Contains(items, i => i.QueryId == "h2");
        Assert.Contains(items, i => i.QueryId == "h3");
        Assert.Contains(items, i => i is { Tool: "memory-eval", QueryId: "m1", Query: "zqquery m1" });
    }

    [Fact]
    public void Load_Plaintext_RoundTripsAndSkipsUngradedItems()
    {
        this.Lock("history-eval", EvalProtection.Plaintext, Item("history-eval", "h1", "7", 2, 1), Item("history-eval", "h2", "8", 0, null));
        this.archive.Archive("run1", this.workspaces);

        var items = this.archive.Load(EvalProtection.Plaintext);

        Assert.Equal(Item("history-eval", "h1", "7", 2, 1), Assert.Single(items));
    }

    [Fact]
    public void Locked_CurrentWorkspaces_OnlyTheSubmittedSamples()
    {
        this.Lock("history-eval", this.protection, Item("history-eval", "h1", "7", 2, 1));
        KappaSampleFile.Write(this.workspaces["memory-eval"], this.protection, [Item("memory-eval", "m1", "a", 1, 1)]);

        var items = CalibrationArchive.Locked(this.workspaces, this.protection);

        Assert.Equal("h1", Assert.Single(items).QueryId);
    }

    [Fact]
    public void Load_NoArchive_Empty() => Assert.Empty(this.archive.Load(this.protection));
}
