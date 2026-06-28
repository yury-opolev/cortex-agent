namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Resolves an MCP secret reference id (e.g. <c>mcp/github/apikey</c>) to its plaintext value
/// from the Bridge's DPAPI-backed store. The seam that keeps secret values out of config and tests.
/// </summary>
public interface IMcpSecretResolver
{
    /// <summary>Returns the decrypted secret for <paramref name="secretId"/>, or null when absent.</summary>
    string? GetSecret(string secretId);
}
