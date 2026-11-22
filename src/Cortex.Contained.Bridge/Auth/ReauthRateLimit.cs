using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace Cortex.Contained.Bridge.Auth;

/// <summary>
/// Rate limit of the re-authentication endpoints (enabling encryption at rest, exporting and restoring the
/// recovery kit): each of them checks the Bridge password again, so each is a password-guessing oracle for a
/// stolen session. Partitioned per session and client address, stricter than a normal request budget.
/// </summary>
internal static class ReauthRateLimit
{
    /// <summary>Name of the policy registered in <c>AddRateLimiter</c> and applied to the three endpoints.</summary>
    public const string PolicyName = "reauth";

    /// <summary>Attempts per window and partition.</summary>
    public const int PermitLimit = 5;

    /// <summary>The window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>The partition (and limiter) of <paramref name="context"/>.</summary>
    public static RateLimitPartition<string> Partition(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var token = context.Request.Cookies.TryGetValue(CortexSessionAuthHandler.CookieName, out var value) ? value : string.Empty;
        var session = string.IsNullOrEmpty(token) ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{address}|{session}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = Window });
    }
}
