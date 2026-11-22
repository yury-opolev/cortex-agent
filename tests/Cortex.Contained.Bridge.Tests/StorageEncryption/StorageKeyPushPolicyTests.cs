using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;
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

    [Fact]
    public async Task NoKey_AgentStatusUnavailable_NothingMintedAndTheNextPushAsksAgain()
    {
        var status = new StorageEncryptionStatusDto { Unavailable = true, Error = "x" };

        var (pushed, decision) = await StorageKeyPushPolicy.ResolveAsync(this.secrets, "default", () => Task.FromResult<StorageEncryptionStatusDto?>(status));

        Assert.Null(pushed);
        Assert.Equal(StorageKeyPushDecision.WithholdUnknownStatus, decision);
        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
    }

    [Theory]
    [InlineData("Method does not exist")]
    [InlineData("Unknown hub method 'GetStorageEncryptionStatus'")]
    [InlineData("An unexpected error occurred invoking 'GetStorageEncryptionStatus' on the server. HubException: Method does not exist.")]
    public async Task ReadAgentStatus_MissingHubMethod_IsAnAgentWithoutTheStorageHub(string message)
    {
        var status = await StorageKeyPushPolicy.ReadAgentStatusAsync(() => throw new HubException(message));

        Assert.Null(status);
    }

    [Theory]
    [InlineData("Storage encryption is not available on this agent.")]
    [InlineData("Failed to invoke 'GetStorageEncryptionStatus' due to an error on the server.")]
    public async Task ReadAgentStatus_AnyOtherHubFailure_IsUnavailableNotAnOldAgent(string message)
    {
        var status = await StorageKeyPushPolicy.ReadAgentStatusAsync(() => throw new HubException(message));

        Assert.True(status!.Unavailable);
    }

    [Fact]
    public async Task ReadAgentStatus_TransportFailure_IsUnavailable()
    {
        var status = await StorageKeyPushPolicy.ReadAgentStatusAsync(() => throw new IOException("connection reset"));

        Assert.True(status!.Unavailable);
    }

    [Fact]
    public async Task ReadAgentStatus_Cancellation_Propagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => StorageKeyPushPolicy.ReadAgentStatusAsync(() => throw new OperationCanceledException()));
    }
}
