using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

/// <summary>The eval-corpus job command end to end: freeze, export, record the upload, compare, delete.</summary>
public sealed class EvalCorpusCliTests : IAsyncDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"eval-corpus-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private string State => Path.Combine(this.root, "state");

    private EvalCorpusContext Context(FreezeSources sources) => new(
        Path.Combine(this.State, "eval"), sources.MessagesDb, new MemoryMcpOptions { DataDirectory = Path.Combine(this.State, "memory") },
        sources.History.Directory, sources.MemoryWorkspace.Directory, TimeProvider.System);

    private async Task<(int Code, string Output)> RunAsync(FreezeSources sources, string? passphrase, params string[] args)
    {
        using var output = new StringWriter();
        var code = await EvalCorpusCli.RunAsync(new EvaluationRun(args, output, this.protection, passphrase), this.Context(sources), CancellationToken.None);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Workflow_FreezeExportUploadCompareDelete()
    {
        var sources = await FrozenCorpusBuilderTests.SeedAsync(this.State, EvalTestKeys.Ring(), this.protection);

        Assert.Equal(0, (await this.RunAsync(sources, null, "freeze")).Code);
        Assert.Equal(2, (await this.RunAsync(sources, null, "export", "--version", "v1")).Code);
        var export = await this.RunAsync(sources, Passphrase, "export", "--version", "v1");
        Assert.Equal(0, export.Code);
        var sha = new FrozenCorpusStore(Path.Combine(this.State, "eval")).Read("v1")!.PackageSha256!;
        Assert.Contains(sha, export.Output, StringComparison.Ordinal);
        Assert.Equal(2, (await this.RunAsync(sources, null, "uploaded", "--version", "v1", "--content-id", "c1", "--sha256", new string('0', 64))).Code);
        Assert.Equal(0, (await this.RunAsync(sources, null, "uploaded", "--version", "v1", "--content-id", "c1", "--sha256", sha)).Code);

        using (var output = new StringWriter())
        {
            Assert.Equal(0, await FrozenCorpusRunTests.RunHistoryAsync(sources, this.protection, output, "holdout"));
            Assert.Equal(0, await FrozenCorpusRunTests.RunHistoryAsync(sources, this.protection, output, "holdout", "--corpus", "frozen:v1"));
        }

        var compare = await this.RunAsync(sources, null, "compare", "--version", "v1");
        Assert.Equal(0, compare.Code);
        Assert.Contains("| hybrid-rrf60 |", compare.Output, StringComparison.Ordinal);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(this.State, "eval", "compare"), "*.md"));

        var delete = await this.RunAsync(sources, null, "delete", "--version", "v1");
        Assert.Equal(0, delete.Code);
        Assert.Contains("cortex-eval-corpus-v1.cxpkg", delete.Output, StringComparison.Ordinal);
        var list = await this.RunAsync(sources, null, "list");
        Assert.Contains("PENDING off-machine deletion: v1", list.Output, StringComparison.Ordinal);
        Assert.Contains("contentId c1", list.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Freeze_LeakFound_RefusedWithLocationsAndKindsOnly()
    {
        var sources = await FrozenCorpusBuilderTests.SeedAsync(this.State, EvalTestKeys.Ring(), this.protection, extraText: "and Krôllvik");

        var freeze = await this.RunAsync(sources, null, "freeze");

        Assert.Equal(1, freeze.Code);
        Assert.Contains("leak: messages.Content dictionary-term ×1", freeze.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Krôllvik", freeze.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_TermAddedAfterFreeze_FoundInTheVersion()
    {
        var sources = await FrozenCorpusBuilderTests.SeedAsync(this.State, EvalTestKeys.Ring(), this.protection, extraText: "with Dorvin");
        Assert.Equal(0, (await this.RunAsync(sources, null, "freeze")).Code);
        this.protection.WriteLines(sources.LexiconPath, [.. this.protection.ReadLines(sources.LexiconPath), """{"term":"Dorvin","kind":"person"}"""]);

        var check = await this.RunAsync(sources, null, "check", "--version", "v1");

        Assert.Equal(1, check.Code);
        Assert.Contains("finding: messages.Content dictionary-term ×1", check.Output, StringComparison.Ordinal);
    }
}
