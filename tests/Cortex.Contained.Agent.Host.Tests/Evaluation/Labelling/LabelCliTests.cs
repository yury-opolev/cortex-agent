using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.History;
using Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

/// <summary><c>history-eval label</c> end to end on a synthetic store: fake LLM, fake embedder, encrypted workspace.</summary>
public sealed class LabelCliTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"label-cli-{Guid.NewGuid():N}");
    private readonly FakeLabellingLlm fake = new();
    private readonly StringWriter output = new();
    private readonly StorageKeyRing keys = EvalTestKeys.Ring();
    private readonly EvalProtection protection;

    public LabelCliTests() => this.protection = EvalProtection.Encrypted(this.keys);

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        this.output.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private LabelContext Context(LabellingOptions options) => new(
        options,
        this.fake,
        FakeLabellingLlm.ServesAll,
        () => "main-model",
        Path.Combine(this.root, "messages.db"),
        Path.Combine(this.root, "hybrid"),
        Path.Combine(this.root, "memory"),
        new MemoryMcpOptions { DataDirectory = Path.Combine(this.root, "live-memory"), Ollama = new OllamaOptions { Dimensions = FakeMemoryEmbedder.Dimensions } },
        _ => new FakeHistoryEmbedder(),
        _ => new FakeMemoryEmbedder(),
        TimeProvider.System);

    private static string Respond(string model, string system, string user) => model switch
    {
        "gpt-6.1-sol" => """{"text":"what happened with Q7x over that period","category":"lookup","goldFilters":{"since":"2000-01-01T00:00:00Z","until":"2100-01-01T00:00:00Z"}}""",
        "main-model" => """{"query":"Q7x"}""",
        _ => "{\"grades\":{" + string.Join(',', user.Split('\n').Where(l => l.StartsWith("[c", StringComparison.Ordinal))
            .Select(l => l[1..l.IndexOf(']', StringComparison.Ordinal)]).Select(a => $"\"{a}\":{(a == "c1" ? 2 : 0)}")) + "}}",
    };

    [Fact]
    public async Task RunAsync_Label_WritesAValidSealedDatasetAndLeavesNoCopy()
    {
        Directory.CreateDirectory(this.root);
        await using (var store = new MessageStore(Path.Combine(this.root, "messages.db"), NullLogger<MessageStore>.Instance, new MessageStoreEncryption(this.keys, TimeSpan.FromSeconds(5))))
        {
            for (var i = 0; i < 40; i++)
            {
                await store.SaveMessageAsync("u", i % 3 == 0 ? "ch" : "other", "user", $"message {i} about the garden plan and the boat repair schedule", T0.AddHours(i * 5));
            }
        }

        var workspace = new EvalWorkspace(Path.Combine(this.root, "hybrid"), this.protection);
        Directory.CreateDirectory(workspace.Directory);
        workspace.WritePlan(new AblationPlan { Configs = [new AblationConfig { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }, new AblationConfig { Name = "hybrid-rrf60" }] });
        this.fake.Respond = Respond;
        var run = new EvaluationRun(["label", "--queries", "6", "--kappa", "3"], this.output, this.protection, null);

        var code = await LabelCli.RunAsync("history-eval", run, this.Context(new LabellingOptions { MinCallIntervalMs = 0 }), CancellationToken.None);

        Assert.Equal(0, code);
        var dataset = workspace.ReadDataset();
        Assert.NotEmpty(dataset);
        Assert.Empty(EvalDataset.Validate(dataset));
        Assert.All(dataset, q => Assert.Equal(("gpt", "claude"), (q.QueryModelFamily, q.JudgeModelFamily)));
        Assert.True(EvalProtection.IsSealed(workspace.DatasetPath));
        Assert.Equal(3, KappaSampleFile.Read(workspace.Directory, this.protection).Count);
        Assert.Empty(Directory.EnumerateDirectories(workspace.RunsDirectory));
        Assert.DoesNotContain("garden", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Estimate_PrintsWithoutCalls()
    {
        var run = new EvaluationRun(["label", "--estimate"], this.output, EvalProtection.Plaintext, null);

        Assert.Equal(0, await LabelCli.RunAsync("memory-eval", run, this.Context(new LabellingOptions()), CancellationToken.None));

        Assert.Contains("memory-eval label: 270 queries", this.output.ToString(), StringComparison.Ordinal);
        Assert.Empty(this.fake.Requests);
    }

    [Fact]
    public async Task RunAsync_SameFamilyModels_Refused()
    {
        var run = new EvaluationRun(["label"], this.output, EvalProtection.Plaintext, null);

        Assert.Equal(2, await LabelCli.RunAsync("history-eval", run, this.Context(new LabellingOptions { QueryModel = "gpt-5", JudgeModel = "gpt-6.1-sol" }), CancellationToken.None));
        Assert.Contains("different families", this.output.ToString(), StringComparison.Ordinal);
    }
}
