using System.Globalization;

namespace Cortex.Contained.Bridge.Connectors.Replay;

/// <summary>
/// Serialisation helpers for the replay cursor carried on every outbound frame.
/// The cursor is an ISO-8601 round-trip timestamp (format <c>o</c>).
/// </summary>
public static class ConnectorCursor
{
    /// <summary>
    /// Formats <paramref name="value"/> as an ISO-8601 round-trip string in UTC.
    /// </summary>
    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Tries to parse <paramref name="cursor"/> as an ISO-8601 round-trip timestamp.
    /// Returns <see langword="false"/> for null, empty, whitespace, or unparseable input.
    /// This method never throws — the cursor is untrusted wire input.
    /// </summary>
    public static bool TryParse(string? cursor, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        // Guard against absurdly large junk strings before handing them to the parser.
        if (cursor.Length > 64)
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            cursor,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out value);
    }
}
