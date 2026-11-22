using System.Security.Cryptography;
using System.Text;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>Content hashes recorded in event payloads instead of the content itself.</summary>
public static class EventHashing
{
    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static string Sha256Hex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
