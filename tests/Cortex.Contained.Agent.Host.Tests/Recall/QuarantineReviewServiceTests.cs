using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

/// <summary>Quarantine review: list with purge source, bulk keep, bulk delete through one batched delete.</summary>
public sealed class QuarantineReviewServiceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}");
    private readonly MessageStore store;
    private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
    private readonly IMemoryManagementService memories = Substitute.For<IMemoryManagementService>();
    private readonly QuarantineReviewService review;

    public QuarantineReviewServiceTests()
    {
        this.store = new MessageStore(Path.Combine(this.dir, "messages.db"), NullLogger<MessageStore>.Instance);
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MemoryCreationTime("m1", T0.AddHours(1)),
            new MemoryCreationTime("m2", T0.AddHours(2)),
            new MemoryCreationTime("outside", T0.AddDays(9)),
        ]);
        this.memories.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new MemoryItem { MemoryId = call.Arg<string>(), Content = "content of " + call.Arg<string>() });
        this.memories.DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<string>>().Count);
        this.review = new QuarantineReviewService(new MemoryQuarantine(this.store, this.times, TimeProvider.System), this.memories);
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task PurgeAsync()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "a", T0);
        await this.store.PurgeAsync(new MessagePurgeRequest(new HistoryPurgeScope("ch", null)) { Now = T0.AddDays(2) }, CancellationToken.None);
    }

    [Fact]
    public async Task ListAsync_IncludesContentAndPurgeSource()
    {
        await this.PurgeAsync();

        var list = await this.review.ListAsync(0, 10, CancellationToken.None);

        Assert.Equal(["m2", "m1"], list.Items.Select(i => i.Memory.MemoryId));
        Assert.Equal(2, list.Total);
        Assert.All(list.Items, i => Assert.Equal("ch", i.PurgeChannelId));
        Assert.Equal("content of m2", list.Items[0].Memory.Content);
        Assert.Equal(T0.AddDays(2), list.Items[0].PurgedAt);
    }

    [Fact]
    public async Task ListAsync_Page_ReturnsOnlyThatPageAndLoadsOnlyItsContent()
    {
        await this.PurgeAsync();

        var list = await this.review.ListAsync(1, 1, CancellationToken.None);

        Assert.Equal(["m1"], list.Items.Select(i => i.Memory.MemoryId));
        Assert.Equal(2, list.Total);
        Assert.Equal(1, list.Offset);
        Assert.Equal(1, list.Limit);
        await this.memories.Received(1).GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, QuarantineReviewService.MaxPageSize + 1)]
    public async Task ListAsync_InvalidPaging_Rejected(int offset, int limit)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this.review.ListAsync(offset, limit, CancellationToken.None));
    }

    [Fact]
    public async Task ListIdsAsync_ReturnsEveryQuarantinedIdWithoutLoadingContent()
    {
        await this.PurgeAsync();

        var ids = await this.review.ListIdsAsync(CancellationToken.None);

        Assert.Equal(["m2", "m1"], ids);
        await this.memories.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_OnlyQuarantinedIds_InOneBatch()
    {
        await this.PurgeAsync();

        var result = await this.review.DeleteAsync(["m1", "m2", "outside"], CancellationToken.None);

        Assert.Equal(2, result.Affected);
        Assert.Equal(["outside"], result.Skipped);
        await this.memories.Received(1).DeleteManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 2 && ids.Contains("m1") && ids.Contains("m2")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_NothingQuarantined_DoesNotCallDelete()
    {
        var result = await this.review.DeleteAsync(["m1"], CancellationToken.None);

        Assert.Equal(0, result.Affected);
        await this.memories.DidNotReceive().DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeepAsync_KeepsQuarantinedAndTheyLeaveTheList()
    {
        await this.PurgeAsync();

        var result = await this.review.KeepAsync(["m1", "outside"], CancellationToken.None);

        Assert.Equal(1, result.Affected);
        Assert.Equal(["outside"], result.Skipped);
        Assert.Equal(["m2"], (await this.review.ListAsync(0, 10, CancellationToken.None)).Items.Select(i => i.Memory.MemoryId));
    }

    [Fact]
    public async Task KeepAsync_TooManyIds_Rejected()
    {
        var ids = Enumerable.Range(0, QuarantineReviewService.MaxBatch + 1).Select(i => "m" + i).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => this.review.KeepAsync(ids, CancellationToken.None));
    }
}
