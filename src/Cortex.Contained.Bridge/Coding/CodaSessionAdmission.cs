namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Pure, stateless helper for the per-tenant concurrent-session ceiling. Extracted so it
/// can be unit-tested without a process or DI (mirrors <see cref="CodaSessionPolicy"/>).
/// </summary>
public static class CodaSessionAdmission
{
    /// <summary>
    /// Returns an error when starting one more session for <paramref name="tenantId"/> would
    /// exceed <paramref name="maxSessions"/>, or <c>null</c> when admission is allowed.
    /// </summary>
    /// <param name="liveSessionTenantIds">
    /// The tenant id of every currently-live session (may contain <c>null</c> entries, which
    /// are never counted against a named tenant).
    /// </param>
    public static CodaSessionPolicy.PolicyError? CheckTenantCeiling(
        IEnumerable<string?> liveSessionTenantIds,
        string tenantId,
        int maxSessions)
    {
        ArgumentNullException.ThrowIfNull(liveSessionTenantIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var count = liveSessionTenantIds.Count(id => string.Equals(id, tenantId, StringComparison.Ordinal));

        if (count >= maxSessions)
        {
            return new CodaSessionPolicy.PolicyError(
                CodingAgentErrorCodes.MaxSessionsReached,
                $"Tenant '{tenantId}' already has {count} active coding session(s); the limit is {maxSessions}. End one first.");
        }

        return null;
    }
}
