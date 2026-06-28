namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Pure parser for the secret-reference token convention used in stdio <c>env</c> values:
/// <c>${secret:&lt;id&gt;}</c> denotes "resolve DPAPI secret &lt;id&gt; here"; any other value is a literal.
/// </summary>
public static class McpSecretRef
{
    private const string Opening = "${secret:";
    private const char Closing = '}';

    /// <summary>
    /// When <paramref name="value"/> is exactly a <c>${secret:id}</c> token, returns true and emits
    /// the non-empty secret <paramref name="secretId"/>; otherwise false (the value is a literal).
    /// </summary>
    public static bool TryParse(string? value, out string secretId)
    {
        secretId = string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (!value.StartsWith(Opening, StringComparison.Ordinal) || value[^1] != Closing)
        {
            return false;
        }

        var id = value[Opening.Length..^1];
        if (id.Length == 0)
        {
            return false;
        }

        secretId = id;
        return true;
    }
}
