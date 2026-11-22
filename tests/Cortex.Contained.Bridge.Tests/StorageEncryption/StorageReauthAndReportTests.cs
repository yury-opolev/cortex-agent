using System.Security.Cryptography;
using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.RateLimiting;

namespace Cortex.Contained.Bridge.Tests.StorageEncryption;

public sealed class StorageReauthAndReportTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cortex-reauth-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void VerifyPassword_OnlyTheOwnerPasswordPasses()
    {
        var sessions = new SessionManager(
            new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.dir), NullLogger<SessionManager>.Instance);
        Assert.False(sessions.VerifyPassword("anything"));
        sessions.SetupPassword("owner-password-1");

        Assert.True(sessions.VerifyPassword("owner-password-1"));
        Assert.False(sessions.VerifyPassword("owner-password-2"));
        Assert.False(sessions.VerifyPassword(null));
    }

    [Fact]
    public void Scan_ClassifiesTheBridgeStores()
    {
        Directory.CreateDirectory(Path.Combine(this.dir, "mcp"));
        File.WriteAllBytes(Path.Combine(this.dir, "mcp", "actions.db"), RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(Path.Combine(this.dir, "messages.db"), [.. "SQLite format 3\0"u8.ToArray(), .. new byte[100]]);

        var stores = BridgeStorageReport.Scan(this.dir).ToDictionary(s => s.Id, s => s.State);

        Assert.Equal(StorageItemStates.Encrypted, stores["mcp-actions"]);
        Assert.Equal(StorageItemStates.Plaintext, stores["legacy-messages"]);
        Assert.Equal(StorageItemStates.Absent, BridgeStorageReport.Scan(Path.Combine(this.dir, "none"))[0].State);
    }

    private SessionManager Owner()
    {
        var sessions = new SessionManager(
            new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.dir), NullLogger<SessionManager>.Instance);
        sessions.SetupPassword("owner-password-1");
        return sessions;
    }

    [Fact]
    public void VerifyPassword_RepeatedFailuresInASession_InvalidateOnlyThatSession()
    {
        var sessions = this.Owner();
        var attacked = sessions.Login("owner-password-1")!;
        var other = sessions.Login("owner-password-1")!;

        for (var i = 0; i < SessionManager.MaxReauthFailures - 1; i++)
        {
            Assert.False(sessions.VerifyPassword("guess", attacked));
            Assert.True(sessions.ValidateSession(attacked));
        }

        Assert.False(sessions.VerifyPassword("guess", attacked));

        Assert.False(sessions.ValidateSession(attacked));
        Assert.True(sessions.ValidateSession(other));
    }

    [Fact]
    public void VerifyPassword_ASuccessResetsTheFailureCount()
    {
        var sessions = this.Owner();
        var token = sessions.Login("owner-password-1")!;

        for (var round = 0; round < 3; round++)
        {
            Assert.False(sessions.VerifyPassword("guess", token));
            Assert.False(sessions.VerifyPassword("guess", token));
            Assert.True(sessions.VerifyPassword("owner-password-1", token));
        }

        Assert.True(sessions.ValidateSession(token));
    }

    [Fact]
    public void VerifyPassword_WithoutASessionToken_NeverCountsOrThrows()
    {
        var sessions = this.Owner();

        for (var i = 0; i < 5; i++)
        {
            Assert.False(sessions.VerifyPassword("guess", null));
            Assert.False(sessions.VerifyPassword("guess", "unknown-token"));
        }
    }

    private static DefaultHttpContext Request(string? sessionCookie, string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
        if (sessionCookie is not null)
        {
            context.Request.Headers.Cookie = $"cortex_session={sessionCookie}";
        }

        return context;
    }

    [Fact]
    public void ReauthRateLimit_FiveAttemptsPerSessionAndAddressPerWindow()
    {
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(ReauthRateLimit.Partition);
        var session = Request("token-a", "10.0.0.1");

        for (var i = 0; i < ReauthRateLimit.PermitLimit; i++)
        {
            using var lease = limiter.AttemptAcquire(session);
            Assert.True(lease.IsAcquired);
        }

        using var denied = limiter.AttemptAcquire(session);
        using var otherSession = limiter.AttemptAcquire(Request("token-b", "10.0.0.1"));
        using var otherAddress = limiter.AttemptAcquire(Request("token-a", "10.0.0.2"));
        Assert.False(denied.IsAcquired);
        Assert.True(otherSession.IsAcquired);
        Assert.True(otherAddress.IsAcquired);
    }
}
