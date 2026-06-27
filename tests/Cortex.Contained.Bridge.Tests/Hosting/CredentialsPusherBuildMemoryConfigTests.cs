using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.RemoteServices;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Common.Security;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="CredentialsPusher.BuildMemoryConfig"/>.
/// Only the config, resolver, and secretManager fields are exercised; the
/// SignalR/router/catalog dependencies are passed as null because
/// <see cref="CredentialsPusher.BuildMemoryConfig"/> never touches them.
/// </summary>
public sealed class CredentialsPusherBuildMemoryConfigTests : IDisposable
{
    private readonly string tempDir;
    private readonly InMemorySecretStore secretStore;
    private readonly SecretManager secretManager;
    private readonly RemoteServiceResolver resolver;

    public CredentialsPusherBuildMemoryConfigTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"cortex-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.secretStore = new InMemorySecretStore();
        this.secretManager = new SecretManager(this.secretStore, NullLogger<SecretManager>.Instance, this.tempDir);
        this.resolver = new RemoteServiceResolver();
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* cleanup */ }
        GC.SuppressFinalize(this);
    }

    private CredentialsPusher BuildPusher(MemorySettingsConfig memorySettings, string? embeddingKey = null)
    {
        if (embeddingKey is not null)
        {
            this.secretManager.StoreApiKey("embeddings-provider", embeddingKey);
        }

        var config = new BridgeConfig { Memory = memorySettings };

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
    public void BuildMemoryConfig_WithOverrideEndpoint_UsesOverride()
    {
        var pusher = this.BuildPusher(new MemorySettingsConfig
        {
            EmbeddingEndpoint = "http://mac:11434",
        });

        var result = pusher.BuildMemoryConfig();

        Assert.Equal("http://mac:11434", result.OllamaEndpoint);
    }

    [Fact]
    public void BuildMemoryConfig_WithNullEndpoint_UsesLocalDefault()
    {
        var pusher = this.BuildPusher(new MemorySettingsConfig
        {
            EmbeddingEndpoint = null,
        });

        var result = pusher.BuildMemoryConfig();

        Assert.Equal(RemoteServiceResolver.EmbeddingsLocalDefault, result.OllamaEndpoint);
    }

    [Fact]
    public void BuildMemoryConfig_WithBlankEndpoint_UsesLocalDefault()
    {
        var pusher = this.BuildPusher(new MemorySettingsConfig
        {
            EmbeddingEndpoint = "   ",
        });

        var result = pusher.BuildMemoryConfig();

        Assert.Equal(RemoteServiceResolver.EmbeddingsLocalDefault, result.OllamaEndpoint);
    }

    [Fact]
    public void BuildMemoryConfig_WithStoredKey_FlowsKeyThrough()
    {
        var pusher = this.BuildPusher(
            new MemorySettingsConfig { EmbeddingEndpoint = "http://mac:11434" },
            embeddingKey: "secret-key-123");

        var result = pusher.BuildMemoryConfig();

        Assert.Equal("http://mac:11434", result.OllamaEndpoint);
        Assert.Equal("secret-key-123", result.OllamaApiKey);
    }

    [Fact]
    public void BuildMemoryConfig_WithNoStoredKey_ReturnsNullKey()
    {
        var pusher = this.BuildPusher(new MemorySettingsConfig());

        var result = pusher.BuildMemoryConfig();

        Assert.Null(result.OllamaApiKey);
    }

    [Fact]
    public void BuildMemoryConfig_PropagatesEnabledFalse()
    {
        var pusher = this.BuildPusher(new MemorySettingsConfig { Enabled = false });

        var result = pusher.BuildMemoryConfig();

        Assert.False(result.Enabled);
    }

    [Fact]
    public void BuildMemoryConfig_DefaultEnabledTrue()
    {
        var pusher = this.BuildPusher(new MemorySettingsConfig());

        var result = pusher.BuildMemoryConfig();

        Assert.True(result.Enabled);
    }

    [Fact]
    public void BuildMemoryConfig_PassesThroughThresholds()
    {
        var pusher = this.BuildPusher(new MemorySettingsConfig
        {
            DuplicateThreshold = 0.85f,
            CompactionSimilarityThreshold = 0.65f,
            CompactionEnabled = false,
            IdleCompactionEnabled = false,
            IdleResetMinutes = 120,
            CompactionPreserveRecentTurns = 6,
        });

        var result = pusher.BuildMemoryConfig();

        Assert.Equal(0.85f, result.DuplicateThreshold);
        Assert.Equal(0.65f, result.CompactionSimilarityThreshold);
        Assert.False(result.CompactionEnabled);
        Assert.False(result.IdleCompactionEnabled);
        Assert.Equal(120, result.IdleResetMinutes);
        Assert.Equal(6, result.CompactionPreserveRecentTurns);
    }
}
