using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>A locked (key-less) encrypted store: health answers, background services wait, nothing halts.</summary>
public sealed class StorageReadinessTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"storage-ready-{Guid.NewGuid():N}");
    private readonly IOptionsMonitor<HistoryOptions> monitor = Substitute.For<IOptionsMonitor<HistoryOptions>>();
    private readonly StorageKeyRing ring = new();
    private readonly MessageStore store;

    public StorageReadinessTests()
    {
        this.monitor.CurrentValue.Returns(new HistoryOptions { Enabled = true, TimeZone = "UTC" });
        this.store = new MessageStore(
            Path.Combine(this.root, "messages.db"),
            NullLogger<MessageStore>.Instance,
            new MessageStoreEncryption(this.ring, TimeSpan.FromSeconds(30)));
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task HealthSnapshot_Locked_AnswersImmediatelyWithAwaitingKey()
    {
        var reporter = new HistoryHealthReporter(this.store, this.store, new HistoryStatus(), new HistoryToolsSwitch(this.monitor), this.monitor, this.store);

        var health = await reporter.SnapshotAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("awaitingKey", health.Storage);
    }

    [Fact]
    public async Task ProjectorService_WaitsForTheKeyInsteadOfHalting()
    {
        var status = new HistoryStatus();
        using var projector = new HistoryProjector(
            this.store, this.store, new HistoryIndexGenerations(Path.Combine(this.root, "history"), this.ring),
            new HistoryAccessGate(), this.monitor, status, NullLogger<HistoryProjector>.Instance);
        using var service = new HistoryProjectorService(projector, status, this.monitor, NullLogger<HistoryProjectorService>.Instance, TimeSpan.FromMilliseconds(10), this.store);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.False(projector.IsInitialized);
        Assert.False(status.Snapshot().Halted);

        this.ring.Provide(Convert.ToBase64String(Enumerable.Range(3, 32).Select(i => (byte)i).ToArray()));
        var deadline = Environment.TickCount64 + 5000;
        while (!projector.IsInitialized && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }

        await service.StopAsync(CancellationToken.None);
        Assert.True(projector.IsInitialized);
        Assert.Equal(StorageState.Open, this.store.State);
    }

    [Fact]
    public async Task ProjectorService_WrongKey_HaltsWithoutCrashingTheHost()
    {
        this.ring.Provide(Convert.ToBase64String(Enumerable.Range(3, 32).Select(i => (byte)i).ToArray()));
        await using (var seed = new MessageStore(
            Path.Combine(this.root, "messages.db"),
            NullLogger<MessageStore>.Instance,
            new MessageStoreEncryption(this.ring, TimeSpan.FromSeconds(5))))
        {
            await seed.SaveMessageAsync("u", "ch", "user", "x", DateTimeOffset.UtcNow);
        }

        SqliteConnection.ClearAllPools();
        var otherRing = new StorageKeyRing();
        otherRing.Provide(Convert.ToBase64String(Enumerable.Range(9, 32).Select(i => (byte)i).ToArray()));
        await using var wrong = new MessageStore(
            Path.Combine(this.root, "messages.db"),
            NullLogger<MessageStore>.Instance,
            new MessageStoreEncryption(otherRing, TimeSpan.FromSeconds(5)));
        var status = new HistoryStatus();
        using var projector = new HistoryProjector(
            wrong, wrong, new HistoryIndexGenerations(Path.Combine(this.root, "history"), otherRing),
            new HistoryAccessGate(), this.monitor, status, NullLogger<HistoryProjector>.Instance);
        using var service = new HistoryProjectorService(projector, status, this.monitor, NullLogger<HistoryProjectorService>.Instance, TimeSpan.FromMilliseconds(10), wrong);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(status.Snapshot().Halted);
        Assert.Equal(StorageState.Failed, wrong.State);
    }

    [Fact]
    public async Task ToolExecution_StoreAwaitingKey_CompletesWithoutWaitingForTheKey()
    {
        var tool = Substitute.For<IAgentTool>();
        tool.Name.Returns("date_time");
        tool.ExecuteAsync(Arg.Any<string>(), Arg.Any<ToolExecutionContext>(), Arg.Any<CancellationToken>()).Returns(AgentToolResult.Ok("now"));
        var secrets = new KnownSecrets();
        secrets.Load(new LlmCredentials { Providers = [] });
        var registry = new ToolRegistry(
            [tool],
            new ActiveChannelStore(),
            NullLogger<ToolRegistry>.Instance,
            eventRecorder: new ToolEventRecorder(this.store, new ToolEventPayloads(secrets), NullLogger<ToolEventRecorder>.Instance, this.store));

        AgentToolResult result;
        using (RunScope.Begin(new RunProvenance("run_l", AuthorKinds.Main, null, "ch", null)))
        {
            result = await registry
                .ExecuteAsync(new LlmToolCall { Id = "c1", Name = "date_time", Arguments = "{}" }, new ToolExecutionContext { ConversationId = "ch", ChannelId = "ch" }, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(result.Success);
        Assert.Null(result.EvidenceId);
        Assert.Equal(StorageState.AwaitingKey, this.store.State);
    }

    [Fact]
    public async Task DeliveryRecording_StoreAwaitingKey_ReturnsWithoutWaitingForTheKey()
    {
        var recorder = new DeliveryRecorder(this.store, TimeProvider.System, NullLogger<DeliveryRecorder>.Instance, this.store);
        var builder = new DeliveryReportBuilder(this.store, NullLogger<DeliveryReportBuilder>.Instance, this.store);

        var attempt = await recorder.AttemptAsync("discord-dm", "proactive", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await recorder.AcknowledgedAsync(attempt, "discord-dm", delivered: true, messageRowId: null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var report = await builder
            .BuildAsync(new ProactiveMessageRecord { ChannelId = "discord-dm", Text = "x", OperationId = attempt.OperationId }, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(report.EvidenceAvailable);
    }
}
