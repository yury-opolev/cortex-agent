using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.StorageEncryption;

/// <summary>The Bridge never mints a storage key that would orphan encrypted data.</summary>
public sealed class StorageKeyPushPolicyTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cortex-keypush-" + Guid.NewGuid().ToString("N"));
    private readonly SecretManager secrets;

    public StorageKeyPushPolicyTests()
    {
        this.secrets = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task ExistingKey_PushedWithoutAskingTheAgent()
    {
        var key = this.secrets.GetOrCreateTenantStorageKey("default");
        var asked = false;

        var (pushed, decision) = await StorageKeyPushPolicy.ResolveAsync(this.secrets, "default", () =>
        {
            asked = true;
            return Task.FromResult<StorageEncryptionStatusDto?>(null);
        });

        Assert.Equal((key, StorageKeyPushDecision.Push), (pushed, decision));
        Assert.False(asked);
    }

    [Fact]
    public async Task NoKey_AgentHoldsEncryptedData_NothingMinted()
    {
        var status = new StorageEncryptionStatusDto { EncryptAtRest = true, HasEncryptedData = true };

        var (pushed, decision) = await StorageKeyPushPolicy.ResolveAsync(this.secrets, "default", () => Task.FromResult<StorageEncryptionStatusDto?>(status));

        Assert.Null(pushed);
        Assert.Equal(StorageKeyPushDecision.WithholdMissingKey, decision);
        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task NoKey_EncryptionOff_NothingMinted()
    {
        var (pushed, decision) = await StorageKeyPushPolicy.ResolveAsync(
            this.secrets, "default", () => Task.FromResult<StorageEncryptionStatusDto?>(new StorageEncryptionStatusDto()));

        Assert.Null(pushed);
        Assert.Equal(StorageKeyPushDecision.Withhold, decision);
        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoKey_FreshEncryptedSetupOrAgentWithoutStorageHub_Minted(bool olderAgent)
    {
        StorageEncryptionStatusDto? status = olderAgent ? null : new StorageEncryptionStatusDto { EncryptAtRest = true };

        var (pushed, decision) = await StorageKeyPushPolicy.ResolveAsync(this.secrets, "default", () => Task.FromResult(status));

        Assert.Equal(StorageKeyPushDecision.MintAndPush, decision);
        Assert.Equal(pushed, this.secrets.TryGetTenantStorageKey("default"));
    }
}
