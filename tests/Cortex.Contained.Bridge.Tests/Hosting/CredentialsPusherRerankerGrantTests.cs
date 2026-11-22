using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.RemoteServices;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Hosting;

/// <summary>The reranker switch and sidecar key (DPAPI) pushed to every tenant's agent.</summary>
public sealed class CredentialsPusherRerankerGrantTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"cortex-test-{Guid.NewGuid():N}");
    private readonly SecretManager secretManager;

    public CredentialsPusherRerankerGrantTests()
    {
        Directory.CreateDirectory(this.tempDir);
        this.secretManager = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* cleanup */ }
    }

    private CredentialsPusher Pusher(bool enabled) => new(
        tenantRouter: null!,
        config: new BridgeConfig { Reranker = new RerankerSettingsConfig { Enabled = enabled } },
        httpClientFactory: null!,
        secretManager: this.secretManager,
        modelCatalog: null!,
        resolver: new RemoteServiceResolver(),
        tokenRefreshService: null!,
        logger: NullLogger<CredentialsPusher>.Instance);

    [Fact]
    public void GetOrCreateRerankerKey_IsStable256BitHex_AndPersisted()
    {
        var key = this.secretManager.GetOrCreateRerankerKey();
        var again = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir).GetOrCreateRerankerKey();

        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.Equal(key, again);
    }

    [Fact]
    public void BuildRerankerGrant_Disabled_CarriesNoKey()
    {
        var grant = this.Pusher(enabled: false).BuildRerankerGrant();

        Assert.False(grant.Enabled);
        Assert.Null(grant.ApiKey);
    }

    [Fact]
    public async Task PushRerankerGrantToTargets_EnabledGrantReachesEveryTenant_FailuresIsolated()
    {
        var pusher = this.Pusher(enabled: true);
        var grant = pusher.BuildRerankerGrant();
        var ok = new FakeTarget("default");
        var failing = new FakeTarget("tenant-a") { Throw = true };
        var ok2 = new FakeTarget("tenant-b");

        await pusher.PushRerankerGrantToTargetsAsync(grant, [ok, failing, ok2], CancellationToken.None);

        Assert.Equal(this.secretManager.GetOrCreateRerankerKey(), ok.Received!.ApiKey);
        Assert.True(ok2.Received!.Enabled);
        Assert.NotNull(failing.Received);
    }

    private sealed class FakeTarget(string tenantId) : IRerankerGrantPushTarget
    {
        public string TenantId { get; } = tenantId;

        public bool Throw { get; init; }

        public RerankerGrant? Received { get; private set; }

        public Task ProvideRerankerGrantAsync(RerankerGrant grant, CancellationToken cancellationToken)
        {
            this.Received = grant;
            return this.Throw ? throw new InvalidOperationException("tenant down") : Task.CompletedTask;
        }
    }
}
