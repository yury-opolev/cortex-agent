using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Cortex.Contained.Bridge.Auth;

namespace Cortex.Contained.Bridge.Evaluation;

/// <summary>
/// Short-lived grading grants: the owner enters the Bridge password once to grade the κ sample, and the grading
/// screen then fetches one decrypted item at a time with the grant instead of the password. A grant is bound to the
/// session cookie that requested it, expires after <see cref="IdleTimeout"/> without use (and after
/// <see cref="MaxLifetime"/> in any case), and lives in memory only.
/// </summary>
public sealed class GradingGrants(TimeProvider time)
{
    /// <summary>A grant unused for this long expires.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>No grant lives longer.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(2);

    /// <summary>Request header carrying the grant.</summary>
    public const string HeaderName = "X-Grading-Grant";

    private readonly ConcurrentDictionary<string, Grant> grants = new(StringComparer.Ordinal);

    /// <summary>A new grant for <paramref name="session"/> (the session cookie value).</summary>
    public string Issue(string? session)
    {
        var now = time.GetUtcNow();
        foreach (var expired in this.grants.Where(g => g.Value.Expired(now)).Select(g => g.Key).ToList())
        {
            this.grants.TryRemove(expired, out _);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        this.grants[token] = new Grant(Hash(session), now, now);
        return token;
    }

    /// <summary>True when <paramref name="token"/> is a live grant of <paramref name="session"/>; extends its idle time.</summary>
    public bool Validate(string? token, string? session)
    {
        if (string.IsNullOrEmpty(token) || !this.grants.TryGetValue(token, out var grant))
        {
            return false;
        }

        var now = time.GetUtcNow();
        if (grant.Expired(now) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(grant.Session), Encoding.ASCII.GetBytes(Hash(session))))
        {
            return false;
        }

        this.grants[token] = grant with { LastUsed = now };
        return true;
    }

    private static string Hash(string? session) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session ?? string.Empty)));

    private sealed record Grant(string Session, DateTimeOffset Issued, DateTimeOffset LastUsed)
    {
        public bool Expired(DateTimeOffset now) => now - this.LastUsed > IdleTimeout || now - this.Issued > MaxLifetime;
    }
}

/// <summary>Rate limit of the grading item endpoints (each returns decrypted text): generous for one person grading, tight for a script.</summary>
internal static class GradingRateLimit
{
    public const string PolicyName = "eval-grading";

    public const int PermitLimit = 60;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimitPartition<string> Partition(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var token = context.Request.Cookies.TryGetValue(CortexSessionAuthHandler.CookieName, out var value) ? value : string.Empty;
        var session = string.IsNullOrEmpty(token) ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{context.Connection.RemoteIpAddress}|{session}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = Window });
    }
}
