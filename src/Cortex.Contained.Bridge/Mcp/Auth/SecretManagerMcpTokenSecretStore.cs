using Cortex.Contained.Bridge.Security;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Adapts the Bridge's DPAPI-backed <see cref="SecretManager"/> to <see cref="IMcpTokenSecretStore"/>.
/// OAuth token blobs are stored under their <c>mcp/&lt;serverKey&gt;/oauth</c> id via the generic
/// api-key store, so encryption-at-rest is reused as-is and tokens never touch <c>cortex.yml</c>.
/// </summary>
public sealed class SecretManagerMcpTokenSecretStore : IMcpTokenSecretStore
{
    private readonly SecretManager secretManager;

    public SecretManagerMcpTokenSecretStore(SecretManager secretManager)
    {
        this.secretManager = secretManager;
    }

    public string? GetSecret(string secretId) => this.secretManager.GetApiKey(secretId);

    public void SetSecret(string secretId, string value) => this.secretManager.StoreApiKey(secretId, value);

    public void RemoveSecret(string secretId) => this.secretManager.RemoveApiKey(secretId);
}
