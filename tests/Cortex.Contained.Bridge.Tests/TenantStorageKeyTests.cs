using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Common.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

public sealed class TenantStorageKeyTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "cortex-storagekey-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void GetOrCreateTenantStorageKey_FirstCall_Creates256BitKeyAndReturnsItAgain()
    {
        var manager = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir);

        var first = manager.GetOrCreateTenantStorageKey("default");
        var second = manager.GetOrCreateTenantStorageKey("default");

        Assert.Equal(32, Convert.FromBase64String(first).Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, manager.GetOrCreateTenantStorageKey("other"));
    }

    [Fact]
    public void GetOrCreateTenantStorageKey_Undecryptable_ThrowsAndNeverRegenerates()
    {
        var store = new BreakableSecretStore();
        var manager = new SecretManager(store, NullLogger<SecretManager>.Instance, this.tempDir);
        manager.GetOrCreateTenantStorageKey("default");
        var onDisk = File.ReadAllText(Path.Combine(this.tempDir, "secrets.json"));
        store.Broken = true;

        Assert.Throws<StorageKeyUnavailableException>(() => manager.GetOrCreateTenantStorageKey("default"));
        Assert.Equal(onDisk, File.ReadAllText(Path.Combine(this.tempDir, "secrets.json")));
    }

    private sealed class BreakableSecretStore : ISecretStore
    {
        public bool Broken { get; set; }

        public string Protect(string plaintext) => "p:" + plaintext;

        public string Unprotect(string ciphertext) =>
            this.Broken ? throw new System.Security.Cryptography.CryptographicException("profile changed") : ciphertext[2..];
    }
}
