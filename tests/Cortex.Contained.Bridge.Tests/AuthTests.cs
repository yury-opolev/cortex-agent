using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace Cortex.Contained.Bridge.Tests;

public class SessionManagerTests
{
    private readonly SecretManager _secretManager;
    private readonly SessionManager _sessionManager;

    public SessionManagerTests()
    {
        var store = new InMemorySecretStore();
        var tempDir = Path.Combine(Path.GetTempPath(), "cortex-test-" + Guid.NewGuid().ToString("N"));
        _secretManager = new SecretManager(store, NullLogger<SecretManager>.Instance, tempDir);
        _sessionManager = new SessionManager(_secretManager, NullLogger<SessionManager>.Instance);
    }

    // ── IsPasswordSet ──────────────────────────────────────────

    [Fact]
    public void IsPasswordSet_ReturnsFalse_WhenNoPasswordSet()
    {
        Assert.False(_sessionManager.IsPasswordSet());
    }

    [Fact]
    public void IsPasswordSet_ReturnsTrue_AfterPasswordSetup()
    {
        _sessionManager.SetupPassword("testPassword123");
        Assert.True(_sessionManager.IsPasswordSet());
    }

    // ── SetupPassword ──────────────────────────────────────────

    [Fact]
    public void SetupPassword_ReturnsTrue_OnFirstSetup()
    {
        var result = _sessionManager.SetupPassword("myPassword123");
        Assert.True(result);
    }

    [Fact]
    public void SetupPassword_ReturnsFalse_WhenPasswordAlreadySet()
    {
        _sessionManager.SetupPassword("firstPassword");
        var result = _sessionManager.SetupPassword("secondPassword");
        Assert.False(result);
    }

    // ── Login ──────────────────────────────────────────────────

    [Fact]
    public void Login_ReturnsNull_WhenNoPasswordSet()
    {
        var token = _sessionManager.Login("anyPassword");
        Assert.Null(token);
    }

    [Fact]
    public void Login_ReturnsToken_WithCorrectPassword()
    {
        _sessionManager.SetupPassword("correctPassword");
        var token = _sessionManager.Login("correctPassword");

        Assert.NotNull(token);
        Assert.NotEmpty(token);
    }

    [Fact]
    public void Login_ReturnsNull_WithIncorrectPassword()
    {
        _sessionManager.SetupPassword("correctPassword");
        var token = _sessionManager.Login("wrongPassword");

        Assert.Null(token);
    }

    [Fact]
    public void Login_ReturnsUniqueTokens_ForMultipleLogins()
    {
        _sessionManager.SetupPassword("password");
        var token1 = _sessionManager.Login("password");
        var token2 = _sessionManager.Login("password");

        Assert.NotNull(token1);
        Assert.NotNull(token2);
        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public void Login_IncrementsActiveSessionCount()
    {
        _sessionManager.SetupPassword("password");
        Assert.Equal(0, _sessionManager.ActiveSessionCount);

        _sessionManager.Login("password");
        Assert.Equal(1, _sessionManager.ActiveSessionCount);

        _sessionManager.Login("password");
        Assert.Equal(2, _sessionManager.ActiveSessionCount);
    }

    // ── ValidateSession ────────────────────────────────────────

    [Fact]
    public void ValidateSession_ReturnsTrue_ForValidToken()
    {
        _sessionManager.SetupPassword("password");
        var token = _sessionManager.Login("password")!;

        Assert.True(_sessionManager.ValidateSession(token));
    }

    [Fact]
    public void ValidateSession_ReturnsFalse_ForInvalidToken()
    {
        Assert.False(_sessionManager.ValidateSession("not-a-real-token"));
    }

    [Fact]
    public void ValidateSession_ReturnsFalse_AfterLogout()
    {
        _sessionManager.SetupPassword("password");
        var token = _sessionManager.Login("password")!;

        _sessionManager.Logout(token);
        Assert.False(_sessionManager.ValidateSession(token));
    }

    // ── Logout ─────────────────────────────────────────────────

    [Fact]
    public void Logout_DecrementsActiveSessionCount()
    {
        _sessionManager.SetupPassword("password");
        var token = _sessionManager.Login("password")!;
        Assert.Equal(1, _sessionManager.ActiveSessionCount);

        _sessionManager.Logout(token);
        Assert.Equal(0, _sessionManager.ActiveSessionCount);
    }

    [Fact]
    public void Logout_IsIdempotent_ForUnknownToken()
    {
        // Should not throw
        _sessionManager.Logout("nonexistent-token");
    }

    // ── ChangePassword ─────────────────────────────────────────

    [Fact]
    public void ChangePassword_ReturnsTrue_WithCorrectCurrentPassword()
    {
        _sessionManager.SetupPassword("oldPassword");
        var result = _sessionManager.ChangePassword("oldPassword", "newPassword");

        Assert.True(result);
    }

    [Fact]
    public void ChangePassword_ReturnsFalse_WithWrongCurrentPassword()
    {
        _sessionManager.SetupPassword("oldPassword");
        var result = _sessionManager.ChangePassword("wrongPassword", "newPassword");

        Assert.False(result);
    }

    [Fact]
    public void ChangePassword_ReturnsFalse_WhenNoPasswordSet()
    {
        var result = _sessionManager.ChangePassword("any", "new");
        Assert.False(result);
    }

    [Fact]
    public void ChangePassword_NewPasswordWorksAfterChange()
    {
        _sessionManager.SetupPassword("oldPassword");
        _sessionManager.ChangePassword("oldPassword", "newPassword");

        // Old password should no longer work
        Assert.Null(_sessionManager.Login("oldPassword"));

        // New password should work
        Assert.NotNull(_sessionManager.Login("newPassword"));
    }

    [Fact]
    public void ChangePassword_InvalidatesOtherSessions()
    {
        _sessionManager.SetupPassword("password");
        var session1 = _sessionManager.Login("password")!;
        var session2 = _sessionManager.Login("password")!;
        var callerSession = _sessionManager.Login("password")!;

        Assert.Equal(3, _sessionManager.ActiveSessionCount);

        _sessionManager.ChangePassword("password", "newPassword", callerSession);

        // Caller's session should survive
        Assert.True(_sessionManager.ValidateSession(callerSession));

        // Other sessions should be invalidated
        Assert.False(_sessionManager.ValidateSession(session1));
        Assert.False(_sessionManager.ValidateSession(session2));

        Assert.Equal(1, _sessionManager.ActiveSessionCount);
    }

    [Fact]
    public void ChangePassword_InvalidatesAllSessions_WhenNoCallerToken()
    {
        _sessionManager.SetupPassword("password");
        _sessionManager.Login("password");
        _sessionManager.Login("password");
        Assert.Equal(2, _sessionManager.ActiveSessionCount);

        _sessionManager.ChangePassword("password", "newPassword");

        Assert.Equal(0, _sessionManager.ActiveSessionCount);
    }
}

public class CortexSessionAuthHandlerTests
{
    private readonly SessionManager _sessionManager;

    public CortexSessionAuthHandlerTests()
    {
        var store = new InMemorySecretStore();
        var tempDir = Path.Combine(Path.GetTempPath(), "cortex-test-" + Guid.NewGuid().ToString("N"));
        var secretManager = new SecretManager(store, NullLogger<SecretManager>.Instance, tempDir);
        _sessionManager = new SessionManager(secretManager, NullLogger<SessionManager>.Instance);
    }

    private CortexSessionAuthHandler CreateHandler(HttpContext context)
    {
        var options = new OptionsMonitor<AuthenticationSchemeOptions>(
            new OptionsFactory<AuthenticationSchemeOptions>([], []),
            [],
            new OptionsCache<AuthenticationSchemeOptions>());

        var handler = new CortexSessionAuthHandler(
            options,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            _sessionManager);

        var scheme = new AuthenticationScheme(
            CortexSessionAuthHandler.SchemeName,
            displayName: null,
            typeof(CortexSessionAuthHandler));

        handler.InitializeAsync(scheme, context).GetAwaiter().GetResult();

        return handler;
    }

    [Fact]
    public async Task HandleAuthenticate_ReturnsNoResult_WhenNoCookie()
    {
        var context = new DefaultHttpContext();
        var handler = CreateHandler(context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task HandleAuthenticate_ReturnsFail_WhenInvalidSession()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Append("Cookie", $"{CortexSessionAuthHandler.CookieName}=invalid-token");
        var handler = CreateHandler(context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.None); // Fail, not NoResult
    }

    [Fact]
    public async Task HandleAuthenticate_ReturnsSuccess_WhenValidSession()
    {
        _sessionManager.SetupPassword("password");
        var token = _sessionManager.Login("password")!;

        var context = new DefaultHttpContext();
        context.Request.Headers.Append("Cookie", $"{CortexSessionAuthHandler.CookieName}={token}");
        var handler = CreateHandler(context);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("bridge-user", result.Principal?.Identity?.Name);
        Assert.Equal(CortexSessionAuthHandler.SchemeName, result.Ticket?.AuthenticationScheme);
    }

    [Fact]
    public async Task HandleChallenge_Returns401()
    {
        var context = new DefaultHttpContext();
        var handler = CreateHandler(context);

        await handler.ChallengeAsync(null);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAuthenticate_ReturnsNoResult_WhenEmptyCookieValue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Append("Cookie", $"{CortexSessionAuthHandler.CookieName}=");
        var handler = CreateHandler(context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task HandleAuthenticate_ReturnsFail_AfterSessionLogout()
    {
        _sessionManager.SetupPassword("password");
        var token = _sessionManager.Login("password")!;
        _sessionManager.Logout(token);

        var context = new DefaultHttpContext();
        context.Request.Headers.Append("Cookie", $"{CortexSessionAuthHandler.CookieName}={token}");
        var handler = CreateHandler(context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
    }
}

/// <summary>
/// Wrapper for <see cref="IOptionsMonitor{TOptions}"/> that works without DI for testing.
/// </summary>
file sealed class OptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
{
    private readonly IOptionsFactory<T> _factory;
    private readonly IEnumerable<IOptionsChangeTokenSource<T>> _sources;
    private readonly IOptionsMonitorCache<T> _cache;

    public OptionsMonitor(IOptionsFactory<T> factory, IEnumerable<IOptionsChangeTokenSource<T>> sources, IOptionsMonitorCache<T> cache)
    {
        _factory = factory;
        _sources = sources;
        _cache = cache;
    }

    public T CurrentValue => Get(Options.DefaultName);

    public T Get(string? name) => _cache.GetOrAdd(name ?? Options.DefaultName, () => _factory.Create(name ?? Options.DefaultName));

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
