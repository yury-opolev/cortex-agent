using System.Diagnostics.CodeAnalysis;

namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Encodes/decodes a coding-agent error code into the single message string that survives a
/// SignalR client-result fault.
/// <para>
/// When the Bridge (SignalR client) throws inside a hub-invoked handler, only the exception
/// <em>message</em> reaches the Agent Host (server) — structured fields like an error code are
/// lost. To preserve the stable code, the Bridge encodes it as a parseable prefix and the agent
/// decodes it back. Format: <c>[coda_err:&lt;code&gt;] &lt;message&gt;</c>.
/// </para>
/// </summary>
public static class CodingErrorWire
{
    private const string Prefix = "[coda_err:";

    /// <summary>Encodes <paramref name="code"/> + <paramref name="message"/> into a single wire string.</summary>
    public static string Encode(string code, string message) => $"{Prefix}{code}] {message}";

    /// <summary>
    /// Decodes a wire string produced by <see cref="Encode"/>. Returns false (and null outputs)
    /// for any string that is not in the encoded form.
    /// </summary>
    public static bool TryDecode(string? wire, [NotNullWhen(true)] out string? code, [NotNullWhen(true)] out string? message)
    {
        code = null;
        message = null;
        if (string.IsNullOrEmpty(wire) || !wire.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var close = wire.IndexOf("] ", Prefix.Length, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        code = wire[Prefix.Length..close];
        message = wire[(close + 2)..];
        return code.Length > 0;
    }
}
