using System.Globalization;

namespace Cortex.Contained.Bridge.Tenants;

/// <summary>
/// Pure input-handling helpers for the per-channel history endpoints
/// (<c>GET /api/tenants/{tenantId}/channels</c> and
/// <c>DELETE /api/tenants/{tenantId}/channels/{channelId}/history</c>).
/// Extracted into a static class so that URL decoding and timestamp parsing
/// can be unit-tested without spinning up the whole web host.
/// </summary>
public static class ChannelHistoryEndpoints
{
    /// <summary>
    /// URL-decodes the raw route segment and validates that it is non-empty after trimming.
    /// Returns false on null, empty, whitespace, or inputs that decode to whitespace.
    /// </summary>
    public static bool TryParseChannelId(string? raw, out string? decoded)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            decoded = null;
            return false;
        }

        var unescaped = Uri.UnescapeDataString(raw);
        if (string.IsNullOrWhiteSpace(unescaped))
        {
            decoded = null;
            return false;
        }

        decoded = unescaped;
        return true;
    }

    /// <summary>
    /// Parses an ISO-8601 <c>olderThan</c> query parameter. When the value is null
    /// or empty the method succeeds and returns <see cref="DateTimeOffset.MaxValue"/>,
    /// meaning "no upper bound — clear everything". Invalid values produce false.
    /// </summary>
    public static bool TryParseOlderThan(string? raw, out DateTimeOffset cutoff)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            cutoff = DateTimeOffset.MaxValue;
            return true;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out cutoff);
    }
}
