using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>Direct replies are sends too: attempted → acknowledged | unknown.</summary>
public sealed class ReplySendEventTests : IAsyncDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"reply-sends-{Guid.NewGuid():N}");
    private readonly MessageStore store;
    private readonly IAgentHubClient client = Substitute.For<IAgentHubClient>();
    private readonly TurnResponseDelivery delivery;

    public ReplySendEventTests()
    {
        this.store = new MessageStore(Path.Combine(this.tempDir, "messages.db"), NullLogger<MessageStore>.Instance);
        this.delivery = new TurnResponseDelivery(
            this.client, this.store, "reply-conv", "chan-1", "corr-1", useProactiveDelivery: false, NullLogger.Instance,
            new DeliveryRecorder(this.store, TimeProvider.System, NullLogger<DeliveryRecorder>.Instance));
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<List<(string Type, string? Payload)>> DeliveryEventsAsync()
    {
        var events = new List<(string, string?)>();
        await using var connection = await this.store.OpenReaderAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Type, Payload FROM Events WHERE Type LIKE 'delivery_%' ORDER BY Seq;";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add((reader.GetString(0), reader.GetString(1)));
        }

        return events;
    }

    [Fact]
    public async Task DeliverFinalResponseAsync_Sent_RecordsAttemptedAndAcknowledged()
    {
        await this.delivery.DeliverFinalResponseAsync(new AgentSession("chan-1"), "hello", null, null, 1, "m1", CancellationToken.None);

        var events = await this.DeliveryEventsAsync();
        Assert.Equal([EventTypes.DeliveryAttempted, EventTypes.DeliveryAcknowledged], events.Select(e => e.Type));
        Assert.Contains("\"via\":\"reply\"", events[0].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliverFinalResponseAsync_TransportFails_RecordsUnknownAndRethrows()
    {
        this.client.OnResponseComplete(Arg.Any<ResponseCompleteMessage>()).ThrowsAsync(new IOException("socket closed"));

        await Assert.ThrowsAsync<IOException>(() =>
            this.delivery.DeliverFinalResponseAsync(new AgentSession("chan-1"), "hello", null, null, 1, "m1", CancellationToken.None));

        Assert.Equal([EventTypes.DeliveryAttempted, EventTypes.DeliveryUnknown], (await this.DeliveryEventsAsync()).Select(e => e.Type));
    }
}
