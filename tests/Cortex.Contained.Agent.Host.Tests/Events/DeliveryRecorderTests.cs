using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>Sends record attempted → acknowledged | unknown with one operation ID (§4.1).</summary>
public sealed class DeliveryRecorderTests : IAsyncDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"deliveries-{Guid.NewGuid():N}");
    private readonly MessageStore store;
    private readonly DeliveryRecorder recorder;
    private readonly IAgentHubClient client = Substitute.For<IAgentHubClient>();
    private readonly ProactiveMessageDispatcher dispatcher;

    public DeliveryRecorderTests()
    {
        this.store = new MessageStore(Path.Combine(this.tempDir, "messages.db"), NullLogger<MessageStore>.Instance);
        this.recorder = new DeliveryRecorder(this.store, TimeProvider.System, NullLogger<DeliveryRecorder>.Instance);
        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        hubClients.Client("conn").Returns(this.client);
        var accessor = new BridgeClientAccessor(hubContext);
        accessor.SetConnectionId("conn");
        this.dispatcher = new ProactiveMessageDispatcher(accessor, this.store, NullLogger<ProactiveMessageDispatcher>.Instance, this.recorder);
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AttemptAsync_InRun_LinksRunAndItsToolResults()
    {
        var run = new RunProvenance("run_d", AuthorKinds.ScheduledTask, "t1", "ops", "scheduled task t1 (\"brief\")");
        string resultId;
        DeliveryAttempt attempt;
        using (RunScope.Begin(run))
        {
            resultId = await this.store.AppendAsync(EventDraft.ForRun(EventTypes.ToolResult, run), CancellationToken.None);
            attempt = await this.recorder.AttemptAsync("discord-dm", "proactive", CancellationToken.None);
        }

        var attempted = Assert.Single(await this.store.ListOperationAsync(attempt.OperationId, CancellationToken.None));
        Assert.Equal(EventTypes.DeliveryAttempted, attempted.Type);
        Assert.Equal("run_d", attempted.RunId);
        Assert.Equal("discord-dm", attempted.ChannelId);
        Assert.Equal([resultId], attempted.Payload["evidence"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("scheduled task t1 (\"brief\")", (string?)attempted.Payload["producer"]);
    }

    [Fact]
    public async Task DispatchAsync_Acknowledged_RecordsAttemptAndAckWithMessageRow()
    {
        this.client.OnProactiveMessage(Arg.Any<ProactiveMessage>()).Returns(new ProactiveMessageResult { Success = true, ConversationId = "c" });
        var context = new ToolExecutionContext { ConversationId = "x", ChannelId = "x" };

        var result = await this.dispatcher.DispatchAsync("discord-dm", "hello", context, CancellationToken.None);

        Assert.True(result.Success);
        var record = Assert.Single(context.ProactiveMessages.Collected);
        Assert.NotNull(record.OperationId);
        var events = await this.store.ListOperationAsync(record.OperationId, CancellationToken.None);
        Assert.Equal([EventTypes.DeliveryAttempted, EventTypes.DeliveryAcknowledged], events.Select(e => e.Type));
        Assert.True((bool)events[1].Payload["delivered"]!);
        var row = Assert.Single(await this.store.GetMessagesAsync("discord-dm"));
        Assert.Equal(row.Id, (long?)events[1].Payload["messageRowId"]);
    }

    [Fact]
    public async Task DispatchAsync_TransportFailure_RecordsUnknown()
    {
        this.client.OnProactiveMessage(Arg.Any<ProactiveMessage>()).ThrowsAsync(new TimeoutException("gone"));

        var result = await this.dispatcher.DispatchAsync("discord-dm", "hello", context: null, CancellationToken.None);

        Assert.False(result.Success);
        var unknown = await this.TypesInChannelAsync("discord-dm");
        Assert.Equal([EventTypes.DeliveryAttempted, EventTypes.DeliveryUnknown], unknown);
    }

    [Fact]
    public async Task DispatchAsync_BridgeRejects_RecordsAcknowledgedNotDelivered()
    {
        this.client.OnProactiveMessage(Arg.Any<ProactiveMessage>()).Returns(new ProactiveMessageResult { Success = false, Error = "no channel" });

        await this.dispatcher.DispatchAsync("discord-dm", "hello", context: null, CancellationToken.None);

        Assert.Equal([EventTypes.DeliveryAttempted, EventTypes.DeliveryAcknowledged], await this.TypesInChannelAsync("discord-dm"));
    }

    private async Task<IReadOnlyList<string>> TypesInChannelAsync(string channelId)
    {
        var types = new List<string>();
        await using var connection = await this.store.OpenReaderAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Type FROM Events WHERE ChannelId = @c AND Type LIKE 'delivery_%' ORDER BY Seq;";
        cmd.Parameters.AddWithValue("@c", channelId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            types.Add(reader.GetString(0));
        }

        return types;
    }
}
