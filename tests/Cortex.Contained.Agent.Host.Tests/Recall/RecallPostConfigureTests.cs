using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Recall;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

public sealed class RecallPostConfigureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "recall-options-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider provider;

    public RecallPostConfigureTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<HistoryOptions>(o => o.TimeZone = "UTC");
        services.Configure<RerankerOptions>(_ => { });
        services.Configure<MemoryMcpOptions>(_ => { });
        services.AddRecallSettings(this.root);
        this.provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        this.provider.Dispose();
        try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
    }

    private RecallSettingsStore Store => this.provider.GetRequiredService<RecallSettingsStore>();

    [Fact]
    public void CurrentValue_NoOverrides_KeepsConfiguredDefaults()
    {
        var history = this.provider.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue;
        var memory = this.provider.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>().CurrentValue;

        Assert.True(history.Enabled);
        Assert.True(history.Dense.Enabled);
        Assert.Equal(HistorySearchMode.Hybrid, history.DefaultSearchMode);
        Assert.Equal(MemorySearchMode.Hybrid, memory.Search.DefaultMode);
    }

    [Fact]
    public void Apply_UpdatesEveryMonitorLive()
    {
        var history = this.provider.GetRequiredService<IOptionsMonitor<HistoryOptions>>();
        var reranker = this.provider.GetRequiredService<IOptionsMonitor<RerankerOptions>>();
        var memory = this.provider.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>();
        _ = history.CurrentValue;
        _ = reranker.CurrentValue;
        _ = memory.CurrentValue;

        this.Store.Apply(new RecallSettingsPatchDto
        {
            HistoryEnabled = true,
            HistoryDenseEnabled = true,
            HistorySearchMode = "keyword",
            MemorySearchMode = "hybrid",
            RerankHistoryTopN = 10,
            RerankMemoryMaxDocChars = 800,
            RerankTimeoutMs = 4000,
        });

        Assert.True(history.CurrentValue.Enabled);
        Assert.True(history.CurrentValue.Dense.Enabled);
        Assert.Equal(HistorySearchMode.Keyword, history.CurrentValue.DefaultSearchMode);
        Assert.Equal("UTC", history.CurrentValue.TimeZone);
        Assert.Equal(MemorySearchMode.Hybrid, memory.CurrentValue.Search.DefaultMode);
        Assert.Equal(10, reranker.CurrentValue.History.TopN);
        Assert.Equal(800, reranker.CurrentValue.Memory.MaxDocChars);
        Assert.Equal(4000, reranker.CurrentValue.TimeoutMs);
        Assert.Empty(reranker.CurrentValue.Validate());
    }

    [Fact]
    public void Apply_Reset_RestoresConfiguredDefaults()
    {
        var history = this.provider.GetRequiredService<IOptionsMonitor<HistoryOptions>>();
        this.Store.Apply(new RecallSettingsPatchDto { HistoryEnabled = false });
        Assert.False(history.CurrentValue.Enabled);

        this.Store.Apply(new RecallSettingsPatchDto { ResetToDefaults = true });

        Assert.True(history.CurrentValue.Enabled);
    }
}
