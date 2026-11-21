using System.Text;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.History;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Erasure workflow (§11) and the byte-level erasure gate (§14.1).</summary>
public sealed class HistoryPurgeServiceTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();
    private readonly SubagentSessionStore subagents;
    private readonly HistoryRecallService recall;
    private readonly HistoryPurgeService purge;
    private readonly string toolOutput;

    public HistoryPurgeServiceTests()
    {
        this.toolOutput = Path.Combine(this.h.Root, "tool-output");
        Directory.CreateDirectory(this.toolOutput);
        this.h.Options = new HistoryOptions { Enabled = true, TimeZone = "UTC", SmokeArtifactsDirectory = Path.Combine(this.h.Root, "smoke"), EvalArtifactsDirectory = Path.Combine(this.h.Root, "eval") };
        this.subagents = new SubagentSessionStore(this.h.Root, NullLogger<SubagentSessionStore>.Instance);
        this.recall = new HistoryRecallService(this.h.Store, this.h.Projector, this.h.Gate, this.h.Status, this.h.Monitor);
        this.purge = new HistoryPurgeService(
            this.h.Store,
            this.h.Gate,
            this.h.Projector,
            [
                new IndexErasureStep(this.h.Projector),
                new MessagesWalErasureStep(this.h.Store),
                new ToolOutputErasureStep(this.toolOutput),
                new SubagentErasureStep(this.subagents),
                new SmokeArtifactErasureStep(this.h.Monitor),
                new VacuumErasureStep(this.h.Store, this.h.Projector, this.h.Monitor, TimeProvider.System),
            ],
            this.h.Monitor,
            TimeProvider.System,
            NullLogger<HistoryPurgeService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        this.subagents.Dispose();
        await this.h.DisposeAsync();
    }

    private async Task IndexAsync()
    {
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
    }

    private IEnumerable<string> AllFiles() =>
        Directory.EnumerateFiles(this.h.Root, "*", SearchOption.AllDirectories);

    /// <summary>Files (relative names) containing <paramref name="marker"/> verbatim.</summary>
    private List<string> FilesContaining(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        var hits = new List<string>();
        foreach (var file in this.AllFiles())
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (copy.ToArray().AsSpan().IndexOf(needle) >= 0)
            {
                hits.Add(Path.GetRelativePath(this.h.Root, file));
            }
        }

        return hits;
    }

    private bool AnyFileContains(string marker) => this.FilesContaining(marker).Count > 0;

    /// <summary>FTS5 prefix-compresses sorted terms, so each marker is a random token sharing no prefix with its neighbours and must be present in both the message store and the index before the purge.</summary>
    private void AssertStoredInMessagesAndIndex(string marker)
    {
        var files = this.FilesContaining(marker);
        Assert.Contains(files, f => f.StartsWith("messages.db", StringComparison.Ordinal));
        Assert.Contains(files, f => f.StartsWith("history", StringComparison.Ordinal));
    }
    private bool VocabularyContains(string prefix)
    {
        using var reader = this.h.Projector.Current!.OpenReader();
        return reader.ReadTerms().Any(t => t.StartsWith(prefix, StringComparison.Ordinal))
            || reader.ReadPostings().Any(p => p.StartsWith(prefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurgeAsync_Channel_MarkersGoneFromEveryFileAndVocabulary()
    {
        await this.h.SayAsync("xvkqjwzd alpha", 0, channel: "secret");
        await this.h.SayAsync("wnfhgtrp beta", 1, channel: "secret", role: "assistant");
        await this.h.SayAsync("keepme gamma", 2, channel: "other");
        await this.IndexAsync();
        Assert.True(this.AnyFileContains("xvkqjwzd"));
        Assert.True(this.VocabularyContains("xvk"));

        var result = await this.purge.PurgeAsync(new HistoryPurgeScope("secret", null), null, CancellationToken.None);

        Assert.Equal(2, result.Deleted);
        Assert.True(result.ErasureComplete);
        Assert.False(this.AnyFileContains("xvkqjwzd"));
        Assert.False(this.AnyFileContains("wnfhgtrp"));
        Assert.False(this.VocabularyContains("xvk"));
        Assert.False(this.VocabularyContains("wnf"));
        Assert.True(this.VocabularyContains("keepme"));
        Assert.Empty(await this.h.Store.ReadPendingErasureAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PurgeAsync_AfterSameLengthUpdate_OldTextGoneEverywhere()
    {
        var id = await this.h.SayAsync("code pjdyzmqv", 0);
        await this.h.SayAsync("other channel", 1, channel: "other");
        await this.IndexAsync();

        await this.h.Store.UpdateContentAsync(id, "code xxxxxxxxx");
        await this.purge.PurgeAsync(new HistoryPurgeScope("other", null), null, CancellationToken.None);

        Assert.False(this.AnyFileContains("pjdyzmqv"));
        Assert.False(this.VocabularyContains("pjd"));
    }

    [Fact]
    public async Task PurgeAsync_BusyReaderHoldsWal_IncompleteThenCompletesOnResume()
    {
        await this.h.SayAsync("hrtkwxbn", 0);
        await this.IndexAsync();
        var reader = new SqliteConnection($"Data Source={Path.Combine(this.h.Root, "messages.db")};Mode=ReadOnly;Pooling=False");
        reader.Open();
        var transaction = reader.BeginTransaction(deferred: true);
        using (var pin = reader.CreateCommand())
        {
            pin.Transaction = transaction;
            pin.CommandText = "SELECT COUNT(*) FROM Messages;";
            pin.ExecuteScalar();
        }

        var result = await this.purge.PurgeAsync(HistoryPurgeScope.All, null, CancellationToken.None);

        Assert.False(result.ErasureComplete);
        Assert.Contains(ErasureSteps.MessagesWal, result.CleanupPending);
        Assert.Empty((await this.recall.SearchAsync(new HistorySearchRequest { Query = "hrtkwxbn" }, CancellationToken.None)).Hits);

        transaction.Dispose();
        reader.Dispose();
        Assert.Empty(await this.purge.ResumePendingAsync(CancellationToken.None));
        Assert.Empty(await this.h.Store.ReadPendingErasureAsync(CancellationToken.None));
        Assert.False(this.AnyFileContains("hrtkwxbn"));
    }

    [Fact]
    public async Task PurgeAsync_RecallInFlight_PurgeWaitsAndLaterRecallsNeverSeeRows()
    {
        await this.h.SayAsync("zqemarker", 0);
        await this.IndexAsync();
        var release = new TaskCompletionSource();
        this.recall.AfterSnapshotHook = () => release.Task;

        var inFlight = this.recall.SearchAsync(new HistorySearchRequest { Query = "zqemarker" }, CancellationToken.None);
        while (this.h.Gate.ActiveReaders == 0)
        {
            await Task.Delay(10);
        }

        var purging = this.purge.PurgeAsync(HistoryPurgeScope.All, null, CancellationToken.None);
        this.recall.AfterSnapshotHook = null;
        var queued = this.recall.SearchAsync(new HistorySearchRequest { Query = "zqemarker" }, CancellationToken.None);
        await Task.Delay(200);
        Assert.False(purging.IsCompleted);
        Assert.False(queued.IsCompleted);

        release.SetResult();
        await inFlight;
        await purging;

        Assert.Empty((await queued).Hits);
        Assert.Empty((await this.recall.SearchAsync(new HistorySearchRequest { Query = "zqemarker" }, CancellationToken.None)).Hits);
    }

    [Fact]
    public async Task PurgeAsync_AllScope_CleansToolOutputSubagentsAndEvaluationArtifacts()
    {
        await this.h.SayAsync("old", 0);
        await File.WriteAllTextAsync(Path.Combine(this.toolOutput, "20260504-spill.txt"), "zqf spill");
        this.subagents.Create(new SubagentTask
        {
            TaskId = "sa-done", ParentConversation = "c", ParentChannel = "ch", Description = "d", Prompt = "zqg prompt",
            State = SubagentTaskState.Completed, CreatedAt = HistoryHarness.T0.AddMinutes(-5), CompletedAt = HistoryHarness.T0.AddMinutes(5),
        });
        this.subagents.Create(new SubagentTask
        {
            TaskId = "sa-later", ParentConversation = "c", ParentChannel = "ch", Description = "d", Prompt = "p",
            State = SubagentTaskState.Completed, CreatedAt = HistoryHarness.T0.AddDays(3), CompletedAt = HistoryHarness.T0.AddDays(3),
        });
        var overlapping = Path.Combine(this.h.Options.SmokeArtifactsDirectory, "runs", "run-1");
        var disjoint = Path.Combine(this.h.Options.SmokeArtifactsDirectory, "runs", "run-2");
        var evalOverlapping = Path.Combine(this.h.Options.EvalArtifactsDirectory, "runs", "ablate-dev-1");
        Directory.CreateDirectory(overlapping);
        Directory.CreateDirectory(disjoint);
        Directory.CreateDirectory(evalOverlapping);
        await File.WriteAllTextAsync(Path.Combine(evalOverlapping, "manifest.json"), $$"""{"corpusFrom":"2026-05-01T00:00:00Z","corpusTo":"2026-05-10T00:00:00Z"}""");
        await File.WriteAllTextAsync(Path.Combine(overlapping, "manifest.json"), $$"""{"corpusFrom":"2026-05-01T00:00:00Z","corpusTo":"2026-05-10T00:00:00Z"}""");
        await File.WriteAllTextAsync(Path.Combine(disjoint, "manifest.json"), $$"""{"corpusFrom":"2026-06-01T00:00:00Z","corpusTo":"2026-06-10T00:00:00Z"}""");
        await this.IndexAsync();

        var result = await this.purge.PurgeAsync(HistoryPurgeScope.All, null, CancellationToken.None);

        Assert.True(result.ErasureComplete);
        Assert.Empty(Directory.EnumerateFiles(this.toolOutput));
        Assert.Null(this.subagents.GetById("sa-done"));
        Assert.NotNull(this.subagents.GetById("sa-later"));
        Assert.False(Directory.Exists(overlapping));
        Assert.False(Directory.Exists(evalOverlapping));
        Assert.True(Directory.Exists(disjoint));
    }

    [Fact]
    public async Task PurgeAsync_ChannelScope_KeepsToolOutputFiles()
    {
        await this.h.SayAsync("x", 0);
        await File.WriteAllTextAsync(Path.Combine(this.toolOutput, "spill.txt"), "kept");

        await this.purge.PurgeAsync(new HistoryPurgeScope("ch", null), null, CancellationToken.None);

        Assert.Single(Directory.EnumerateFiles(this.toolOutput));
    }

    [Fact]
    public async Task PurgeAsync_WithReplacement_SwapsRowsAndReportsInserted()
    {
        await this.h.SayAsync("gcsvnpqy", 0);
        await this.IndexAsync();

        var result = await this.purge.PurgeAsync(
            HistoryPurgeScope.All,
            [new Cortex.Contained.Agent.Host.Storage.MessageRecord { UserId = "u", ChannelId = "ch", Role = "user", Content = "imported", Timestamp = HistoryHarness.T0 }],
            CancellationToken.None);

        Assert.Equal((1, 1), (result.Deleted, result.Inserted));
        Assert.Null(this.h.Projector.Current);
        Assert.False(this.AnyFileContains("gcsvnpqy"));
    }
}
