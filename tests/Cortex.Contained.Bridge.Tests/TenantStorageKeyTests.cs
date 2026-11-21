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

    [Fact]
    public async Task GetOrCreateTenantStorageKey_Concurrent_AllCallersGetThePersistedKey()
    {
        var store = new InMemorySecretStore();
        using var gate = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            return new SecretManager(store, NullLogger<SecretManager>.Instance, this.tempDir).GetOrCreateTenantStorageKey("default");
        })).ToArray();
        gate.Set();

        var keys = await Task.WhenAll(tasks);

        Assert.Single(keys.Distinct());
        Assert.Equal(keys[0], new SecretManager(store, NullLogger<SecretManager>.Instance, this.tempDir).GetOrCreateTenantStorageKey("default"));
    }

    [Fact]
    public async Task StoreOAuthTokens_ConcurrentWithKeyCreation_KeepsBoth()
    {
        var store = new InMemorySecretStore();
        for (var round = 0; round < 20; round++)
        {
            var dir = Path.Combine(this.tempDir, $"round{round}");
            using var gate = new ManualResetEventSlim();
            var keyTask = Task.Run(() =>
            {
                gate.Wait();
                return new SecretManager(store, NullLogger<SecretManager>.Instance, dir).GetOrCreateTenantStorageKey("default");
            });
            var oauthTasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                gate.Wait();
                new SecretManager(store, NullLogger<SecretManager>.Instance, dir).StoreOAuthTokens($"p{i}", $"access{i}", $"refresh{i}", 100 + i);
            })).ToArray();
            gate.Set();

            var key = await keyTask;
            await Task.WhenAll(oauthTasks);

            var reader = new SecretManager(store, NullLogger<SecretManager>.Instance, dir);
            Assert.Equal(key, reader.GetOrCreateTenantStorageKey("default"));
            for (var i = 0; i < 8; i++)
            {
                Assert.Equal($"access{i}", reader.GetApiKey($"p{i}"));
            }
        }
    }

    [Fact]
    public void SaveSecrets_UnknownFieldsFromAnotherVersion_SurviveARoundTrip()
    {
        Directory.CreateDirectory(this.tempDir);
        var path = Path.Combine(this.tempDir, "secrets.json");
        File.WriteAllText(path, """{"HubToken":"p:abc","FutureSecret":"p:xyz","FutureMap":{"a":1}}""");
        var manager = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir);

        manager.GetOrCreateTenantStorageKey("default");

        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("p:xyz", saved.RootElement.GetProperty("FutureSecret").GetString());
        Assert.Equal(1, saved.RootElement.GetProperty("FutureMap").GetProperty("a").GetInt32());
        Assert.True(saved.RootElement.TryGetProperty("TenantStorageKeys", out _));
    }

    private sealed class BreakableSecretStore : ISecretStore
    {
        public bool Broken { get; set; }

        public string Protect(string plaintext) => "p:" + plaintext;

        public string Unprotect(string ciphertext) =>
            this.Broken ? throw new System.Security.Cryptography.CryptographicException("profile changed") : ciphertext[2..];
    }
}
