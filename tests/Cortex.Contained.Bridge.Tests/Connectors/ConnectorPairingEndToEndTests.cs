// NSubstitute ValueTask setup triggers CA2012 — suppress intentionally in test code.
#pragma warning disable CA2012
using System.Text.Json;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Pairing;
using Cortex.Contained.Bridge.Connectors.Protocol;
using Cortex.Contained.Bridge.Connectors.Security;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Connectors;

/// <summary>
/// End-to-end test through <see cref="ConnectorSession"/> + <see cref="ConnectorPairingService"/> verifying the
/// full pairing path: first connect pairing_required → approve → paired + ready; second connect with token → ready only.
/// </summary>
public sealed class ConnectorPairingEndToEndTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FullPairingPath_FirstConnectPairsAndSecondConnectUsesToken()
    {
        var tokenStore = new ConnectorTokenStore(new FakeConnectorSecretStore(), NullLogger<ConnectorTokenStore>.Instance);
        using var pairingService = BuildPairingService(tokenStore);

        // === First session: no token → pairing_required → approve → paired + ready ===
        var (session1, transport1) = BuildSession(pairingService);
        await using (session1)
        {
            transport1.QueueIncoming(HelloFrame());

            // Do NOT complete the incoming stream yet — the session parks on the pairing completion.
            var sessionTask = session1.RunAsync(CancellationToken.None);

            await WaitForFrameAsync(transport1, ConnectorFrameTypes.PairingRequired);

            var pending = await WaitForPendingRequestAsync(pairingService);
            Assert.True(pairingService.Approve(pending.RequestId));

            var pairedFrame = await WaitForFrameAsync(transport1, ConnectorFrameTypes.Paired);
            await WaitForFrameAsync(transport1, ConnectorFrameTypes.Ready);

            var token = ReadPayloadString(pairedFrame, "token");
            Assert.False(string.IsNullOrEmpty(token));

            transport1.CompleteIncoming();
            await sessionTask;

            Assert.Equal(token, tokenStore.Get(ConnectorChannelId.Create("terminal", "default"))!.Token);

            // === Second session: present the token → straight to ready, no pairing ===
            var (session2, transport2) = BuildSession(pairingService);
            await using (session2)
            {
                transport2.QueueIncoming(HelloFrame(token: token));
                transport2.CompleteIncoming();

                await session2.RunAsync(CancellationToken.None);

                var types = SentFrameTypes(transport2);
                Assert.DoesNotContain(ConnectorFrameTypes.PairingRequired, types);
                Assert.DoesNotContain(ConnectorFrameTypes.Paired, types);
                Assert.Contains(ConnectorFrameTypes.Ready, types);
            }
        }
    }

    [Fact]
    public async Task FullPairingPath_DeniedRequest_SendsPairingDeniedAndNeverAttaches()
    {
        var tokenStore = new ConnectorTokenStore(new FakeConnectorSecretStore(), NullLogger<ConnectorTokenStore>.Instance);
        using var pairingService = BuildPairingService(tokenStore);

        var registry = CreateSessionRegistry();
        var (session, transport) = BuildSession(pairingService, registry);
        await using (session)
        {
            transport.QueueIncoming(HelloFrame());
            var sessionTask = session.RunAsync(CancellationToken.None);

            await WaitForFrameAsync(transport, ConnectorFrameTypes.PairingRequired);

            var pending = await WaitForPendingRequestAsync(pairingService);
            Assert.True(pairingService.Deny(pending.RequestId, "user_refused"));

            await WaitForFrameAsync(transport, ConnectorFrameTypes.PairingDenied);

            transport.CompleteIncoming();
            await sessionTask;

            Assert.DoesNotContain(ConnectorFrameTypes.Ready, SentFrameTypes(transport));
            Assert.Empty(tokenStore.GetAll());
            await registry.DidNotReceive().TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>());
        }
    }

    private static string HelloFrame(string key = "terminal", string? token = null) =>
        ConnectorFrame.Serialize(ConnectorFrameTypes.Hello, new ConnectorHelloPayload
        {
            Key = key,
            InstanceId = "default",
            Token = token,
        });

    private static IConnectorRegistry CreateSessionRegistry()
    {
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>())
            .Returns(_ => ValueTask.CompletedTask);
        registry.DetachByChannelIdAsync(Arg.Any<string>())
            .Returns(_ => ValueTask.FromResult(false));
        return registry;
    }

    private static (ConnectorSession Session, FakeConnectorTransport Transport) BuildSession(
        IConnectorAuthenticator authenticator,
        IConnectorRegistry? registry = null)
    {
        var transport = new FakeConnectorTransport();
        var session = new ConnectorSession(
            transport,
            authenticator,
            new ConnectorSettingsConfig { Enabled = true, MaxConnectors = 16 },
            registry ?? CreateSessionRegistry(),
            NullLoggerFactory.Instance,
            TimeProvider.System,
            CreateAbortDispatcher());

        return (session, transport);
    }

    private static IConnectorAbortDispatcher CreateAbortDispatcher()
    {
        var abortDispatcher = Substitute.For<IConnectorAbortDispatcher>();
        abortDispatcher.AbortAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return abortDispatcher;
    }

    private static ConnectorPairingService BuildPairingService(ConnectorTokenStore tokenStore) =>
        new(
            tokenStore,
            new BridgeConfig { Connectors = new ConnectorSettingsConfig { RequireApproval = true } },
            CreateSessionRegistry(),
            TimeProvider.System,
            NullLogger<ConnectorPairingService>.Instance);

    private static string FrameType(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("type").GetString() ?? string.Empty;
    }

    private static string? ReadPayloadString(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("payload").GetProperty(propertyName).GetString();
    }

    private static List<string> SentFrameTypes(FakeConnectorTransport transport)
    {
        List<string> types = [];
        var count = transport.Sent.Count;
        for (var i = 0; i < count; i++)
        {
            types.Add(FrameType(transport.Sent[i]));
        }

        return types;
    }

    private static async Task<string> WaitForFrameAsync(FakeConnectorTransport transport, string type)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var count = transport.Sent.Count;
            for (var i = 0; i < count; i++)
            {
                var json = transport.Sent[i];
                if (string.Equals(FrameType(json), type, StringComparison.Ordinal))
                {
                    return json;
                }
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.Fail($"No '{type}' frame was sent within {WaitTimeout}.");
        return string.Empty;
    }

    private static async Task<ConnectorPairingRequest> WaitForPendingRequestAsync(ConnectorPairingService service)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var pending = service.GetPendingRequests();
            if (pending.Count > 0)
            {
                return pending[0];
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.Fail($"No pending pairing request appeared within {WaitTimeout}.");
        throw new InvalidOperationException("unreachable");
    }
}
