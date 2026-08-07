// NSubstitute ValueTask setup triggers CA2012 — suppress intentionally in test code.
#pragma warning disable CA2012
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Pairing;
using Cortex.Contained.Bridge.Connectors.Security;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class ConnectorPairingServiceTests
{
    private static BridgeConfig RequireApprovalConfig() => new()
    {
        Connectors = new ConnectorSettingsConfig { RequireApproval = true },
    };

    private static BridgeConfig AutoApproveConfig() => new()
    {
        Connectors = new ConnectorSettingsConfig { RequireApproval = false },
    };

    // ── Approved with valid token ─────────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_ValidToken_ReturnsApprovedWithNoNewToken()
    {
        var channelId = ConnectorChannelId.Create("terminal", "default");
        var token = ConnectorTokenGenerator.CreateToken();
        var store = BuildTokenStore();
        store.Save(MakeRecord(channelId, token));

        var (svc, _, _) = BuildService(tokenStore: store);

        var result = await svc.AuthenticateAsync(MakeRequest(token: token), CancellationToken.None);

        Assert.Equal(ConnectorAuthOutcome.Approved, result.Outcome);
        Assert.Null(result.IssuedToken);
    }

    [Fact]
    public async Task AuthenticateAsync_ValidToken_UpdatesLastSeen()
    {
        var channelId = ConnectorChannelId.Create("terminal", "default");
        var token = ConnectorTokenGenerator.CreateToken();
        var store = BuildTokenStore();
        store.Save(MakeRecord(channelId, token));

        var (svc, _, clock) = BuildService(tokenStore: store);
        clock.Advance(TimeSpan.FromMinutes(1));

        await svc.AuthenticateAsync(MakeRequest(token: token), CancellationToken.None);

        Assert.Equal(clock.GetUtcNow(), store.Get(channelId)!.LastSeenAt);
    }

    // ── Disabled connector ────────────────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_DisabledConnector_ReturnsDenied()
    {
        var channelId = ConnectorChannelId.Create("terminal", "default");
        var store = BuildTokenStore();
        store.Save(MakeRecord(channelId, "tok") with { Enabled = false });

        var (svc, _, _) = BuildService(tokenStore: store);

        var result = await svc.AuthenticateAsync(MakeRequest(token: "tok"), CancellationToken.None);

        Assert.Equal(ConnectorAuthOutcome.Denied, result.Outcome);
        Assert.Equal("connector_disabled", result.Reason);
    }

    // ── Token mismatch falls through to pairing ───────────────────────

    [Fact]
    public async Task AuthenticateAsync_WrongToken_StartsNewPairingFlow()
    {
        var channelId = ConnectorChannelId.Create("terminal", "default");
        var store = BuildTokenStore();
        store.Save(MakeRecord(channelId, "correct-token"));

        var (svc, _, _) = BuildService(tokenStore: store);

        var result = await svc.AuthenticateAsync(MakeRequest(token: "wrong-token"), CancellationToken.None);

        Assert.Equal(ConnectorAuthOutcome.PairingRequired, result.Outcome);
    }

    // ── RequireApproval=false auto-approves ───────────────────────────

    [Fact]
    public async Task AuthenticateAsync_AutoApproveConfig_ReturnsApprovedWithToken()
    {
        var (svc, _, _) = BuildService(config: AutoApproveConfig());

        var result = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal(ConnectorAuthOutcome.Approved, result.Outcome);
        Assert.NotNull(result.IssuedToken);
    }

    [Fact]
    public async Task AuthenticateAsync_AutoApproveConfig_PersistsIssuedToken()
    {
        var store = BuildTokenStore();
        var (svc, _, _) = BuildService(config: AutoApproveConfig(), tokenStore: store);

        var result = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);

        var saved = store.Get(ConnectorChannelId.Create("terminal", "default"));
        Assert.NotNull(saved);
        Assert.Equal(result.IssuedToken, saved.Token);
    }

    // ── Pairing flow: pending request created ─────────────────────────

    [Fact]
    public async Task AuthenticateAsync_NoPriorRecord_ReturnsPairingRequired()
    {
        var (svc, _, _) = BuildService();

        var result = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal(ConnectorAuthOutcome.PairingRequired, result.Outcome);
        Assert.NotNull(result.PairingCode);
        Assert.NotNull(result.PairingCompletion);
    }

    [Fact]
    public async Task GetPendingRequests_AfterPairingStarted_ExposesRequestDetails()
    {
        var (svc, _, clock) = BuildService();

        var result = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);

        var pending = Assert.Single(svc.GetPendingRequests());
        Assert.Equal(ConnectorChannelId.Create("terminal", "default"), pending.ChannelId);
        Assert.Equal("terminal", pending.Key);
        Assert.Equal("default", pending.InstanceId);
        Assert.Equal("Terminal", pending.DisplayName);
        Assert.Equal("127.0.0.1:9000", pending.RemoteEndpoint);
        Assert.Equal(result.PairingCode, pending.Code);
        Assert.Equal(clock.GetUtcNow(), pending.RequestedAt);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), pending.ExpiresAt);
    }

    // ── Single-flight: two connects share one code ────────────────────

    [Fact]
    public async Task AuthenticateAsync_TwoConcurrentConnects_ShareSameCode()
    {
        var (svc, _, _) = BuildService();

        var r1 = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);
        var r2 = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal(r1.PairingCode, r2.PairingCode);
        Assert.Same(r1.PairingCompletion, r2.PairingCompletion);
        Assert.Single(svc.GetPendingRequests());
    }

    // ── Approve: completion resolves with Approved ────────────────────

    [Fact]
    public async Task Approve_PendingRequest_ResolvesCompletionWithApproved()
    {
        var (svc, _, _) = BuildService();
        var authResult = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);
        var pending = svc.GetPendingRequests();

        var approved = svc.Approve(pending[0].RequestId);
        var completion = await authResult.PairingCompletion!;

        Assert.True(approved);
        Assert.Equal(ConnectorAuthOutcome.Approved, completion.Outcome);
        Assert.NotNull(completion.IssuedToken);
    }

    [Fact]
    public async Task Approve_PendingRequest_PersistsRecordWithIssuedToken()
    {
        var store = BuildTokenStore();
        var (svc, _, _) = BuildService(tokenStore: store);
        var authResult = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);

        svc.Approve(svc.GetPendingRequests()[0].RequestId);
        var completion = await authResult.PairingCompletion!;

        var saved = store.Get(ConnectorChannelId.Create("terminal", "default"));
        Assert.NotNull(saved);
        Assert.Equal(completion.IssuedToken, saved.Token);
        Assert.Empty(svc.GetPendingRequests());
    }

    // ── Deny: completion resolves with Denied ─────────────────────────

    [Fact]
    public async Task Deny_PendingRequest_ResolvesCompletionWithDenied()
    {
        var (svc, _, _) = BuildService();
        var authResult = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);
        var pending = svc.GetPendingRequests();

        var denied = svc.Deny(pending[0].RequestId, "user_refused");
        var completion = await authResult.PairingCompletion!;

        Assert.True(denied);
        Assert.Equal(ConnectorAuthOutcome.Denied, completion.Outcome);
        Assert.Equal("user_refused", completion.Reason);
    }

    // ── Approve/Deny unknown id returns false ────────────────────────

    [Fact]
    public void Approve_UnknownId_ReturnsFalse()
    {
        var (svc, _, _) = BuildService();

        Assert.False(svc.Approve("non-existent-id"));
    }

    [Fact]
    public void Deny_UnknownId_ReturnsFalse()
    {
        var (svc, _, _) = BuildService();

        Assert.False(svc.Deny("non-existent-id", "nope"));
    }

    [Fact]
    public async Task Approve_AlreadyApprovedRequest_ReturnsFalse()
    {
        var (svc, _, _) = BuildService();
        await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);
        var requestId = svc.GetPendingRequests()[0].RequestId;

        Assert.True(svc.Approve(requestId));
        Assert.False(svc.Approve(requestId));
    }

    // ── Expiry via FakeTimeProvider ───────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_ExpiredByTimer_CompletesWithPairingExpired()
    {
        var (svc, _, clock) = BuildService();
        var authResult = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(6));

        var completion = await authResult.PairingCompletion!;
        Assert.Equal(ConnectorAuthOutcome.Denied, completion.Outcome);
        Assert.Equal("pairing_expired", completion.Reason);
    }

    // ── GetPendingRequests excludes expired ───────────────────────────

    [Fact]
    public async Task GetPendingRequests_AfterExpiry_ExcludesExpiredEntries()
    {
        var (svc, _, clock) = BuildService();
        await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Empty(svc.GetPendingRequests());
    }

    // ── Rate limiting: 5 per 10 min ───────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_ExceedsRateLimit_ReturnsDenied()
    {
        var (svc, _, _) = BuildService();

        for (var i = 0; i < 5; i++)
        {
            await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);
            var pending = svc.GetPendingRequests();
            if (pending.Count > 0)
            {
                svc.Deny(pending[0].RequestId, "denied");
            }
        }

        var result = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal(ConnectorAuthOutcome.Denied, result.Outcome);
        Assert.Equal("pairing_rate_limited", result.Reason);
    }

    // ── Rate limit resets after window ────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_AfterRateLimitWindow_AllowsNewRequests()
    {
        var (svc, _, clock) = BuildService();

        for (var i = 0; i < 5; i++)
        {
            await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);
            var pending = svc.GetPendingRequests();
            if (pending.Count > 0)
            {
                svc.Deny(pending[0].RequestId, "denied");
            }
        }

        clock.Advance(TimeSpan.FromMinutes(11));

        var result = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal(ConnectorAuthOutcome.PairingRequired, result.Outcome);
    }

    // ── Revoke drops the channel ──────────────────────────────────────

    [Fact]
    public async Task RevokeAsync_ExistingRecord_ReturnsTrueAndDetaches()
    {
        var channelId = ConnectorChannelId.Create("terminal", "default");
        var store = BuildTokenStore();
        store.Save(MakeRecord(channelId, "tok"));

        var (svc, registry, _) = BuildService(tokenStore: store);

        var result = await svc.RevokeAsync(channelId);

        Assert.True(result);
        Assert.Null(store.Get(channelId));
        await registry.Received(1).DetachByChannelIdAsync(channelId);
    }

    [Fact]
    public async Task RevokeAsync_UnknownChannel_ReturnsFalse()
    {
        var (svc, registry, _) = BuildService();

        var result = await svc.RevokeAsync("plugin:terminal:missing");

        Assert.False(result);
        await registry.Received(1).DetachByChannelIdAsync("plugin:terminal:missing");
    }

    // ── SetEnabled(false) drops channel ──────────────────────────────

    [Fact]
    public async Task SetEnabledAsync_DisablingConnector_DetachesChannel()
    {
        var channelId = ConnectorChannelId.Create("terminal", "default");
        var store = BuildTokenStore();
        store.Save(MakeRecord(channelId, "tok"));

        var (svc, registry, _) = BuildService(tokenStore: store);

        var result = await svc.SetEnabledAsync(channelId, false);

        Assert.True(result);
        Assert.False(store.Get(channelId)!.Enabled);
        await registry.Received(1).DetachByChannelIdAsync(channelId);
    }

    [Fact]
    public async Task SetEnabledAsync_EnablingConnector_DoesNotDetach()
    {
        var channelId = ConnectorChannelId.Create("terminal", "default");
        var store = BuildTokenStore();
        store.Save(MakeRecord(channelId, "tok") with { Enabled = false });

        var (svc, registry, _) = BuildService(tokenStore: store);

        var result = await svc.SetEnabledAsync(channelId, true);

        Assert.True(result);
        await registry.DidNotReceive().DetachByChannelIdAsync(Arg.Any<string>());
    }

    // ── GetPairedConnectors surfaces the store ───────────────────────

    [Fact]
    public void GetPairedConnectors_WithSavedRecords_ReturnsThem()
    {
        var store = BuildTokenStore();
        store.Save(MakeRecord(ConnectorChannelId.Create("terminal", "default"), "tok"));

        var (svc, _, _) = BuildService(tokenStore: store);

        Assert.Single(svc.GetPairedConnectors());
    }

    // ── Dispose completes pending with shutting_down ──────────────────

    [Fact]
    public async Task Dispose_WithPendingRequests_CompletesWithShuttingDown()
    {
        var (svc, _, _) = BuildService();
        var authResult = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);
        var completionTask = authResult.PairingCompletion!;

        svc.Dispose();

        var result = await completionTask;
        Assert.Equal(ConnectorAuthOutcome.Denied, result.Outcome);
        Assert.Equal("shutting_down", result.Reason);
    }

    [Fact]
    public async Task Approve_AfterDeadlineButBeforeTimerFires_ReturnsFalse()
    {
        var (svc, _, clock) = BuildService();

        var authResult = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);
        Assert.Equal(ConnectorAuthOutcome.PairingRequired, authResult.Outcome);

        var pending = svc.GetPendingRequests();
        var requestId = Assert.Single(pending).RequestId;

        // Land exactly on the deadline: GetPendingRequests already treats it as expired,
        // but the entry may still be in the map if the timer callback has not run.
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.False(svc.Approve(requestId));
        Assert.Empty(svc.GetPendingRequests());

        var result = await authResult.PairingCompletion!;
        Assert.Equal(ConnectorAuthOutcome.Denied, result.Outcome);
        Assert.Equal("pairing_expired", result.Reason);
    }

    [Fact]
    public async Task GetPairedConnectors_AfterApproval_DoesNotExposeTheDurableToken()
    {
        var (svc, _, _) = BuildService();

        var authResult = await svc.AuthenticateAsync(MakeRequest(), CancellationToken.None);
        var requestId = Assert.Single(svc.GetPendingRequests()).RequestId;
        Assert.True(svc.Approve(requestId));

        var issued = (await authResult.PairingCompletion!).IssuedToken;
        Assert.False(string.IsNullOrWhiteSpace(issued));

        var summary = Assert.Single(svc.GetPairedConnectors());
        Assert.Equal("plugin:terminal:default", summary.ChannelId);

        // The summary type must not carry the token in any form, including via serialisation.
        var json = System.Text.Json.JsonSerializer.Serialize(summary);
        Assert.DoesNotContain(issued, json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticateAsync_TooManyPendingRequests_IsRefused()
    {
        // A hostile process can pick unlimited distinct keys, so the per-channel rate limit
        // alone does not bound the approval list. A flooded list buries the legitimate request
        // the user is looking for, which is a social-engineering aid.
        var (svc, _, _) = BuildService();

        for (var i = 0; i < 16; i++)
        {
            var accepted = await svc.AuthenticateAsync(MakeRequest(key: $"connector-{i}"), CancellationToken.None);
            Assert.Equal(ConnectorAuthOutcome.PairingRequired, accepted.Outcome);
        }

        var refused = await svc.AuthenticateAsync(MakeRequest(key: "one-too-many"), CancellationToken.None);

        Assert.Equal(ConnectorAuthOutcome.Denied, refused.Outcome);
        Assert.Equal("pairing_rate_limited", refused.Reason);
        Assert.Equal(16, svc.GetPendingRequests().Count);
    }

    [Fact]
    public async Task AuthenticateAsync_PendingRequestsExpired_SlotsAreReleased()
    {
        // Expired entries must not keep consuming slots, or a burst of abandoned attempts
        // would lock the user out of pairing anything else.
        var (svc, _, clock) = BuildService();

        for (var i = 0; i < 16; i++)
        {
            await svc.AuthenticateAsync(MakeRequest(key: $"connector-{i}"), CancellationToken.None);
        }

        clock.Advance(TimeSpan.FromMinutes(6));

        var result = await svc.AuthenticateAsync(MakeRequest(key: "later-arrival"), CancellationToken.None);

        Assert.Equal(ConnectorAuthOutcome.PairingRequired, result.Outcome);
    }

    private static ConnectorTokenStore BuildTokenStore() =>
        new(new FakeConnectorSecretStore(), NullLogger<ConnectorTokenStore>.Instance);

    private static (ConnectorPairingService Svc, IConnectorRegistry Registry, FakeTimeProvider Clock)
        BuildService(BridgeConfig? config = null, ConnectorTokenStore? tokenStore = null)
    {
        config ??= RequireApprovalConfig();
        tokenStore ??= BuildTokenStore();

        var registry = Substitute.For<IConnectorRegistry>();
        registry.DetachByChannelIdAsync(Arg.Any<string>())
            .Returns(_ => ValueTask.FromResult(false));

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var svc = new ConnectorPairingService(
            tokenStore,
            config,
            registry,
            clock,
            NullLogger<ConnectorPairingService>.Instance);

        return (svc, registry, clock);
    }

    private static ConnectorRecord MakeRecord(string channelId, string token) => new()
    {
        ChannelId = channelId,
        Key = "terminal",
        InstanceId = "default",
        DisplayName = "Terminal",
        Token = token,
        PairedAt = DateTimeOffset.UtcNow,
    };

    private static ConnectorAuthRequest MakeRequest(
        string key = "terminal",
        string instanceId = "default",
        string? token = null) => new()
    {
        Key = key,
        InstanceId = instanceId,
        DisplayName = "Terminal",
        Token = token,
        RemoteEndpoint = "127.0.0.1:9000",
    };
}
