using System.Reflection;
using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Scheduler;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Every hub delete/import path runs through <see cref="HistoryPurgeService"/>.</summary>
public sealed class AgentHubHistoryTests : IAsyncDisposable
{
    private static readonly string[] ClearedPair = ["a", "b"];
    private static readonly string[] ReplacedPair = ["old-1", "old-2"];
    private readonly HistoryHarness h = new();
    private readonly IAgentRuntime runtime = Substitute.For<IAgentRuntime>();
    private readonly AgentHub hub;

    public AgentHubHistoryTests()
    {
        var purge = new HistoryPurgeService(
            this.h.Store, this.h.Gate, this.h.Projector, [new MessagesWalErasureStep(this.h.Store)],
            this.h.Monitor, TimeProvider.System, NullLogger<HistoryPurgeService>.Instance);
        this.hub = (AgentHub)RuntimeHelpers.GetUninitializedObject(typeof(AgentHub));
        Set("historyPurge", purge);
        Set("runtime", this.runtime);
        Set("messageStore", this.h.Store);
        Set("logger", NullLogger<AgentHub>.Instance);
        Set("historyTools", new HistoryToolsSwitch(this.h.Monitor));
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionAborted.Returns(CancellationToken.None);
        this.hub.Context = context;
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private void Set(string field, object value) =>
        typeof(AgentHub).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this.hub, value);

    [Fact]
    public async Task DeleteConversation_PurgesThroughWorkflowAndResetsSession()
    {
        await this.h.SayAsync("a", 0, channel: "x");
        await this.h.SayAsync("b", 0, channel: "y");

        Assert.Equal(1, await this.hub.DeleteConversation("x"));

        await this.runtime.Received(1).ResetSessionAsync("x", Arg.Any<CancellationToken>());
        Assert.Empty(await this.h.Store.GetMessagesAsync("x"));
        Assert.Single(await this.h.Store.ReadQuarantineWindowsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeleteConversation_AlsoResetsSessionsThatUsedRecallTools()
    {
        await this.h.SayAsync("a", 0, channel: "x");

        await this.hub.DeleteConversation("x");

        await this.runtime.Received(1).ResetRecallExposedSessionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeHistory_ChannelScope_AlsoResetsSessionsThatUsedRecallTools()
    {
        await this.h.SayAsync("a", 0, channel: "x");

        await this.hub.PurgeHistory(new HistoryPurgeRequestDto { ChannelId = "x" });

        await this.runtime.Received(1).ResetSessionAsync("x", Arg.Any<CancellationToken>());
        await this.runtime.Received(1).ResetRecallExposedSessionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteChannelMessagesOlderThan_AlsoResetsSessionsThatUsedRecallTools()
    {
        await this.h.SayAsync("a", 0, channel: "x");

        await this.hub.DeleteChannelMessagesOlderThan("x", HistoryHarness.T0.AddDays(1));

        await this.runtime.Received(1).ResetRecallExposedSessionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeHistory_ReturnsErasureStatus()
    {
        await this.h.SayAsync("a", 0);

        var result = await this.hub.PurgeHistory(new HistoryPurgeRequestDto { OlderThan = HistoryHarness.T0.AddDays(1) });

        Assert.Equal(1, result.Deleted);
        Assert.True(result.ErasureComplete);
        await this.runtime.Received(1).ResetAllSessionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportMessages_ClearExisting_ReplacesInOneTransactionAndBumpsRestoreGeneration()
    {
        await this.h.SayAsync("old", 0);

        var result = await this.hub.ImportMessages(new ImportMessagesRequest
        {
            ClearExisting = true,
            Items =
            [
                new ExportMessageEntry
                {
                    UserId = "u", ChannelId = "ch", Role = "assistant", Content = "imported", Timestamp = HistoryHarness.T0,
                    ToolCalls = HistoryRows.ToolCalls("history_search"),
                },
            ],
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.MessagesImported);
        var row = Assert.Single(await this.h.Store.GetMessagesAsync("ch"));
        Assert.NotNull(row.ToolCalls);
        await using var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None);
        Assert.Equal(1, (await snapshot.ReadMetaAsync(CancellationToken.None)).RestoreGeneration);
    }

    [Fact]
    public async Task ImportMessages_InvalidRow_RejectedBeforeAnyChange()
    {
        await this.h.SayAsync("old", 0);

        var result = await this.hub.ImportMessages(new ImportMessagesRequest
        {
            ClearExisting = true,
            Items =
            [
                new ExportMessageEntry { UserId = "u", ChannelId = "ch", Role = "user", Content = "fine", Timestamp = HistoryHarness.T0 },
                new ExportMessageEntry { UserId = "u", ChannelId = "", Role = "user", Content = "broken", Timestamp = HistoryHarness.T0 },
            ],
        });

        Assert.False(result.Success);
        Assert.Contains("row 1", result.Error, StringComparison.Ordinal);
        Assert.Equal("old", Assert.Single(await this.h.Store.GetMessagesAsync("ch")).Content);
        await this.runtime.DidNotReceive().ResetAllSessionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAll_InvalidMessageRow_RejectedBeforeMemoriesAreTouched()
    {
        // No memory service is wired: reaching the memory import would fail with a different error.
        var result = await this.hub.ImportAll(new ExportBundle
        {
            Memories = new ExportMemoriesPayload { Items = [] },
            Messages = new ExportMessagesPayload
            {
                Items = [new ExportMessageEntry { UserId = "u", ChannelId = "", Role = "user", Content = "broken", Timestamp = HistoryHarness.T0 }],
            },
            Tasks = new ExportTasksPayload { Items = [] },
        });

        Assert.False(result.Success);
        Assert.Contains("row 0", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportMemoriesThenMessages_DoesNotQuarantineTheImportedMemories()
    {
        var created = new Dictionary<string, DateTimeOffset>();
        var memoryService = Substitute.For<Cortex.Contained.Agent.Host.Memory.IMemoryManagementService>();
        memoryService.CreateAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = "imported-" + created.Count;
                created[id] = DateTimeOffset.UtcNow;
                return new MemoryItem { MemoryId = id, Content = call.ArgAt<string>(0) };
            });
        var times = Substitute.For<Cortex.Contained.Agent.Host.Memory.IMemoryCreationTimes>();
        times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            created.Select(kv => new Cortex.Contained.Agent.Host.Memory.MemoryCreationTime(kv.Key, kv.Value)).ToList());
        var quarantine = new Cortex.Contained.Agent.Host.Memory.MemoryQuarantine(this.h.Store, times, TimeProvider.System);
        Set("memoryService", memoryService);
        Set("memoryQuarantine", quarantine);
        await this.h.Store.SaveMessageAsync("u", "ch", "user", "recent", DateTimeOffset.UtcNow.AddMinutes(-5));

        var memories = await this.hub.ImportMemories(new ImportMemoriesRequest
        {
            ClearExisting = false,
            Items = [new MemoryCreateRequest { Content = "kept fact", Tags = [] }],
        });
        var messages = await this.hub.ImportMessages(new ImportMessagesRequest
        {
            ClearExisting = true,
            Items = [new ExportMessageEntry { UserId = "u", ChannelId = "ch", Role = "user", Content = "imported", Timestamp = HistoryHarness.T0 }],
        });

        Assert.True(memories.Success, memories.Error);
        Assert.True(messages.Success, messages.Error);
        Assert.Single(created);
        Assert.Single(await this.h.Store.ReadQuarantineWindowsAsync(CancellationToken.None));
        Assert.Empty(await quarantine.GetQuarantinedIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ImportAll_ReplacesEverythingWithoutQuarantiningTheImportedMemories()
    {
        var created = new Dictionary<string, DateTimeOffset>();
        var memoryService = Substitute.For<Cortex.Contained.Agent.Host.Memory.IMemoryManagementService>();
        memoryService.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryListResult { Items = [], TotalCount = 0 });
        memoryService.CreateAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = "imported-" + created.Count;
                created[id] = DateTimeOffset.UtcNow;
                return new MemoryItem { MemoryId = id, Content = call.ArgAt<string>(0) };
            });
        var times = Substitute.For<Cortex.Contained.Agent.Host.Memory.IMemoryCreationTimes>();
        times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            created.Select(kv => new Cortex.Contained.Agent.Host.Memory.MemoryCreationTime(kv.Key, kv.Value)).ToList());
        var quarantine = new Cortex.Contained.Agent.Host.Memory.MemoryQuarantine(this.h.Store, times, TimeProvider.System);
        var schedulerDirectory = Path.Combine(this.h.Root, "scheduler-data");
        using var scheduler = new SchedulerService(new AgentMessageChannel(), schedulerDirectory, NullLogger<SchedulerService>.Instance);
        Set("memoryService", memoryService);
        Set("memoryQuarantine", quarantine);
        Set("scheduler", scheduler);
        await this.h.Store.SaveMessageAsync("u", "ch", "user", "recent", DateTimeOffset.UtcNow.AddMinutes(-5));
        var before = DateTimeOffset.UtcNow;

        var result = await this.hub.ImportAll(new ExportBundle
        {
            Memories = new ExportMemoriesPayload { Items = [new MemoryItem { MemoryId = "old", Content = "kept fact" }] },
            Messages = new ExportMessagesPayload
            {
                Items = [new ExportMessageEntry { UserId = "u", ChannelId = "ch", Role = "user", Content = "imported", Timestamp = HistoryHarness.T0 }],
            },
            Tasks = new ExportTasksPayload { Items = [] },
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal((1, 1), (result.MemoriesImported, result.MessagesImported));
        Assert.Equal("imported", Assert.Single(await this.h.Store.GetMessagesAsync("ch")).Content);
        var window = Assert.Single(await this.h.Store.ReadQuarantineWindowsAsync(CancellationToken.None));
        Assert.True(window.To <= DateTimeOffset.UtcNow && window.To >= before.AddSeconds(-5) && window.To <= created.Values.Min(), "the window must end where the import began, before the first imported memory");
        Assert.Empty(await quarantine.GetQuarantinedIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClearAllMemories_DeletesInOneBatch()
    {
        var memoryService = Substitute.For<Cortex.Contained.Agent.Host.Memory.IMemoryManagementService>();
        memoryService.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryListResult { Items = [new MemoryItem { MemoryId = "a", Content = "x" }, new MemoryItem { MemoryId = "b", Content = "y" }], TotalCount = 2 });
        Set("memoryService", memoryService);

        await this.hub.ClearAllMemories();

        await memoryService.Received(1).DeleteManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(ClearedPair)), Arg.Any<CancellationToken>());
        await memoryService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportMemories_ClearExisting_ReplacesInOneBatchDelete()
    {
        var memoryService = Substitute.For<Cortex.Contained.Agent.Host.Memory.IMemoryManagementService>();
        memoryService.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryListResult { Items = [new MemoryItem { MemoryId = "old-1", Content = "x" }, new MemoryItem { MemoryId = "old-2", Content = "y" }], TotalCount = 2 });
        Set("memoryService", memoryService);

        var result = await this.hub.ImportMemories(new ImportMemoriesRequest { ClearExisting = true, Items = [] });

        Assert.True(result.Success, result.Error);
        await memoryService.Received(1).DeleteManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(ReplacedPair)), Arg.Any<CancellationToken>());
        await memoryService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackfillHistoryEvents_ReportsScannedAndInserted()
    {
        await this.h.SayAsync("a", 0);
        Set("eventBackfill", new Cortex.Contained.Agent.Host.Events.EventBackfillService(
            this.h.Store, this.h.Gate, TimeProvider.System, NullLogger<Cortex.Contained.Agent.Host.Events.EventBackfillService>.Instance));

        var result = await this.hub.BackfillHistoryEvents();

        Assert.Equal(1, result.Scanned);
        Assert.Equal(0, result.Inserted);
    }

    [Fact]
    public async Task SetHistoryRecallTools_ReturnsEffectiveState()
    {
        Assert.False(await this.hub.SetHistoryRecallTools(false));
        Assert.True(await this.hub.SetHistoryRecallTools(true));
    }
}
