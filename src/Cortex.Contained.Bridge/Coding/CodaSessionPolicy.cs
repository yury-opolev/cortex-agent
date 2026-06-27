using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Pure, stateless helper for resolving and validating a session's <see cref="CodingPolicy"/>
/// against a folder ceiling.  Extracted so it can be unit-tested without a process or DI.
/// </summary>
public static class CodaSessionPolicy
{
    /// <summary>
    /// Resolves the effective <see cref="CodingPolicy"/> for a session.
    /// </summary>
    /// <param name="requested">
    /// The explicitly requested policy, or <c>null</c> to use the folder's configured ceiling
    /// as the default.
    /// </param>
    /// <param name="ceiling">
    /// The maximum permitted policy for the containing folder.  Acts as both the ceiling and
    /// the default when <paramref name="requested"/> is <c>null</c>.
    /// </param>
    /// <returns>
    /// A tuple of <c>(policy, null)</c> on success, or <c>(default, errorInfo)</c> when the
    /// requested policy exceeds the ceiling.
    /// </returns>
    public static (CodingPolicy Policy, PolicyError? Error) Resolve(
        CodingPolicy? requested,
        CodingPolicy ceiling)
    {
        var effective = requested ?? ceiling;

        if (!effective.IsWithinCeiling(ceiling))
        {
            return (default, new PolicyError(
                CodingAgentErrorCodes.FolderNotAllowed,
                $"Requested policy '{effective}' exceeds the folder ceiling '{ceiling}'. " +
                "Raise the folder ceiling via the Bridge web UI > Settings > Coding > Folders, " +
                "or use a less permissive policy."));
        }

        return (effective, null);
    }

    /// <summary>Details of a policy rejection from <see cref="Resolve"/>.</summary>
    public readonly record struct PolicyError(string ErrorCode, string Message);
}
