using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Common.Security;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Auth;

/// <summary>
/// Manages browser session cookies and password authentication for the Bridge web UI.
/// Sessions are stored in-memory and do not survive Bridge restarts.
/// Password hash is stored in DPAPI-encrypted secrets via <see cref="SecretManager"/>.
/// </summary>
public sealed partial class SessionManager
{
    private readonly ConcurrentDictionary<string, SessionInfo> sessions = new();
    private readonly SecretManager secretManager;
    private readonly ILogger<SessionManager> logger;
    private readonly TimeProvider timeProvider;

    public SessionManager(SecretManager secretManager, ILogger<SessionManager> logger, TimeProvider? timeProvider = null)
    {
        this.secretManager = secretManager;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns true if a password has been set (i.e., password hash exists in secret store).
    /// </summary>
    public bool IsPasswordSet()
    {
        return this.secretManager.GetPasswordHash() is not null;
    }

    /// <summary>
    /// Sets the initial password. Returns false if a password is already set (use <see cref="ChangePassword"/> instead).
    /// </summary>
    public bool SetupPassword(string password)
    {
        if (IsPasswordSet())
        {
            return false;
        }

        var hash = BCrypt.Net.BCrypt.EnhancedHashPassword(password, workFactor: 12);
        this.secretManager.SetPasswordHash(hash);
        this.LogPasswordSetup();

        return true;
    }

    /// <summary>
    /// Validates the given password against the stored bcrypt hash.
    /// Returns a session token on success, or null on failure.
    /// </summary>
    public string? Login(string password)
    {
        var storedHash = this.secretManager.GetPasswordHash();
        if (storedHash is null)
        {
            return null;
        }

        if (!BCrypt.Net.BCrypt.EnhancedVerify(password, storedHash))
        {
            this.LogLoginFailed();
            return null;
        }

        var sessionToken = GenerateSessionToken();
        var session = new SessionInfo(this.timeProvider.GetUtcNow());
        this.sessions[sessionToken] = session;
        this.LogLoginSucceeded();

        return sessionToken;
    }

    /// <summary>
    /// Validates a session token. Returns true if the session exists.
    /// </summary>
    public bool ValidateSession(string sessionToken)
    {
        return this.sessions.ContainsKey(sessionToken);
    }

    /// <summary>
    /// Removes a session (logout).
    /// </summary>
    public void Logout(string sessionToken)
    {
        this.sessions.TryRemove(sessionToken, out _);
        this.LogLogout();
    }

    /// <summary>
    /// Changes the password. Validates the current password first, then updates the hash.
    /// Invalidates all other sessions on success (keeps only the caller's session).
    /// </summary>
    public bool ChangePassword(string currentPassword, string newPassword, string? callerSessionToken = null)
    {
        var storedHash = this.secretManager.GetPasswordHash();
        if (storedHash is null)
        {
            return false;
        }

        if (!BCrypt.Net.BCrypt.EnhancedVerify(currentPassword, storedHash))
        {
            this.LogChangePasswordFailed();
            return false;
        }

        var newHash = BCrypt.Net.BCrypt.EnhancedHashPassword(newPassword, workFactor: 12);
        this.secretManager.SetPasswordHash(newHash);

        // Invalidate all sessions except the caller's
        foreach (var key in this.sessions.Keys)
        {
            if (key != callerSessionToken)
            {
                this.sessions.TryRemove(key, out _);
            }
        }

        this.LogPasswordChanged();
        return true;
    }

    /// <summary>
    /// Returns the number of active sessions (useful for diagnostics).
    /// </summary>
    public int ActiveSessionCount => this.sessions.Count;

    private static string GenerateSessionToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    // ── LoggerMessage ────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Initial password set for Bridge web UI")]
    private partial void LogPasswordSetup();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed login attempt for Bridge web UI")]
    private partial void LogLoginFailed();

    [LoggerMessage(Level = LogLevel.Information, Message = "Successful login to Bridge web UI")]
    private partial void LogLoginSucceeded();

    [LoggerMessage(Level = LogLevel.Information, Message = "Session logged out from Bridge web UI")]
    private partial void LogLogout();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed password change attempt — incorrect current password")]
    private partial void LogChangePasswordFailed();

    [LoggerMessage(Level = LogLevel.Information, Message = "Password changed for Bridge web UI, other sessions invalidated")]
    private partial void LogPasswordChanged();

    internal sealed record SessionInfo(DateTimeOffset CreatedAt);
}
