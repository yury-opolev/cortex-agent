using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.RemoteServices;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Hosting;

/// <summary>
/// Tests the multi-tenant memory-settings push loop in
/// <see cref="CredentialsPusher.PushMemoryConfigToTargetsAsync"/>. The router and
/// hub clients are sealed/concrete and cannot be mocked, so the iteration is extracted
/// behind <see cref="IMemoryConfigPushTarget"/> — the testable seam exercised here.
/// </summary>
public sealed class CredentialsPusherMemorySettingsPushTests : IDisposable
{
    private readonly string tempDir;
    private readonly SecretManager secretManager;
    private readonly RemoteServiceResolver resolver;

    public CredentialsPusherMemorySettingsPushTests()
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

    private CredentialsPusher BuildPusher()
    {
        return new CredentialsPusher(
            tenantRouter: null!,
            config: new BridgeConfig { Memory = new MemorySettingsConfig() },
            httpClientFactory: null!,
            secretManager: this.secretManager,
            modelCatalog: null!,
            resolver: this.resolver,
            tokenRefreshService: null!,
            logger: NullLogger<CredentialsPusher>.Instance);
    }

    [Fact]
    public async Task PushMemoryConfigToTargets_PushesToEveryTenant()
    {
        var pusher = this.BuildPusher();
        var config = pusher.BuildMemoryConfig();

        var t1 = new FakePushTarget("default");
        var t2 = new FakePushTarget("tenant-a");
        var t3 = new FakePushTarget("tenant-b");

        await pusher.PushMemoryConfigToTargetsAsync(config, [t1, t2, t3], CancellationToken.None);

        Assert.Equal(1, t1.PushCount);
        Assert.Equal(1, t2.PushCount);
        Assert.Equal(1, t3.PushCount);
        Assert.Same(config, t1.LastConfig);
        Assert.Same(config, t2.LastConfig);
        Assert.Same(config, t3.LastConfig);
    }

    [Fact]
    public async Task PushMemoryConfigToTargets_OneTenantThrows_OthersStillReceive()
    {
        var pusher = this.BuildPusher();
        var config = pusher.BuildMemoryConfig();

        var ok1 = new FakePushTarget("default");
        var failing = new FakePushTarget("tenant-a") { Throw = true };
        var ok2 = new FakePushTarget("tenant-b");

        // Must not throw — per-tenant failures are isolated.
        await pusher.PushMemoryConfigToTargetsAsync(config, [ok1, failing, ok2], CancellationToken.None);

        Assert.Equal(1, ok1.PushCount);
        Assert.Equal(1, failing.PushCount); // attempted, then threw
        Assert.Equal(1, ok2.PushCount);
    }

    [Fact]
    public async Task PushMemoryConfigToTargets_NoTenants_DoesNothing()
    {
        var pusher = this.BuildPusher();
        var config = pusher.BuildMemoryConfig();

        await pusher.PushMemoryConfigToTargetsAsync(config, [], CancellationToken.None);
        // No exception, nothing to assert beyond completion.
    }

    private sealed class FakePushTarget : IMemoryConfigPushTarget
    {
        public FakePushTarget(string tenantId) => this.TenantId = tenantId;

        public string TenantId { get; }
        public bool Throw { get; init; }
        public int PushCount { get; private set; }
        public MemoryConfig? LastConfig { get; private set; }

        public Task UpdateMemoryConfigAsync(MemoryConfig config, CancellationToken cancellationToken)
        {
            this.PushCount++;
            this.LastConfig = config;
            if (this.Throw)
            {
                throw new InvalidOperationException($"Tenant '{this.TenantId}' push failed");
            }
            return Task.CompletedTask;
        }
    }
}
