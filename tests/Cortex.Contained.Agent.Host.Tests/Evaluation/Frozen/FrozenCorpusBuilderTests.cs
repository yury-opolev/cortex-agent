using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

/// <summary>Freezing a sanitised, encrypted corpus version with consistent pseudonyms, fail-closed on leakage.</summary>
public sealed class FrozenCorpusBuilderTests : IAsyncDisposable
{
    internal static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"frozen-{Guid.NewGuid():N}");
    private readonly StorageKeyRing keys = EvalTestKeys.Ring();
    private readonly EvalProtection protection;

    public FrozenCorpusBuilderTests()
    {
        this.protection = EvalProtection.Encrypted(this.keys);
    }

    private string State => Path.Combine(this.root, "state");

    private FrozenCorpusStore Store => new(Path.Combine(this.State, "eval"));

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    /// <summary>An encrypted live install with a registered history workspace and a sealed dictionary.</summary>
    internal static async Task<FreezeSources> SeedAsync(string state, StorageKeyRing keys, EvalProtection protection, string extraText = "")
    {
        Directory.CreateDirectory(state);
        var messagesDb = Path.Combine(state, "messages.db");
        long train, invoice;
        await using (var store = new MessageStore(messagesDb, NullLogger<MessageStore>.Instance, new MessageStoreEncryption(keys, TimeSpan.FromSeconds(5))))
        {
            train = await store.SaveMessageAsync("user-123456789012345678", "discord-dm-123456789012345678", "user", "Alvenia booked the railway journey to Krøllvik, mail a.person@example.com " + extraText, T0);
            invoice = await store.SaveMessageAsync("user-123456789012345678", "discord-dm-123456789012345678", "user", "invoice INV2041 is paid", T0.AddHours(5));
        }

        var history = new EvalWorkspace(Path.Combine(state, "eval", "hybrid"), protection);
        Directory.CreateDirectory(history.Directory);
        protection.WriteLines(history.DatasetPath,
        [
            EvalDataset.ToLine(new EvalQuery
            {
                Id = "e001", Text = "when did Alvenia go to Krøllvik", Category = "lookup", Judgments = [new EvalJudgment(train, 2)],
                GoldFilters = new EvalFilters { Channel = "discord-dm-123456789012345678" },
            }),
            EvalDataset.ToLine(new EvalQuery { Id = "e002", Text = "invoice INV2041", Category = "identifier", Judgments = [new EvalJudgment(invoice, 2)] }),
        ]);
        File.WriteAllText(history.AnalysisPlanPath, "# plan");
        history.WritePlan(new AblationPlan
        {
            Configs = [new AblationConfig { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }, new AblationConfig { Name = "hybrid-rrf60" }],
            Holdout = new HoldoutPlan { Config = "hybrid-rrf60" },
        });
        history.Register();

        var lexicon = Path.Combine(state, "eval", "corpus", "dictionary.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(lexicon)!);
        protection.WriteLines(lexicon, ["""{"term":"Alvenia","kind":"person"}""", """{"term":"Krøllvik","kind":"place"}"""]);
        var memory = new MemoryMcpOptions { DataDirectory = Path.Combine(state, "memory") };
        return new FreezeSources(messagesDb, memory, history, new MemoryEvalWorkspace(Path.Combine(state, "eval", "memory"), protection), lexicon);
    }

    [Fact]
    public async Task FreezeAsync_EncryptedInstall_SanitisedEncryptedVersionWithConsistentLabels()
    {
        var sources = await SeedAsync(this.State, this.keys, this.protection);

        var result = await FrozenCorpusBuilder.FreezeAsync(sources, this.Store, this.protection, TimeProvider.System, CancellationToken.None);

        SqliteConnection.ClearAllPools();
        var manifest = Assert.IsType<FrozenCorpusManifest>(result.Manifest);
        Assert.Equal("v1", manifest.Version);
        Assert.Equal((2, 2, T0, T0.AddHours(5)), (manifest.Messages, manifest.HistoryQueries, manifest.CorpusFrom, manifest.CorpusTo));
        var directory = this.Store.DirectoryOf("v1");
        Assert.Equal(Cortex.Contained.Contracts.Security.StoredFileState.Encrypted, Cortex.Contained.Contracts.Security.SqliteFileHeader.Classify(Path.Combine(directory, "messages.db")));
        Assert.All(
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories),
            f => Assert.DoesNotContain("Alvenia", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f)), StringComparison.Ordinal));

        var frozen = new EvalWorkspace(Path.Combine(directory, "history"), this.protection);
        frozen.VerifyRegistration();
        var query = frozen.ReadDataset()[0];
        Assert.StartsWith("when did Person", query.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789012345678", query.GoldFilters!.Channel, StringComparison.Ordinal);
        Assert.Equal(sources.History.ReadDataset()[0].Judgments, query.Judgments);

        await using var copy = new MessageStore(Path.Combine(directory, "messages.db"), NullLogger<MessageStore>.Instance, this.protection.CopyMessageEncryption);
        await using var snapshot = await copy.OpenSnapshotAsync(CancellationToken.None);
        var row = (await snapshot.ReadByIdsAsync([query.Judgments[0].MessageId], CancellationToken.None)).Single();
        Assert.Equal(query.GoldFilters.Channel, row.ChannelId);
        Assert.Contains(query.Text.Split(' ')[2], row.Content, StringComparison.Ordinal);
        Assert.Contains("@example.invalid", row.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreezeAsync_ResidualVariantTheSanitiserMissed_FailsClosedAndKeepsNothing()
    {
        var sources = await SeedAsync(this.State, this.keys, this.protection, extraText: "and later Krôllvik again");

        var result = await FrozenCorpusBuilder.FreezeAsync(sources, this.Store, this.protection, TimeProvider.System, CancellationToken.None);

        Assert.Null(result.Manifest);
        Assert.Equal(new Cortex.Contained.Agent.Host.Evaluation.Sanitising.LeakageFinding("messages.Content", "dictionary-term", 1), Assert.Single(result.Findings));
        Assert.Empty(Directory.EnumerateDirectories(this.Store.Root));
    }

    [Fact]
    public async Task FreezeAsync_DatasetChangedSinceRegistration_Refused()
    {
        var sources = await SeedAsync(this.State, this.keys, this.protection);
        this.protection.WriteLines(sources.History.DatasetPath, [.. sources.History.ReadDataset().Take(1).Select(EvalDataset.ToLine)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => FrozenCorpusBuilder.FreezeAsync(sources, this.Store, this.protection, TimeProvider.System, CancellationToken.None));
    }
}
