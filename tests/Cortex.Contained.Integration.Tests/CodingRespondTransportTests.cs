using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Contracts.Coding;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Contained.Integration.Tests;

/// <summary>
/// Pins the Agent → Bridge respond call as a real SignalR <b>invocation</b> rather than a
/// fire-and-forget send.
/// <para>
/// SignalR's typed-client proxy emits a send for a <c>Task</c>-returning client method and only an
/// invocation for <c>Task&lt;T&gt;</c>. A send carries no invocation id, so a failure raised by the
/// Bridge is logged there and silently dropped — which is exactly how <c>coding_session_respond</c>
/// came to report <c>accepted: true</c> for a request id no prompt was awaiting. The whole error
/// contract rests on that one return type, so this fails if anyone makes it void again.
/// </para>
/// <para>
/// Both directions share a single connection deliberately: <see cref="AgentHub"/> admits one Bridge
/// at a time, so a second concurrent connection would be rejected.
/// </para>
/// </summary>
public sealed class CodingRespondTransportTests : IClassFixture<AgentHostFactory>, IAsyncLifetime
{
    private const string BogusRequestId = "r-bogus";

    private readonly AgentHostFactory factory;
    private HubConnection? hub;

    public CodingRespondTransportTests(AgentHostFactory factory)
    {
        this.factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (this.hub is not null)
        {
            await this.hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task RespondCodingPrompt_CarriesBothTheResultAndTheFailureBackToTheAgent()
    {
        this.hub = this.factory.CreateHubConnection();

        this.hub.On<CodingRespondRequest, CodingRespondResponse>(
            nameof(IAgentHubClient.RespondCodingPrompt),
            (Func<CodingRespondRequest, Task<CodingRespondResponse>>)(req => req.RequestId == BogusRequestId
                ? throw new HubException(CodingErrorWire.Encode(
                    "unknown_request", $"No prompt is awaiting requestId '{req.RequestId}'."))
                : Task.FromResult(new CodingRespondResponse { RequestId = req.RequestId, Accepted = true })));

        await this.hub.StartAsync();

        var agent = this.factory.Services.GetRequiredService<ICodingAgent>();
        await WaitForBridgeRegistrationAsync(this.factory.Services);

        var accepted = await agent.RespondAsync(
            new CodingRespondRequest { RequestId = "r-real", Response = "allow_once" },
            CancellationToken.None);

        Assert.Equal("r-real", accepted.RequestId);
        Assert.True(accepted.Accepted);

        var ex = await Assert.ThrowsAsync<CodingInvokeException>(() => agent.RespondAsync(
            new CodingRespondRequest { RequestId = BogusRequestId, Response = "allow_once" },
            CancellationToken.None));

        Assert.Equal("unknown_request", ex.Code);

        // The session is alive — only the request id is bogus — so the agent must not be told
        // the session is gone.
        Assert.False(ex.SessionTerminated);
    }

    /// <summary>
    /// <c>StartAsync</c> returns on the handshake, which can beat the server's
    /// <c>OnConnectedAsync</c> registering the connection id — so poll rather than assume.
    /// </summary>
    private static async Task WaitForBridgeRegistrationAsync(IServiceProvider services)
    {
        var provider = services.GetRequiredService<BridgeClientAccessor>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (provider.Client is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(provider.Client);
    }
}
