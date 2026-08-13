using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Covers shutdown of the consumer loop against repeated and concurrent stop requests.
/// <para>
/// Shutdown does not arrive exactly once: the host calls <c>StopAsync</c> and disposal can follow,
/// and under WebApplicationFactory teardown both raced. <c>StopProcessingAsync</c> read the
/// cancellation source into a check, then cancelled and disposed it, so a second caller could call
/// <c>CancelAsync</c> on a source the first had already disposed — surfacing as
/// <c>ObjectDisposedException: The CancellationTokenSource has been disposed</c>. That aborted the
/// shutdown path partway through, and in the integration suite it failed the run with a non-zero
/// exit code while every test still reported as passed.
/// </para>
/// </summary>
public class AgentRuntimeShutdownTests
{
    private static AgentRuntime NewRuntime()
    {
        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([], activeChannelStore, NullLogger<ToolRegistry>.Instance);

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        hubClients.Client(Arg.Any<string>()).Returns(Substitute.For<IAgentHubClient>());
        var bridgeAccessor = new BridgeClientAccessor(hubContext);
        bridgeAccessor.SetConnectionId("test-conn");

        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());

        return new AgentRuntime(
            sessions,
            Substitute.For<ILlmClient>(),
            toolRegistry,
            sessionConfig,
            new AgentMessageChannel(),
            bridgeAccessor,
            activeChannelStore,
            Substitute.For<IHttpClientFactory>(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            NullLogger<AgentRuntime>.Instance,
            new ModelProvider(),
            imageAgingMonitor);
    }

    [Fact]
    public async Task StopProcessingAsync_CalledTwice_IsANoOpTheSecondTime()
    {
        var runtime = NewRuntime();
        await runtime.StartProcessingAsync(CancellationToken.None);

        await runtime.StopProcessingAsync(CancellationToken.None);
        await runtime.StopProcessingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopProcessingAsync_WithoutStart_IsANoOp()
    {
        // Host shutdown can run before the consumer ever started (a failure during startup).
        await NewRuntime().StopProcessingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopProcessingAsync_ConcurrentCallers_DoNotDisposeTheSourceFromUnderEachOther()
    {
        // Repeated because the failure is a race: one caller disposing the source between another
        // caller's null check and its CancelAsync. A single attempt would pass most of the time.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var runtime = NewRuntime();
            await runtime.StartProcessingAsync(CancellationToken.None);

            await Task.WhenAll(
                Task.Run(() => runtime.StopProcessingAsync(CancellationToken.None)),
                Task.Run(() => runtime.StopProcessingAsync(CancellationToken.None)),
                Task.Run(() => runtime.StopProcessingAsync(CancellationToken.None)));
        }
    }

    [Fact]
    public async Task StartProcessingAsync_AfterStop_StartsAFreshConsumer()
    {
        // Stop must leave the runtime restartable rather than permanently cancelled.
        var runtime = NewRuntime();

        await runtime.StartProcessingAsync(CancellationToken.None);
        await runtime.StopProcessingAsync(CancellationToken.None);

        await runtime.StartProcessingAsync(CancellationToken.None);
        await runtime.StopProcessingAsync(CancellationToken.None);
    }
}
