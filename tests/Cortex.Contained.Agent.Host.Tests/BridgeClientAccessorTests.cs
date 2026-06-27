using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Tests;

public class BridgeClientAccessorTests
{
    private static (BridgeClientAccessor accessor, IHubClients<IAgentHubClient> hubClients) CreateAccessor()
    {
        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        return (new BridgeClientAccessor(hubContext), hubClients);
    }

    [Fact]
    public void Client_InitiallyNull()
    {
        var (accessor, _) = CreateAccessor();

        Assert.Null(accessor.Client);
    }

    [Fact]
    public void SetConnectionId_StoresReference()
    {
        var (accessor, hubClients) = CreateAccessor();
        var mockClient = Substitute.For<IAgentHubClient>();
        hubClients.Client("conn-1").Returns(mockClient);

        accessor.SetConnectionId("conn-1");

        Assert.Same(mockClient, accessor.Client);
    }

    [Fact]
    public void ClearConnection_RemovesReference()
    {
        var (accessor, hubClients) = CreateAccessor();
        var mockClient = Substitute.For<IAgentHubClient>();
        hubClients.Client("conn-1").Returns(mockClient);

        accessor.SetConnectionId("conn-1");
        Assert.NotNull(accessor.Client);

        accessor.ClearConnection("conn-1");
        Assert.Null(accessor.Client);
    }

    [Fact]
    public void ClearConnection_WrongId_DoesNotClear()
    {
        var (accessor, hubClients) = CreateAccessor();
        var mockClient = Substitute.For<IAgentHubClient>();
        hubClients.Client("conn-1").Returns(mockClient);

        accessor.SetConnectionId("conn-1");
        accessor.ClearConnection("conn-wrong"); // wrong ID — should not clear

        Assert.Same(mockClient, accessor.Client);
        Assert.Equal("conn-1", accessor.CurrentConnectionId);
    }

    [Fact]
    public void CurrentConnectionId_ReflectsState()
    {
        var (accessor, _) = CreateAccessor();

        Assert.Null(accessor.CurrentConnectionId);

        accessor.SetConnectionId("conn-1");
        Assert.Equal("conn-1", accessor.CurrentConnectionId);

        accessor.ClearConnection("conn-1");
        Assert.Null(accessor.CurrentConnectionId);
    }

    [Fact]
    public async Task Client_IsThreadSafe()
    {
        // BridgeClientAccessor uses volatile, so reads/writes are visible across threads.
        var (accessor, hubClients) = CreateAccessor();
        var mockClient = Substitute.For<IAgentHubClient>();
        hubClients.Client("conn-1").Returns(mockClient);

        await Task.Run(() => accessor.SetConnectionId("conn-1"));

        // After the set completes on another thread, the read should see it
        Assert.Same(mockClient, accessor.Client);
    }
}
