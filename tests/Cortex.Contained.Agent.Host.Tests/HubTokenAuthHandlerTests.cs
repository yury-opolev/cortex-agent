using System.Security.Claims;
using System.Text.Encodings.Web;
using Cortex.Contained.Agent.Host.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

public class HubTokenAuthHandlerTests : IDisposable
{
    private const string ValidToken = "super-secret-test-token-1234567890";
    private const string TestIp = "172.18.0.1"; // Non-loopback (simulates Bridge on Docker network)

    public HubTokenAuthHandlerTests()
    {
        HubTokenAuthHandler.ResetRateLimits();
    }

    public void Dispose()
    {
        HubTokenAuthHandler.ResetRateLimits();
        GC.SuppressFinalize(this);
    }

    // ── Token extraction & validation ─────────────────────────────

    [Fact]
    public async Task HandleAuthenticateAsync_ValidQueryToken_ReturnsSuccess()
    {
        var result = await Authenticate(ValidToken, queryToken: ValidToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        Assert.Equal("bridge", result.Principal.Identity?.Name);
        Assert.True(result.Principal.IsInRole("bridge-client"));
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ValidBearerToken_ReturnsSuccess()
    {
        var result = await Authenticate(ValidToken, bearerToken: ValidToken);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_InvalidToken_ReturnsFail()
    {
        var result = await Authenticate(ValidToken, queryToken: "wrong-token");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Contains("Invalid token", result.Failure.Message);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_LoopbackIPv4_Rejected()
    {
        var result = await Authenticate(ValidToken, queryToken: ValidToken, ip: "127.0.0.1");

        Assert.False(result.Succeeded);
        Assert.Contains("Loopback", result.Failure!.Message);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_LoopbackIPv6_Rejected()
    {
        var result = await Authenticate(ValidToken, queryToken: ValidToken, ip: "::1");

        Assert.False(result.Succeeded);
        Assert.Contains("Loopback", result.Failure!.Message);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_MissingToken_ReturnsNoResult()
    {
        var result = await Authenticate(ValidToken); // no token

        Assert.True(result.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_EmptyQueryToken_ReturnsNoResult()
    {
        var result = await Authenticate(ValidToken, queryToken: "");

        Assert.True(result.None);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_BearerPrefix_CaseInsensitive()
    {
        // Manually set lowercase "bearer" prefix
        var context = CreateHttpContext(ip: TestIp);
        context.Request.Headers.Authorization = $"bearer {ValidToken}";
        var result = await AuthenticateWithContext(ValidToken, context);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_QueryTokenTakesPrecedence()
    {
        // Both query and header present; query token should be used
        var context = CreateHttpContext(queryToken: ValidToken, ip: TestIp);
        context.Request.Headers.Authorization = $"Bearer wrong-token";
        var result = await AuthenticateWithContext(ValidToken, context);

        Assert.True(result.Succeeded);
    }

    // ── Claims ────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAuthenticateAsync_Success_SetsBridgeClaims()
    {
        var result = await Authenticate(ValidToken, queryToken: ValidToken);

        Assert.True(result.Succeeded);
        var claims = result.Principal!.Claims.ToList();
        Assert.Contains(claims, c => c.Type == ClaimTypes.Name && c.Value == "bridge");
        Assert.Contains(claims, c => c.Type == ClaimTypes.Role && c.Value == "bridge-client");
    }

    // ── Rate limiting ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAuthenticateAsync_ExceedsMaxAttempts_LocksOut()
    {
        const int maxAttempts = 3;

        // Fail enough times to trigger lockout
        for (int i = 0; i < maxAttempts; i++)
        {
            await Authenticate(ValidToken, queryToken: "bad-token", ip: TestIp, maxAttempts: maxAttempts, lockoutSeconds: 300);
        }

        // Next attempt with VALID token should still be locked out
        var result = await Authenticate(ValidToken, queryToken: ValidToken, ip: TestIp, maxAttempts: maxAttempts, lockoutSeconds: 300);

        Assert.False(result.Succeeded);
        Assert.Contains("Too many failed attempts", result.Failure!.Message);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_DifferentIps_IndependentRateLimits()
    {
        const int maxAttempts = 3;

        // Lock out IP1
        for (int i = 0; i < maxAttempts; i++)
        {
            await Authenticate(ValidToken, queryToken: "bad", ip: "10.0.0.1", maxAttempts: maxAttempts, lockoutSeconds: 300);
        }

        // IP2 should still work
        var result = await Authenticate(ValidToken, queryToken: ValidToken, ip: "10.0.0.2", maxAttempts: maxAttempts, lockoutSeconds: 300);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_SuccessAfterFailures_ClearsRateLimit()
    {
        const int maxAttempts = 5;

        // Fail a few times (but not enough to lock out)
        for (int i = 0; i < maxAttempts - 2; i++)
        {
            await Authenticate(ValidToken, queryToken: "bad", ip: TestIp, maxAttempts: maxAttempts);
        }

        // Now succeed — this should clear the rate limit counter
        var successResult = await Authenticate(ValidToken, queryToken: ValidToken, ip: TestIp, maxAttempts: maxAttempts);
        Assert.True(successResult.Succeeded);

        // Fail again the same number of times — should not lock out since counter was reset
        for (int i = 0; i < maxAttempts - 2; i++)
        {
            await Authenticate(ValidToken, queryToken: "bad", ip: TestIp, maxAttempts: maxAttempts);
        }

        // Should still not be locked out
        var checkResult = await Authenticate(ValidToken, queryToken: ValidToken, ip: TestIp, maxAttempts: maxAttempts);
        Assert.True(checkResult.Succeeded);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_LockoutExpires_AllowsRetry()
    {
        const int maxAttempts = 2;

        // Trigger lockout with 1-second duration
        for (int i = 0; i < maxAttempts; i++)
        {
            await Authenticate(ValidToken, queryToken: "bad", ip: TestIp, maxAttempts: maxAttempts, lockoutSeconds: 1);
        }

        // Verify locked
        var lockedResult = await Authenticate(ValidToken, queryToken: ValidToken, ip: TestIp, maxAttempts: maxAttempts, lockoutSeconds: 1);
        Assert.False(lockedResult.Succeeded);

        // Wait for lockout to expire
        await Task.Delay(1100);

        // Should now be able to auth
        var retryResult = await Authenticate(ValidToken, queryToken: ValidToken, ip: TestIp, maxAttempts: maxAttempts, lockoutSeconds: 1);
        Assert.True(retryResult.Succeeded);
    }

    // ── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a fresh handler + context and runs authentication.
    /// Each call creates a new handler to avoid AuthenticateAsync caching in the base class.
    /// Rate limit state persists via the static ConcurrentDictionary.
    /// </summary>
    private static async Task<AuthenticateResult> Authenticate(
        string configuredToken,
        string? queryToken = null,
        string? bearerToken = null,
        string ip = TestIp,
        int maxAttempts = 10,
        int lockoutSeconds = 300)
    {
        var context = CreateHttpContext(queryToken, bearerToken, ip);
        return await AuthenticateWithContext(configuredToken, context, maxAttempts, lockoutSeconds);
    }

    private static async Task<AuthenticateResult> AuthenticateWithContext(
        string configuredToken,
        DefaultHttpContext context,
        int maxAttempts = 10,
        int lockoutSeconds = 300)
    {
        var handler = CreateHandler(configuredToken, maxAttempts, lockoutSeconds);
        var scheme = new AuthenticationScheme(
            HubTokenDefaults.AuthenticationScheme,
            displayName: null,
            typeof(HubTokenAuthHandler));

        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    private static HubTokenAuthHandler CreateHandler(
        string configuredToken,
        int maxAttempts = 10,
        int lockoutSeconds = 300)
    {
        var options = new HubTokenAuthOptions
        {
            Token = configuredToken,
            MaxAttempts = maxAttempts,
            Window = TimeSpan.FromSeconds(60),
            LockoutDuration = TimeSpan.FromSeconds(lockoutSeconds),
        };

        var optionsMonitor = Substitute.For<IOptionsMonitor<HubTokenAuthOptions>>();
        optionsMonitor.CurrentValue.Returns(options);
        optionsMonitor.Get(Arg.Any<string>()).Returns(options);

        return new HubTokenAuthHandler(
            optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default);
    }

    private static DefaultHttpContext CreateHttpContext(
        string? queryToken = null,
        string? bearerToken = null,
        string ip = TestIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);

        if (queryToken is not null)
        {
            context.Request.QueryString = new QueryString($"?access_token={Uri.EscapeDataString(queryToken)}");
        }

        if (bearerToken is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {bearerToken}";
        }

        return context;
    }
}
