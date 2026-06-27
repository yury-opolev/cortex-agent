using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary>
/// Authentication scheme name for hub token auth.
/// </summary>
public static class HubTokenDefaults
{
    public const string AuthenticationScheme = "HubToken";
}

/// <summary>
/// Options for hub token authentication.
/// </summary>
public sealed class HubTokenAuthOptions : AuthenticationSchemeOptions
{
    /// <summary>Shared secret token the Bridge must present to connect.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Maximum auth attempts before lockout.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Sliding window for rate limit tracking.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Duration of lockout after exceeding max attempts.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Authentication handler that validates Bearer tokens on SignalR connections.
/// Uses constant-time comparison and rate-limits failed attempts per IP.
/// </summary>
public sealed partial class HubTokenAuthHandler : AuthenticationHandler<HubTokenAuthOptions>
{
    private static readonly ConcurrentDictionary<string, RateLimitEntry> RateLimitTracker = new();
    private readonly ILogger logger;

    public HubTokenAuthHandler(
        IOptionsMonitor<HubTokenAuthOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder)
        : base(options, loggerFactory, encoder)
    {
        this.logger = loggerFactory.CreateLogger<HubTokenAuthHandler>();
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var remoteIp = Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Reject loopback connections — the Bridge connects from the Docker network,
        // never from inside the container. Loopback requests are from subagent run_command
        // or other in-container processes and must not reach the hub.
        if (Context.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip))
        {
            this.LogAuthRejectedLoopback(remoteIp);
            return Task.FromResult(AuthenticateResult.Fail("Loopback connections are not allowed."));
        }

        // Check rate limit lockout
        if (IsLockedOut(remoteIp))
        {
            this.LogAuthLocked(remoteIp);
            return Task.FromResult(AuthenticateResult.Fail("Too many failed attempts. Try again later."));
        }

        // Extract token from query string (SignalR) or Authorization header
        var token = ExtractToken();
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Constant-time comparison to prevent timing attacks
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token),
                Encoding.UTF8.GetBytes(Options.Token)))
        {
            RecordFailedAttempt(remoteIp);
            this.LogAuthFailed(remoteIp);
            return Task.FromResult(AuthenticateResult.Fail("Invalid token."));
        }

        // Clear rate limit on success
        RateLimitTracker.TryRemove(remoteIp, out _);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "bridge"),
            new Claim(ClaimTypes.Role, "bridge-client"),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        this.LogAuthSuccess(remoteIp);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string? ExtractToken()
    {
        // SignalR WebSocket connections pass token via query string
        var queryToken = Request.Query["access_token"].ToString();
        if (!string.IsNullOrEmpty(queryToken))
        {
            return queryToken;
        }

        // Also support standard Authorization: Bearer <token> header
        var authHeader = Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader["Bearer ".Length..].Trim();
        }

        return null;
    }

    private static bool IsLockedOut(string ip)
    {
        if (!RateLimitTracker.TryGetValue(ip, out var entry))
        {
            return false;
        }

        // If locked out and lockout hasn't expired
        if (entry.LockedUntil.HasValue && entry.LockedUntil.Value > DateTimeOffset.UtcNow)
        {
            return true;
        }

        // If lockout expired, reset
        if (entry.LockedUntil.HasValue && entry.LockedUntil.Value <= DateTimeOffset.UtcNow)
        {
            RateLimitTracker.TryRemove(ip, out _);
        }

        return false;
    }

    private void RecordFailedAttempt(string ip)
    {
        var entry = RateLimitTracker.GetOrAdd(ip, _ => new RateLimitEntry());
        var now = DateTimeOffset.UtcNow;

        // Clean old attempts outside the window
        lock (entry)
        {
            entry.Attempts.RemoveAll(t => t < now - Options.Window);
            entry.Attempts.Add(now);

            if (entry.Attempts.Count >= Options.MaxAttempts)
            {
                entry.LockedUntil = now + Options.LockoutDuration;
                this.LogRateLimitTriggered(ip, Options.LockoutDuration.TotalSeconds);
            }
        }
    }

    /// <summary>Allows tests to clear rate limit state.</summary>
    internal static void ResetRateLimits() => RateLimitTracker.Clear();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected loopback connection from {RemoteIp}")]
    private partial void LogAuthRejectedLoopback(string remoteIp);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auth attempt from locked-out IP {RemoteIp}")]
    private partial void LogAuthLocked(string remoteIp);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auth failed from {RemoteIp}")]
    private partial void LogAuthFailed(string remoteIp);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bridge authenticated from {RemoteIp}")]
    private partial void LogAuthSuccess(string remoteIp);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit triggered for {RemoteIp}, locked for {LockoutSeconds}s")]
    private partial void LogRateLimitTriggered(string remoteIp, double lockoutSeconds);

    private sealed class RateLimitEntry
    {
        public List<DateTimeOffset> Attempts { get; } = [];
        public DateTimeOffset? LockedUntil { get; set; }
    }
}
