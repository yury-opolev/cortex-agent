using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary><see cref="IStorageHub"/>: the Bridge Storage page (status, pre-flight preview, enabling encryption at rest).</summary>
public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task<StorageEncryptionStatusDto> GetStorageEncryptionStatus() => Task.FromResult(this.StorageEncryption.GetStatus());

    /// <inheritdoc />
    public Task<StorageEncryptionPreviewDto> PreviewStorageEncryption() => Task.FromResult(this.StorageEncryption.Preview());

    /// <inheritdoc />
    public Task<StorageEncryptionEnableResultDto> EnableStorageEncryption(StorageEncryptionEnableRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(this.StorageEncryption.Enable(request));
    }

    private Storage.StorageEncryptionService StorageEncryption =>
        this.storageEncryption ?? throw new HubException("Storage encryption is not available on this agent.");
}
