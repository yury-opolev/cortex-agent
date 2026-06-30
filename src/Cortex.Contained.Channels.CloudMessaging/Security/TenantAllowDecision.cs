namespace Cortex.Contained.Channels.CloudMessaging.Security;

/// <summary>
/// Pure, stateless tenant-allow guard. Validates that an inbound envelope's
/// <c>tenantId</c> belongs to the set of tenants this bridge serves.
/// Extracted for unit-testability without a live connection.
/// </summary>
public static class TenantAllowDecision
{
    /// <summary>
    /// Returns true when <paramref name="tenantId"/> is non-empty and present
    /// in <paramref name="allowedTenants"/>.
    /// </summary>
    /// <param name="tenantId">The tenantId from an inbound envelope.</param>
    /// <param name="allowedTenants">
    /// The set of tenant IDs this bridge is authorised to serve (from negotiate-bridge).
    /// </param>
    public static bool IsAllowed(string? tenantId, IReadOnlyCollection<string> allowedTenants)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        if (allowedTenants is null || allowedTenants.Count == 0)
        {
            return false;
        }

        foreach (var allowed in allowedTenants)
        {
            if (string.Equals(tenantId, allowed, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
