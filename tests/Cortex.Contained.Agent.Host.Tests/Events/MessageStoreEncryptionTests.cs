using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>Encryption at rest for messages.db (D4): key from the Bridge, migration, fail closed.</summary>
[Trait("Category", "Integrity")]
public sealed class MessageStoreEncryptionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"msgstore-enc-{Guid.NewGuid():N}");

    private string DbPath => Path.Combine(this.dir, "messages.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.dir, recursive: true); } catch { /* best effort */ }
    }

    private static StorageKeyRing Ring(byte seed = 1)
    {
        var ring = new StorageKeyRing();
        ring.Provide(Convert.ToBase64String(Enumerable.Range(seed, 32).Select(i => (byte)i).ToArray()));
        return ring;
    }

    private MessageStore Encrypted(StorageKeyRing ring, TimeSpan? wait = null) =>
        new(this.DbPath, NullLogger<MessageStore>.Instance, new MessageStoreEncryption(ring, wait ?? TimeSpan.FromSeconds(5)));

    private bool AnyFileContains(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        foreach (var file in Directory.EnumerateFiles(this.dir))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (copy.ToArray().AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task EncryptedStore_ContentNeverOnDiskInPlaintext()
    {
        await using (var store = this.Encrypted(Ring()))
        {
            await store.SaveMessageAsync("u", "ch", "user", "zqencmarker secret", T0);
            Assert.Equal("zqencmarker secret", Assert.Single(await store.GetMessagesAsync("ch")).Content);
            Assert.False(this.AnyFileContains("zqencmarker"));
        }

        await using var plain = new SqliteConnection($"Data Source={this.DbPath};Pooling=False");
        plain.Open();
        await using var cmd = plain.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Messages;";
        Assert.Equal(26, Assert.Throws<SqliteException>(() => cmd.ExecuteScalar()).SqliteErrorCode);
    }

    [Fact]
    public async Task PlaintextDatabase_IsMigratedOnFirstOpenAndPlaintextErased()
    {
        await using (var plain = new MessageStore(this.DbPath, NullLogger<MessageStore>.Instance))
        {
            await plain.SaveMessageAsync("u", "ch", "user", "zqoldmarker", T0);
            await plain.SaveMessageAsync("u", "ch", "user", "zqdeletedmarker", T0);
            await plain.DeleteChannelMessagesOlderThanAsync("ch", T0.AddDays(1));
            await plain.SaveMessageAsync("u", "ch", "user", "zqoldmarker kept", T0.AddDays(2));
        }

        SqliteConnection.ClearAllPools();
        Assert.True(this.AnyFileContains("zqoldmarker"));

        await using var store = this.Encrypted(Ring());
        Assert.Equal("zqoldmarker kept", Assert.Single(await store.GetMessagesAsync("ch")).Content);
        Assert.Equal(StorageState.Open, store.State);
        Assert.False(this.AnyFileContains("zqoldmarker"));
        Assert.False(this.AnyFileContains("zqdeletedmarker"));
    }

    [Fact]
    public async Task NoKey_RequestsFailLockedAndNoFileIsCreated()
    {
        await using var store = this.Encrypted(new StorageKeyRing(), TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<StorageLockedException>(() => store.GetMessagesAsync("ch"));

        Assert.Equal(StorageState.AwaitingKey, store.State);
        Assert.False(File.Exists(this.DbPath));
    }

    [Fact]
    public async Task WrongKey_FailsClosedAndNeverRewritesTheFile()
    {
        await using (var store = this.Encrypted(Ring(seed: 1)))
        {
            await store.SaveMessageAsync("u", "ch", "user", "x", T0);
        }

        SqliteConnection.ClearAllPools();
        var before = SHA256.HashData(await File.ReadAllBytesAsync(this.DbPath));

        await using var wrong = this.Encrypted(Ring(seed: 2));
        await Assert.ThrowsAsync<StorageKeyMismatchException>(() => wrong.GetMessagesAsync("ch"));
        await Assert.ThrowsAsync<StorageLockedException>(() => wrong.GetMessagesAsync("ch"));

        Assert.Equal(StorageState.Failed, wrong.State);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(this.DbPath)));
    }

    [Fact]
    public async Task MigrationInterruptedBeforeRekey_RetriedOnNextOpen()
    {
        await using (var plain = new MessageStore(this.DbPath, NullLogger<MessageStore>.Instance))
        {
            await plain.SaveMessageAsync("u", "ch", "user", "zqretry", T0);
        }

        SqliteConnection.ClearAllPools();
        await using (var crashing = this.Encrypted(Ring()))
        {
            crashing.FaultInjector = stage => { if (stage == "before-rekey") { throw new IOException("power loss"); } };
            await Assert.ThrowsAsync<IOException>(() => crashing.GetMessagesAsync("ch"));
        }

        SqliteConnection.ClearAllPools();
        await using var store = this.Encrypted(Ring());
        Assert.Equal("zqretry", Assert.Single(await store.GetMessagesAsync("ch")).Content);
        Assert.False(this.AnyFileContains("zqretry"));
    }
}
