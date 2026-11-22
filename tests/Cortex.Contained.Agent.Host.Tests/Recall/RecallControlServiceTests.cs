using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Recall;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Tests.History;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

public sealed class RecallControlServiceTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();
    private readonly ServiceProvider provider;
    private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
    private readonly RecallControlService recall;

    public RecallControlServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<HistoryOptions>(o =>
        {
            o.TimeZone = "UTC";
            o.Enabled = false;
            o.Dense.Enabled = false;
        });
        services.Configure<RerankerOptions>(_ => { });
        services.Configure<MemoryMcpOptions>(_ => { });
        services.AddRecallSettings(Path.Combine(this.h.Root, "state"));
        this.provider = services.BuildServiceProvider();

        var historyMonitor = this.provider.GetRequiredService<IOptionsMonitor<HistoryOptions>>();
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns([new MemoryCreationTime("m1", HistoryHarness.T0.AddHours(1))]);
        var quarantine = new MemoryQuarantine(this.h.Store, this.times, TimeProvider.System);
        var memories = Substitute.For<IMemoryManagementService>();
        var purge = new HistoryPurgeService(
            this.h.Store, this.h.Gate, this.h.Projector, [], this.h.Monitor, TimeProvider.System, NullLogger<HistoryPurgeService>.Instance, quarantine);
        this.recall = new RecallControlService(
            this.provider.GetRequiredService<RecallSettingsStore>(),
            historyMonitor,
            this.provider.GetRequiredService<IOptionsMonitor<RerankerOptions>>(),
            this.provider.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>(),
            new HistoryHealthReporter(this.h.Store, this.h.Store, this.h.Status, new HistoryToolsSwitch(historyMonitor), historyMonitor),
            () => new RerankHealth(false, "off", 0, null, null, null, null),
            quarantine,
            new QuarantineReviewService(quarantine, memories),
            purge);
    }

    public async ValueTask DisposeAsync()
    {
        await this.provider.DisposeAsync();
        await this.h.DisposeAsync();
    }

    [Fact]
    public void Update_EnablesHistory_AppliesLiveAndExplains()
    {
        var result = this.recall.Update(new RecallSettingsPatchDto { HistoryEnabled = true, HistorySearchMode = "hybrid" });

        Assert.True(result.Applied);
        Assert.True(result.Settings.HistoryEnabled);
        Assert.Equal(["historyEnabled", "historySearchMode"], result.Settings.Overridden);
        Assert.Contains(result.Notices, n => n.StartsWith("History search is on.", StringComparison.Ordinal));
        Assert.Contains(result.Notices, n => n.StartsWith("Hybrid history search needs dense embeddings", StringComparison.Ordinal));
        Assert.True(this.recall.GetSettings().HistoryEnabled);
    }

    [Fact]
    public void Update_Invalid_NotAppliedAndSettingsUnchanged()
    {
        var result = this.recall.Update(new RecallSettingsPatchDto { HistoryEnabled = true, RerankTimeoutMs = 5 });

        Assert.False(result.Applied);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Notices);
        Assert.False(result.Settings.HistoryEnabled);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsHistoryAndQuarantine()
    {
        await this.h.SayAsync("a", 0, channel: "x");
        await this.h.Store.PurgeAsync(new MessagePurgeRequest(new HistoryPurgeScope("x", null)) { Now = HistoryHarness.T0.AddDays(2) }, CancellationToken.None);

        var status = await this.recall.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, status.QuarantinedMemories);
        Assert.False(status.History.Enabled);
        Assert.Equal("off", status.Reranker.State);
        Assert.False(status.EventBackfill.Running);
    }

    [Fact]
    public async Task PreviewPurgeAsync_InvertedRange_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this.recall.PreviewPurgeAsync(
            new HistoryPurgeRequestDto { From = HistoryHarness.T0, OlderThan = HistoryHarness.T0.AddHours(-1) }, CancellationToken.None));
    }

    [Fact]
    public void Notices_DenseToggledWhileHistoryOn_SaysTheIndexIsRebuilt()
    {
        var before = new RecallSettingsDto { HistoryEnabled = true, HistoryDenseEnabled = false, HistorySearchMode = "keyword" };

        var notices = RecallNotices.For(before, before with { HistoryDenseEnabled = true });

        Assert.Equal(["Dense embeddings are on. The history index is rebuilt and then embedded in the background; hybrid search starts once chunks are embedded."], notices);
    }

    [Fact]
    public void Notices_HistorySwitchedOff_SaysToolsHiddenAndIndexKeptUntilNextDeletion()
    {
        var before = new RecallSettingsDto { HistoryEnabled = true, HistorySearchMode = "keyword" };

        var notice = Assert.Single(RecallNotices.For(before, before with { HistoryEnabled = false }));

        Assert.Contains("erased by the next history deletion", notice, StringComparison.Ordinal);
    }
}
