using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>Storage page and key-push calls into the agent (<see cref="IStorageHub"/>).</summary>
public sealed partial class HubClient
{
    /// <summary>Encryption mode, key state and the state of every stored item.</summary>
    public async Task<StorageEncryptionStatusDto> GetStorageEncryptionStatusAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<StorageEncryptionStatusDto>(nameof(IAgentHub.GetStorageEncryptionStatus), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What enabling encryption would do; changes nothing.</summary>
    public async Task<StorageEncryptionPreviewDto> PreviewStorageEncryptionAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<StorageEncryptionPreviewDto>(nameof(IAgentHub.PreviewStorageEncryption), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns encryption at rest on (one-way); the agent restarts to apply it.</summary>
    public async Task<StorageEncryptionEnableResultDto> EnableStorageEncryptionAsync(StorageEncryptionEnableRequestDto request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<StorageEncryptionEnableResultDto>(nameof(IAgentHub.EnableStorageEncryption), request, cancellationToken).ConfigureAwait(false);
    }
}
