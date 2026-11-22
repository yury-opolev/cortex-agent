using System.Security.Cryptography;
using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

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
}
