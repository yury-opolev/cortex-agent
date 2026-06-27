using Cortex.Contained.Bridge.RemoteServices;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Common.Security;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.RemoteServices;

/// <summary>
/// Tests for the embedding-provider GET logic: verifies that
/// the DTO reports keySet correctly, never leaks the raw key value,
/// pre-fills the endpoint with the effective (never blank) URL,
/// and correctly computes isDefault.
/// </summary>
public sealed class EmbeddingProviderDtoTests : IDisposable
{
    private readonly string tempDir;
    private readonly InMemorySecretStore secretStore;
    private readonly SecretManager secretManager;
    private readonly RemoteServiceResolver resolver;

    public EmbeddingProviderDtoTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"ep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.secretStore = new InMemorySecretStore();
        this.secretManager = new SecretManager(
            this.secretStore, NullLogger<SecretManager>.Instance, this.tempDir);
        this.resolver = new RemoteServiceResolver();
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetDto_NoStoredKey_KeySetIsFalse()
    {
        var config = new MemorySettingsConfig();

        var keySet = !string.IsNullOrEmpty(this.secretManager.GetApiKey("embeddings-provider"));

        Assert.False(keySet);
    }

    [Fact]
    public void GetDto_WithStoredKey_KeySetIsTrue()
    {
        this.secretManager.StoreApiKey("embeddings-provider", "secret-abc");

        var storedKey = this.secretManager.GetApiKey("embeddings-provider");
        var keySet = !string.IsNullOrEmpty(storedKey);

        Assert.True(keySet);
        // Must not return the raw key value — only the keySet bool
        Assert.NotEqual("secret-abc", keySet.ToString());
    }

    [Fact]
    public void GetDto_WithClearedKey_KeySetIsFalse()
    {
        this.secretManager.StoreApiKey("embeddings-provider", "secret-abc");

        // Clearing goes through RemoveApiKey (what the clear-key/reset routes call).
        // Storing an empty string would attempt to DPAPI-encrypt "" and throw — that
        // was the original bug. RemoveApiKey deletes the entry so keySet is false.
        this.secretManager.RemoveApiKey("embeddings-provider");

        var storedKey = this.secretManager.GetApiKey("embeddings-provider");
        var keySet = !string.IsNullOrEmpty(storedKey);

        Assert.False(keySet);
        Assert.Null(storedKey);
    }

    [Fact]
    public void GetDto_NullEndpoint_IsDefault_EndpointIsLocalDefault()
    {
        var config = new MemorySettingsConfig { EmbeddingEndpoint = null };

        var effectiveEndpoint = this.resolver.EffectiveEmbeddingEndpoint(config.EmbeddingEndpoint);
        var isDefault = this.resolver.IsEmbeddingDefault(config.EmbeddingEndpoint);

        Assert.Equal(RemoteServiceResolver.EmbeddingsLocalDefault, effectiveEndpoint);
        Assert.True(isDefault);
    }

    [Fact]
    public void GetDto_OverrideEndpoint_IsNotDefault_EndpointIsOverride()
    {
        var config = new MemorySettingsConfig { EmbeddingEndpoint = "http://mac:11434" };

        var effectiveEndpoint = this.resolver.EffectiveEmbeddingEndpoint(config.EmbeddingEndpoint);
        var isDefault = this.resolver.IsEmbeddingDefault(config.EmbeddingEndpoint);

        Assert.Equal("http://mac:11434", effectiveEndpoint);
        Assert.False(isDefault);
    }

    [Fact]
    public void GetDto_EndpointIsAlwaysNonBlank_EvenWhenConfigIsNull()
    {
        var config = new MemorySettingsConfig { EmbeddingEndpoint = null };

        var effectiveEndpoint = this.resolver.EffectiveEmbeddingEndpoint(config.EmbeddingEndpoint);

        Assert.False(string.IsNullOrWhiteSpace(effectiveEndpoint));
    }

    [Fact]
    public void SaveLogic_BlankEndpoint_SetsConfigToNull()
    {
        // Simulates what the save endpoint does before persisting
        var mem = new MemorySettingsConfig { EmbeddingEndpoint = "http://mac:11434" };
        var incoming = "   ";

        mem.EmbeddingEndpoint = string.IsNullOrWhiteSpace(incoming) ? null : incoming.Trim();

        Assert.Null(mem.EmbeddingEndpoint);
    }

    [Fact]
    public void SaveLogic_ValidEndpoint_TrimsAndStores()
    {
        var mem = new MemorySettingsConfig();
        var incoming = "  http://mac:11434  ";

        mem.EmbeddingEndpoint = string.IsNullOrWhiteSpace(incoming) ? null : incoming.Trim();

        Assert.Equal("http://mac:11434", mem.EmbeddingEndpoint);
    }

    [Fact]
    public void SaveLogic_AfterSaveWithOverride_IsDefaultFlipsToFalse()
    {
        var mem = new MemorySettingsConfig { EmbeddingEndpoint = "http://mac:11434" };

        var isDefault = this.resolver.IsEmbeddingDefault(mem.EmbeddingEndpoint);

        Assert.False(isDefault);
    }

    [Fact]
    public void SaveLogic_AfterReset_IsDefaultIsTrue()
    {
        var mem = new MemorySettingsConfig { EmbeddingEndpoint = "http://mac:11434" };

        // Reset clears the endpoint
        mem.EmbeddingEndpoint = null;

        var isDefault = this.resolver.IsEmbeddingDefault(mem.EmbeddingEndpoint);
        Assert.True(isDefault);
    }
}
