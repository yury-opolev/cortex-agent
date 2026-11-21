namespace Cortex.Contained.Common;

/// <summary>
/// The host's time zone as an IANA id, exported to <c>docker compose</c> as <see cref="EnvironmentVariable"/>
/// so the agent container (which runs in UTC) segments history at the owner's local midnight.
/// </summary>
public static class HostTimeZone
{
    /// <summary>Environment variable <c>docker-compose.yml</c> maps to <c>History__TimeZone</c>.</summary>
    public const string EnvironmentVariable = "CORTEX_TIMEZONE";

    /// <summary>The local zone as an IANA id, or empty when it has none (compose then keeps the container zone).</summary>
    public static string GetIanaId() => ToIanaId(TimeZoneInfo.Local);

    /// <summary>The IANA id of <paramref name="zone"/>, converting a Windows id; empty when there is no mapping.</summary>
    public static string ToIanaId(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return zone.HasIanaId
            ? zone.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : string.Empty;
    }

    /// <summary>Environment to add to a compose child process for the current host and process environment.</summary>
    public static IReadOnlyDictionary<string, string> ComposeEnvironment() =>
        ComposeEnvironment(Environment.GetEnvironmentVariable(EnvironmentVariable), GetIanaId());

    /// <summary>
    /// A value the user already set wins, and an unknown zone exports nothing; otherwise the zone is exported.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ComposeEnvironment(string? existing, string ianaId) =>
        string.IsNullOrWhiteSpace(existing) && !string.IsNullOrWhiteSpace(ianaId)
            ? new Dictionary<string, string> { [EnvironmentVariable] = ianaId }
            : new Dictionary<string, string>();
}
