using System.Reflection;
using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

/// <summary>The hub's <see cref="IRecallHub"/> adapter: missing service and invalid input become <see cref="HubException"/>.</summary>
public sealed class AgentHubRecallTests
{
    private readonly AgentHub hub;

    public AgentHubRecallTests()
    {
        this.hub = (AgentHub)RuntimeHelpers.GetUninitializedObject(typeof(AgentHub));
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionAborted.Returns(CancellationToken.None);
        this.hub.Context = context;
    }

    [Fact]
    public void AgentHub_ImplementsRecallHub()
    {
        Assert.Contains(typeof(IRecallHub), typeof(IAgentHub).GetInterfaces());
        Assert.IsAssignableFrom<IRecallHub>(this.hub);
    }

    [Fact]
    public async Task GetRecallSettings_ServiceMissing_ThrowsHubException()
    {
        await Assert.ThrowsAsync<HubException>(() => this.hub.GetRecallSettings());
    }

    [Fact]
    public async Task PreviewHistoryPurge_ServiceMissing_ThrowsHubException()
    {
        typeof(AgentHub).GetField("recall", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this.hub, null);

        await Assert.ThrowsAsync<HubException>(() => this.hub.PreviewHistoryPurge(new HistoryPurgeRequestDto()));
    }
}
