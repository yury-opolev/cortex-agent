using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation;

public sealed class EvaluationFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"eval-files-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
    private readonly EvaluationFiles files;

    public EvaluationFilesTests()
    {
        this.files = new EvaluationFiles(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["history-eval"] = Path.Combine(this.root, "hybrid") },
            () => this.protection);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Write_Dataset_SealedAndReadBack_IdenticalContentNotRewritten()
    {
        var request = new EvaluationFileWriteDto { Tool = "history-eval", Name = "dataset.jsonl", Content = "{\"id\":\"e1\",\"text\":\"zqsecret\"}\n" };

        var written = this.files.Write(request);
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(this.root, "hybrid", "dataset.jsonl"));
        var again = this.files.Write(request);
        var read = this.files.Read(new EvaluationFileRequestDto { Tool = "history-eval", Name = "dataset.jsonl" });

        Assert.True(written is { Written: true, Sealed: true });
        Assert.True(again is { Written: false, Unchanged: true });
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(this.root, "hybrid", "dataset.jsonl")));
        Assert.Equal(request.Content, read.Content);
        Assert.True(read.Sealed);
        Assert.DoesNotContain("zqsecret", File.ReadAllText(Path.Combine(this.root, "hybrid", "dataset.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_AnalysisPlan_StaysPlaintext()
    {
        Assert.True(this.files.Write(new EvaluationFileWriteDto { Tool = "history-eval", Name = "analysis-plan.md", Content = "# plan" }).Written);

        Assert.Equal("# plan", File.ReadAllText(Path.Combine(this.root, "hybrid", "analysis-plan.md")));
    }

    [Theory]
    [InlineData("history-eval", "../messages.db")]
    [InlineData("history-eval", "preregistration.json")]
    [InlineData("history-eval", "runs")]
    [InlineData("memory-eval", "dataset.jsonl")]
    public void Write_NameOutsideTheAllowList_Refused(string tool, string name)
    {
        var result = this.files.Write(new EvaluationFileWriteDto { Tool = tool, Name = name, Content = "x" });

        Assert.False(result.Written);
        Assert.NotNull(result.Error);
    }
}
