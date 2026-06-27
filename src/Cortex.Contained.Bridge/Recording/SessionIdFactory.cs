using System.Globalization;
using System.Text;

namespace Cortex.Contained.Bridge.Recording;

/// <summary>
/// Canonical recording-session id: <c>&lt;label&gt;-yyyyMMdd-HHmmss</c> (UTC).
/// Label is sanitised to <c>[A-Za-z0-9_-]+</c> with runs of disallowed chars
/// collapsed to a single dash; empty after sanitisation falls back to
/// <c>"session"</c>. Used as both folder name and session identifier.
/// </summary>
public static class SessionIdFactory
{
    private const string DefaultLabel = "session";

    public static string Create(string? label, DateTimeOffset utcNow)
    {
        var safe = Sanitise(label);
        var stamp = utcNow.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return $"{safe}-{stamp}";
    }

    public static string Sanitise(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return DefaultLabel;
        }

        var sb = new StringBuilder(label.Length);
        foreach (var c in label)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        while (sb.Length > 0 && sb[^1] == '-')
        {
            sb.Length--;
        }

        return sb.Length == 0 ? DefaultLabel : sb.ToString();
    }
}
