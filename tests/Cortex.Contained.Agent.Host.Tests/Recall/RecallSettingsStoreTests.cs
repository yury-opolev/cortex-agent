using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Recall;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

public sealed class RecallSettingsStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "recall-settings-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(this.root, "recall-settings.json");

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
    }

    private RecallSettingsStore NewStore() => new(this.FilePath, NullLogger<RecallSettingsStore>.Instance);

    [Fact]
    public void Current_NoFile_HasNoOverrides()
    {
        using var store = this.NewStore();

        Assert.Equal(RecallOverrides.None, store.Current);
        Assert.Empty(store.Current.Names());
    }

    [Fact]
    public void Apply_ValidPatch_PersistsAndSurvivesRestart()
    {
        using (var store = this.NewStore())
        {
            var result = store.Apply(new RecallSettingsPatchDto { HistoryEnabled = true, HistorySearchMode = "Hybrid", MemorySearchMode = "hybrid", RerankHistoryTopN = 20 });
            Assert.True(result.Applied);
        }

        using var reopened = this.NewStore();
        Assert.True(reopened.Current.HistoryEnabled);
        Assert.Equal(HistorySearchMode.Hybrid, reopened.Current.HistorySearchMode);
        Assert.Equal(MemorySearchMode.Hybrid, reopened.Current.MemorySearchMode);
        Assert.Equal(20, reopened.Current.RerankHistoryTopN);
        Assert.Contains("\"historySearchMode\": \"hybrid\"", File.ReadAllText(this.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_NullMembers_KeepEarlierOverrides()
    {
        using var store = this.NewStore();
        store.Apply(new RecallSettingsPatchDto { HistoryDenseEnabled = true });

        store.Apply(new RecallSettingsPatchDto { RerankTimeoutMs = 2500 });

        Assert.True(store.Current.HistoryDenseEnabled);
        Assert.Equal(2500, store.Current.RerankTimeoutMs);
    }

    [Fact]
    public void Apply_ResetToDefaults_DropsEveryOverrideThenAppliesThePatch()
    {
        using var store = this.NewStore();
        store.Apply(new RecallSettingsPatchDto { HistoryEnabled = true, HistoryDenseEnabled = true });

        store.Apply(new RecallSettingsPatchDto { ResetToDefaults = true, MemorySearchMode = "keyword" });

        Assert.Equal(["memorySearchMode"], store.Current.Names());
    }

    [Theory]
    [InlineData("semantic", null, 0)]
    [InlineData(null, "auto", 0)]
    [InlineData(null, null, 51)]
    [InlineData("2", null, 0)]
    public void Apply_InvalidValue_RejectedAndNothingPersisted(string? historyMode, string? memoryMode, int topN)
    {
        using var store = this.NewStore();

        var result = store.Apply(new RecallSettingsPatchDto
        {
            HistorySearchMode = historyMode,
            MemorySearchMode = memoryMode,
            RerankMemoryTopN = topN == 0 ? null : topN,
        });

        Assert.False(result.Applied);
        Assert.Single(result.Errors);
        Assert.False(File.Exists(this.FilePath));
    }

    [Fact]
    public void Apply_Applied_FiresTheChangeToken()
    {
        using var store = this.NewStore();
        var token = store.GetChangeToken();

        store.Apply(new RecallSettingsPatchDto { HistoryEnabled = false });

        Assert.True(token.HasChanged);
        Assert.False(store.GetChangeToken().HasChanged);
    }

    [Fact]
    public void Current_CorruptFile_FallsBackToNoOverrides()
    {
        Directory.CreateDirectory(this.root);
        File.WriteAllText(this.FilePath, "{ not json");

        using var store = this.NewStore();

        Assert.Equal(RecallOverrides.None, store.Current);
    }
}
