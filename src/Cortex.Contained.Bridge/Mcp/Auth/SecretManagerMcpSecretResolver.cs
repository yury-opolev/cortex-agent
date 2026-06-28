using Cortex.Contained.Bridge.Security;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Adapts the Bridge's DPAPI-backed <see cref="SecretManager"/> to <see cref="IMcpSecretResolver"/>.
/// MCP secrets are stored under their <c>secretRef</c> id (e.g. <c>mcp/github/apikey</c>) via the
/// generic api-key store, so encryption-at-rest is reused as-is.
/// </summary>
public sealed class SecretManagerMcpSecretResolver : IMcpSecretResolver
{
    private readonly SecretManager secretManager;

    public SecretManagerMcpSecretResolver(SecretManager secretManager)
    {
        this.secretManager = secretManager;
    }

    public string? GetSecret(string secretId)
        => this.secretManager.GetApiKey(secretId);
}
