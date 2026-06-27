using Cortex.Contained.Bridge.Auth;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the authentication endpoints (<c>/api/auth/*</c>). Status/setup/login are
/// unauthenticated; logout and change-password require an authenticated session.
/// </summary>
internal static class AuthEndpoints
{
    /// <summary>Maps the <c>/api/auth/*</c> endpoints onto <paramref name="app"/>.</summary>
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // --- Auth Endpoints (unauthenticated) ---
        app.MapGet("/api/auth/status", (SessionManager sessions) =>
            Results.Ok(new { isPasswordSet = sessions.IsPasswordSet() }));

        app.MapPost("/api/auth/setup", (PasswordRequest request, SessionManager sessions, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
            {
                return Results.Json(new { error = "Password must be at least 6 characters" }, statusCode: 400);
            }

            if (!sessions.SetupPassword(request.Password))
            {
                return Results.Json(new { error = "Password is already set. Use change-password instead." }, statusCode: 409);
            }

            // Auto-login after setup
            var token = sessions.Login(request.Password);
            if (token is null)
            {
                return Results.Json(new { error = "Password set but login failed unexpectedly" }, statusCode: 500);
            }

            ctx.Response.Cookies.Append(CortexSessionAuthHandler.CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false,
                Path = "/",
            });

            return Results.Ok(new { success = true });
        });

        app.MapPost("/api/auth/login", (PasswordRequest request, SessionManager sessions, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.Json(new { error = "Password is required" }, statusCode: 400);
            }

            var token = sessions.Login(request.Password);
            if (token is null)
            {
                return Results.Json(new { error = "Invalid password" }, statusCode: 401);
            }

            ctx.Response.Cookies.Append(CortexSessionAuthHandler.CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false, // localhost HTTP
                Path = "/",
                // No Expires/MaxAge = browser session cookie
            });

            return Results.Ok(new { success = true });
        }).RequireRateLimiting("login");

        app.MapPost("/api/auth/logout", (SessionManager sessions, HttpContext ctx) =>
        {
            if (ctx.Request.Cookies.TryGetValue(CortexSessionAuthHandler.CookieName, out var sessionToken)
                && !string.IsNullOrEmpty(sessionToken))
            {
                sessions.Logout(sessionToken);
            }

            ctx.Response.Cookies.Delete(CortexSessionAuthHandler.CookieName, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false,
                Path = "/",
            });

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        app.MapPost("/api/auth/change-password", (ChangePasswordRequest request, SessionManager sessions, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return Results.Json(new { error = "Both current and new password are required" }, statusCode: 400);
            }

            if (request.NewPassword.Length < 6)
            {
                return Results.Json(new { error = "New password must be at least 6 characters" }, statusCode: 400);
            }

            var callerToken = ctx.Request.Cookies[CortexSessionAuthHandler.CookieName];
            if (!sessions.ChangePassword(request.CurrentPassword, request.NewPassword, callerToken))
            {
                return Results.Json(new { error = "Current password is incorrect" }, statusCode: 401);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization();
    }
}
