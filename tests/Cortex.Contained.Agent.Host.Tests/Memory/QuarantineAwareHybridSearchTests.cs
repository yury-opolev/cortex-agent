using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Memory;

/// <summary>D10 for memory_search: quarantined memories are excluded before scoring, in every mode.</summary>
public class QuarantineAwareHybridSearchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] Expected = ["inside", "already"];

    private readonly IHybridMemorySearch inner = Substitute.For<IHybridMemorySearch>();
    private readonly IMemoryQuarantineSource source = Substitute.For<IMemoryQuarantineSource>();
    private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
    private readonly QuarantineAwareHybridSearch search;

    public QuarantineAwareHybridSearchTests()
    {
        this.source.ReadKeptMemoryIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<string>());
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MemoryCreationTime("inside", T0.AddHours(1)),
            new MemoryCreationTime("outside", T0.AddDays(-5)),
        ]);
        this.inner.SearchAsync(Arg.Any<HybridSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HybridSearchResult([], MemorySearchMode.Hybrid, null, null));
        this.search = new QuarantineAwareHybridSearch(this.inner, new MemoryQuarantine(this.source, this.times, TimeProvider.System));
    }

    [Fact]
    public async Task SearchAsync_QuarantinedIds_JoinTheExcludedIds()
    {
        this.source.ReadQuarantineWindowsAsync(Arg.Any<CancellationToken>()).Returns([new QuarantineWindow(T0, T0.AddDays(1))]);
        var request = new HybridSearchRequest
        {
            Query = "q",
            Mode = MemorySearchMode.Hybrid,
            Filter = new MemorySearchFilter { Tags = ["project"], ExcludeIds = new HashSet<string> { "already" } },
        };

        await this.search.SearchAsync(request, CancellationToken.None);

        await this.inner.Received(1).SearchAsync(
            Arg.Is<HybridSearchRequest>(r => r.Filter.ExcludeIds!.SetEquals(Expected) && r.Filter.Tags![0] == "project"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_NothingQuarantined_PassesTheRequestThrough()
    {
        this.source.ReadQuarantineWindowsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var request = new HybridSearchRequest { Query = "q", Mode = MemorySearchMode.Dense };

        await this.search.SearchAsync(request, CancellationToken.None);

        await this.inner.Received(1).SearchAsync(request, Arg.Any<CancellationToken>());
    }
}
