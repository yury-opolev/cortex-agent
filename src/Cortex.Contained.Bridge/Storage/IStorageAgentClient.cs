using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Storage;

/// <summary>The agent calls the Storage endpoints make (a seam over <see cref="HubClient"/> for tests).</summary>
public interface IStorageAgentClient
{
    Task<StorageEncryptionStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    Task<StorageEncryptionPreviewDto> PreviewAsync(CancellationToken cancellationToken);

    Task<StorageEncryptionEnableResultDto> EnableAsync(StorageEncryptionEnableRequestDto request, CancellationToken cancellationToken);

    /// <summary>Pushes the storage key; false when the agent refuses it (another key in use or not the data's key).</summary>
    Task<bool> ProvideKeyAsync(string base64Key, CancellationToken cancellationToken);
}

/// <summary>Adapts a connected <see cref="HubClient"/> to <see cref="IStorageAgentClient"/>.</summary>
public sealed class HubStorageAgentClient(HubClient client) : IStorageAgentClient
{
    /// <summary>The adapter for <paramref name="client"/>, or null when it is missing or disconnected.</summary>
    public static IStorageAgentClient? For(HubClient? client) => client is { IsConnected: true } ? new HubStorageAgentClient(client) : null;

    public Task<StorageEncryptionStatusDto> GetStatusAsync(CancellationToken cancellationToken) => client.GetStorageEncryptionStatusAsync(cancellationToken);

    public Task<StorageEncryptionPreviewDto> PreviewAsync(CancellationToken cancellationToken) => client.PreviewStorageEncryptionAsync(cancellationToken);

    public Task<StorageEncryptionEnableResultDto> EnableAsync(StorageEncryptionEnableRequestDto request, CancellationToken cancellationToken) =>
        client.EnableStorageEncryptionAsync(request, cancellationToken);

    public Task<bool> ProvideKeyAsync(string base64Key, CancellationToken cancellationToken) =>
        client.ProvideStorageKeyAsync(new StorageKeyGrant { Key = base64Key }, cancellationToken);
}
