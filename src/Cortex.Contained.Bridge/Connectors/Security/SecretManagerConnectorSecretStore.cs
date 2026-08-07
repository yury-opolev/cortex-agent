using Cortex.Contained.Bridge.Security;

namespace Cortex.Contained.Bridge.Connectors.Security;

/// <summary>
/// Adapts the Bridge's DPAPI-backed <see cref="SecretManager"/> to <see cref="IConnectorSecretStore"/>.
/// Connector token blobs are stored under their id via the generic api-key store,
/// so encryption-at-rest is reused as-is and tokens never touch <c>cortex.yml</c>.
/// </summary>
public sealed class SecretManagerConnectorSecretStore : IConnectorSecretStore
{
    private readonly SecretManager secretManager;

    /// <summary>Initialises a new <see cref="SecretManagerConnectorSecretStore"/>.</summary>
    public SecretManagerConnectorSecretStore(SecretManager secretManager)
    {
        this.secretManager = secretManager;
    }

    /// <inheritdoc/>
    public string? GetSecret(string secretId) => this.secretManager.GetApiKey(secretId);

    /// <inheritdoc/>
    public void SetSecret(string secretId, string value) => this.secretManager.StoreApiKey(secretId, value);

    /// <inheritdoc/>
    public void RemoveSecret(string secretId) => this.secretManager.RemoveApiKey(secretId);
}
