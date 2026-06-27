using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Bridge.Auth;

/// <summary>
/// ASP.NET Core authentication handler that validates the <c>cortex_session</c> cookie
/// against in-memory sessions managed by <see cref="SessionManager"/>.
/// </summary>
public sealed class CortexSessionAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    internal const string SchemeName = "CortexSession";
    internal const string CookieName = "cortex_session";

    private readonly SessionManager sessionManager;

    public CortexSessionAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        SessionManager sessionManager)
        : base(options, logger, encoder)
    {
        this.sessionManager = sessionManager;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(CookieName, out var sessionToken) || string.IsNullOrEmpty(sessionToken))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!this.sessionManager.ValidateSession(sessionToken))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired session"));
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "bridge-user") };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
