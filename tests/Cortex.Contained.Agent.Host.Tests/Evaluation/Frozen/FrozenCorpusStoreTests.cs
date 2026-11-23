using Cortex.Contained.Agent.Host.Evaluation.Frozen;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

/// <summary>The ledger of off-machine deletions and the version counter live outside the erasable evaluation directory.</summary>
public sealed class FrozenCorpusStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"frozen-store-{Guid.NewGuid():N}");

    private string Eval => Path.Combine(this.root, "state", "eval");

    private string Ledger => Path.Combine(this.root, "state", "eval-ledger");

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    internal static void WriteVersion(FrozenCorpusStore store, string version, Action<FrozenCorpusManifestBuilder>? configure = null)
    {
        var builder = new FrozenCorpusManifestBuilder(version);
        configure?.Invoke(builder);
        Directory.CreateDirectory(store.DirectoryOf(version));
        builder.Build().Write(Path.Combine(store.DirectoryOf(version), FrozenCorpusManifest.FileName));
    }

    [Fact]
    public void Ledger_AndVersionCounter_LiveOutsideTheEvaluationDirectoryAndSurviveItsErasure()
    {
        var store = new FrozenCorpusStore(this.Eval);
        Assert.Equal("v1", store.ReserveNextVersion());
        Assert.Equal("v2", store.ReserveNextVersion());
        WriteVersion(store, "v1", m => m.Exported = true);
        Assert.True(store.Destroy("v1", Now));

        Directory.Delete(this.Eval, recursive: true);

        var restarted = new FrozenCorpusStore(this.Eval);
        Assert.Equal("v1", Assert.Single(restarted.PendingDeletions()).Version);
        Assert.Equal("v3", restarted.ReserveNextVersion());
        Assert.True(File.Exists(Path.Combine(this.Ledger, "offsite-deletions.json")));
        Assert.True(File.Exists(Path.Combine(this.Ledger, "last-version")));
        Assert.False(Directory.Exists(this.Eval) && Directory.EnumerateFiles(this.Eval, "offsite-deletions.json", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void Construct_LegacyLedgerAndCounterUnderTheEvaluationDirectory_MovedOutOnStartup()
    {
        var frozen = Path.Combine(this.Eval, "frozen");
        Directory.CreateDirectory(frozen);
        File.WriteAllText(
            Path.Combine(frozen, "offsite-deletions.json"),
            """[{"version":"v4","secret":"cortex-recovery-kit","packageFileName":"cortex-eval-corpus-v4.cxpkg","destroyedAt":"2026-11-20T10:00:00+00:00"}]""");
        File.WriteAllText(Path.Combine(frozen, "last-version"), "7");

        var store = new FrozenCorpusStore(this.Eval);

        Assert.Equal("v4", Assert.Single(store.PendingDeletions()).Version);
        Assert.Equal("v8", store.ReserveNextVersion());
        Assert.False(File.Exists(Path.Combine(frozen, "offsite-deletions.json")));
        Assert.False(File.Exists(Path.Combine(frozen, "last-version")));
        Assert.True(File.Exists(Path.Combine(this.Ledger, "offsite-deletions.json")));
    }

    [Fact]
    public void Construct_LegacyLedgerAlongsideANewOne_MergedWithoutLosingEntries()
    {
        var first = new FrozenCorpusStore(this.Eval);
        WriteVersion(first, "v2", m => m.Exported = true);
        first.Destroy("v2", Now);
        var frozen = Path.Combine(this.Eval, "frozen");
        File.WriteAllText(
            Path.Combine(frozen, "offsite-deletions.json"),
            """[{"version":"v1","secret":"cortex-recovery-kit","packageFileName":"cortex-eval-corpus-v1.cxpkg","destroyedAt":"2026-11-20T10:00:00+00:00"}]""");
        File.WriteAllText(Path.Combine(frozen, "last-version"), "1");

        var store = new FrozenCorpusStore(this.Eval);

        Assert.Equal(["v1", "v2"], store.PendingDeletions().Select(p => p.Version).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(frozen, "offsite-deletions.json")));
    }
}

/// <summary>Test helper: a manifest with the fields the store tests vary.</summary>
internal sealed class FrozenCorpusManifestBuilder(string version)
{
    public DateTimeOffset From { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset To { get; set; } = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    public bool Exported { get; set; }

    public bool Imported { get; set; }

    public FrozenCorpusManifest Build() => new()
    {
        Version = version,
        CorpusFrom = this.From,
        CorpusTo = this.To,
        ExportedAt = this.Exported ? new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero) : null,
        Imported = this.Imported,
    };
}
