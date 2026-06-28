namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Read/write seam for the DPAPI-backed secret store used by <see cref="McpTokenStore"/>. Keeps the
/// token store unit-testable with an in-memory fake and reuses the Bridge's encryption-at-rest.
/// </summary>
public interface IMcpTokenSecretStore
{
    /// <summary>Returns the decrypted value for <paramref name="secretId"/>, or null when absent.</summary>
    string? GetSecret(string secretId);

    /// <summary>Encrypts and stores <paramref name="value"/> under <paramref name="secretId"/>.</summary>
    void SetSecret(string secretId, string value);

    /// <summary>Removes the stored value for <paramref name="secretId"/> (no-op when absent).</summary>
    void RemoveSecret(string secretId);
}
