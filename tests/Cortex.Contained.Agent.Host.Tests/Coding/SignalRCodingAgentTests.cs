using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Contracts.Coding;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public sealed class SignalRCodingAgentTests
{
    private static SignalRCodingAgent Build(IAgentHubClient client, int timeoutSeconds)
    {
        var accessor = Substitute.For<IBridgeClientProvider>();
        accessor.Client.Returns(client);
        return new SignalRCodingAgent(accessor, new CodingAgentOptions { BridgeInvokeTimeoutSeconds = timeoutSeconds });
    }

    [Fact]
    public async Task StartSessionAsync_Timeout_ThrowsUnreachable_NotVagueTimeout()
    {
        var client = Substitute.For<IAgentHubClient>();
        client.StartCodingSession(Arg.Any<CodingStartRequest>())
            .Returns(_ => new TaskCompletionSource<CodingStatus>().Task);

        var agent = Build(client, timeoutSeconds: 1);
        var request = new CodingStartRequest { ChannelId = "c1", WorkingFolder = "X:/wf" };

        // The agent's own 1s invoke timeout is the ceiling; the caller never cancels.
        var ex = await Assert.ThrowsAsync<CodingInvokeException>(
            () => agent.StartSessionAsync(request, CancellationToken.None));
        Assert.Equal("coda_unreachable", ex.Code);
        Assert.Contains("state is unknown", ex.Message);
    }

    [Fact]
    public async Task StartSessionAsync_BridgeThrowsEncodedError_SurfacedAsCodingInvokeException()
    {
        var client = Substitute.For<IAgentHubClient>();
        var wire = CodingErrorWire.Encode("coda_start_failed", "The coding session FAILED to start: 400 model_not_supported. No session is running.");
        client.StartCodingSession(Arg.Any<CodingStartRequest>())
            .Returns(_ => Task.FromException<CodingStatus>(new InvalidOperationException(wire)));

        var agent = Build(client, timeoutSeconds: 30);
        var request = new CodingStartRequest { ChannelId = "c1", WorkingFolder = "X:/wf" };

        var ex = await Assert.ThrowsAsync<CodingInvokeException>(
            () => agent.StartSessionAsync(request, CancellationToken.None));
        Assert.Equal("coda_start_failed", ex.Code);
        Assert.Contains("400 model_not_supported", ex.Message);
        Assert.DoesNotContain("[coda_err", ex.Message);
    }

    [Fact]
    public async Task StartSessionAsync_CallerCancels_ThrowsOperationCanceled()
    {
        var client = Substitute.For<IAgentHubClient>();
        client.StartCodingSession(Arg.Any<CodingStartRequest>())
            .Returns(_ => new TaskCompletionSource<CodingStatus>().Task);

        var agent = Build(client, timeoutSeconds: 30);
        var request = new CodingStartRequest { ChannelId = "c1", WorkingFolder = "X:/wf" };

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.StartSessionAsync(request, cts.Token));
        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task StartSessionAsync_BridgeResponds_ReturnsStatus()
    {
        var status = new CodingStatus
        {
            SessionId = "s1",
            ChannelId = "c1",
            WorkingFolder = "X:/wf",
            State = CodingSessionState.Idle,
            Policy = CodingPolicy.Prompt,
        };
        var client = Substitute.For<IAgentHubClient>();
        client.StartCodingSession(Arg.Any<CodingStartRequest>()).Returns(Task.FromResult(status));

        var agent = Build(client, timeoutSeconds: 30);
        var result = await agent.StartSessionAsync(
            new CodingStartRequest { ChannelId = "c1", WorkingFolder = "X:/wf" }, CancellationToken.None);

        Assert.Equal("s1", result.SessionId);
    }
}
