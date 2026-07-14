using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.RemoteServices;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Hosting;

/// <summary>
/// Tests <see cref="CredentialsPusher.BuildAgentConfigUpdate"/> and the multi-tenant fan-out in
/// <see cref="CredentialsPusher.PushAgentConfigToTargetsAsync"/> — the seams behind
/// <see cref="CredentialsPusher.PushAgentConfigAsync"/>, which pushes the Bridge-authoritative
/// <c>MaxConcurrentSubagents</c> value to every connected tenant after initial connection,
/// watchdog reconstruction, and reconnect. The router and hub clients are sealed/concrete and
/// cannot be mocked, so (mirroring <see cref="CredentialsPusherMemorySettingsPushTests"/>) the
/// fan-out is exercised via <see cref="IAgentConfigPushTarget"/> instead.
/// </summary>
public sealed class CredentialsPusherAgentConfigPushTests : IDisposable
{
    private readonly string tempDir;
    private readonly SecretManager secretManager;
    private readonly RemoteServiceResolver resolver;

    public CredentialsPusherAgentConfigPushTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"cortex-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.secretManager = new SecretManager(
            new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir);
        this.resolver = new RemoteServiceResolver();
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* cleanup */ }
        GC.SuppressFinalize(this);
    }

    private CredentialsPusher BuildPusher(BridgeConfig config)
    {
        return new CredentialsPusher(
            tenantRouter: null!,
            config: config,
            httpClientFactory: null!,
            secretManager: this.secretManager,
            modelCatalog: null!,
            resolver: this.resolver,
            tokenRefreshService: null!,
            logger: NullLogger<CredentialsPusher>.Instance);
    }

    [Fact]
    public void BuildAgentConfigUpdate_UsesPersistedConcurrency()
    {
        var pusher = this.BuildPusher(new BridgeConfig { MaxConcurrentSubagents = 37 });

        var update = pusher.BuildAgentConfigUpdate();

        Assert.Equal(37, update.MaxConcurrentSubagents);
    }

    [Fact]
    public async Task PushAgentConfigAsync_PushesAfterReconnect()
    {
        // Simulates the reconnect handler in TenantConnectionBootstrapper: after a tenant's
        // HubClient reconnects, CredentialsPusher.PushAgentConfigAsync builds the update from the
        // Bridge-persisted value and pushes it to every connected (i.e. just-reconnected) tenant.
        var pusher = this.BuildPusher(new BridgeConfig { MaxConcurrentSubagents = 42 });
        var update = pusher.BuildAgentConfigUpdate();

        var reconnectedTenant = new FakeAgentConfigPushTarget("default");

        await pusher.PushAgentConfigToTargetsAsync(update, [reconnectedTenant], CancellationToken.None);

        Assert.Equal(1, reconnectedTenant.PushCount);
        Assert.Equal(42, reconnectedTenant.LastUpdate?.MaxConcurrentSubagents);
    }

    [Fact]
    public async Task PushAgentConfigToTargets_PushesToEveryTenant()
    {
        var pusher = this.BuildPusher(new BridgeConfig { MaxConcurrentSubagents = 10 });
        var update = pusher.BuildAgentConfigUpdate();

        var t1 = new FakeAgentConfigPushTarget("default");
        var t2 = new FakeAgentConfigPushTarget("tenant-a");

        await pusher.PushAgentConfigToTargetsAsync(update, [t1, t2], CancellationToken.None);

        Assert.Equal(1, t1.PushCount);
        Assert.Equal(1, t2.PushCount);
    }

    [Fact]
    public async Task PushAgentConfigToTargets_OneTenantThrows_OthersStillReceive()
    {
        var pusher = this.BuildPusher(new BridgeConfig { MaxConcurrentSubagents = 10 });
        var update = pusher.BuildAgentConfigUpdate();

        var ok1 = new FakeAgentConfigPushTarget("default");
        var failing = new FakeAgentConfigPushTarget("tenant-a") { Throw = true };
        var ok2 = new FakeAgentConfigPushTarget("tenant-b");

        // Must not throw — per-tenant failures are isolated.
        await pusher.PushAgentConfigToTargetsAsync(update, [ok1, failing, ok2], CancellationToken.None);

        Assert.Equal(1, ok1.PushCount);
        Assert.Equal(1, failing.PushCount); // attempted, then threw
        Assert.Equal(1, ok2.PushCount);
    }

    private sealed class FakeAgentConfigPushTarget : IAgentConfigPushTarget
    {
        public FakeAgentConfigPushTarget(string tenantId) => this.TenantId = tenantId;

        public string TenantId { get; }
        public bool Throw { get; init; }
        public int PushCount { get; private set; }
        public AgentConfigUpdate? LastUpdate { get; private set; }

        public Task UpdateConfigAsync(AgentConfigUpdate config, CancellationToken cancellationToken)
        {
            this.PushCount++;
            this.LastUpdate = config;
            if (this.Throw)
            {
                throw new InvalidOperationException($"Tenant '{this.TenantId}' push failed");
            }
            return Task.CompletedTask;
        }
    }
}
