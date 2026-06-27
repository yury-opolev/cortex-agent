using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Bridge.Tests.Coding;

public class CodingHubBinderGuardTests
{
    [Fact]
    public async Task Guard_CodingAgentException_BecomesWireEncodedHubException()
    {
        var hub = await Assert.ThrowsAsync<HubException>(() =>
            CodingHubBinder.Guard<CodingStatus>(() =>
                throw new CodingAgentException(CodingAgentErrorCodes.StartFailed, "coda failed to start: boom. No session is running.")));

        Assert.True(CodingErrorWire.TryDecode(hub.Message, out var code, out var message));
        Assert.Equal("coda_start_failed", code);
        Assert.Contains("boom", message);
    }

    [Fact]
    public async Task Guard_Success_PassesThrough()
    {
        var status = new CodingStatus { SessionId = "s1", ChannelId = "c1", WorkingFolder = "X", State = CodingSessionState.Idle, Policy = CodingPolicy.Prompt };
        var result = await CodingHubBinder.Guard(() => Task.FromResult(status));
        Assert.Equal("s1", result.SessionId);
    }
}
