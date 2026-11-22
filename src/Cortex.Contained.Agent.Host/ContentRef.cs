using System.Security.Cryptography;
using System.Text;

namespace Cortex.Contained.Agent.Host;

/// <summary>
/// A log-safe reference to user-written text (a task description, a to-do list name, tool arguments): the length
/// and a short hash, never the text. Logs live 14 days in the data volume; with encryption at rest they must not
/// carry content. The hash lets one log line be matched to another without revealing what it says.
/// </summary>
/// <param name="Text">The text referenced (never logged).</param>
public readonly record struct ContentRef(string? Text)
{
    /// <summary>Implicit conversion so call sites keep passing the text; only the reference is formatted.</summary>
    public static implicit operator ContentRef(string? text) => new(text);

    /// <summary>The named alternate of the implicit conversion.</summary>
    public static ContentRef FromString(string? text) => new(text);

    /// <summary>For example <c>12 chars #a1b2c3d4</c>.</summary>
    public override string ToString()
    {
        if (string.IsNullOrEmpty(this.Text))
        {
            return "0 chars";
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(this.Text)))[..8];
        return $"{this.Text.Length} chars #{hash}";
    }
}
