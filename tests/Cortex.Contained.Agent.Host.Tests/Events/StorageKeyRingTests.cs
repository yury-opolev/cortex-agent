using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Events;

public sealed class StorageKeyRingTests
{
    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(2, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void Provide_FirstValidKeyWins_SameKeyUnchanged_DifferentKeyRejected()
    {
        var ring = new StorageKeyRing();

        Assert.Equal(StorageKeyAcceptance.Accepted, ring.Provide(KeyA));
        Assert.Equal(StorageKeyAcceptance.Unchanged, ring.Provide(KeyA));
        Assert.Equal(StorageKeyAcceptance.Rejected, ring.Provide(KeyB));
        Assert.Equal(ring.PasswordFor("messages.db"), StorageKeyRing.DerivePassword(Convert.FromBase64String(KeyA), "messages.db"));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void Provide_MalformedKey_Rejected(string key)
    {
        var ring = new StorageKeyRing();

        Assert.Equal(StorageKeyAcceptance.Rejected, ring.Provide(key));
        Assert.False(ring.IsAvailable);
    }

    [Fact]
    public async Task WaitForPasswordAsync_NoKey_TimesOut()
    {
        var ring = new StorageKeyRing();

        await Assert.ThrowsAsync<TimeoutException>(() => ring.WaitForPasswordAsync("messages.db", TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.Throws<StorageLockedException>(() => ring.PasswordFor("messages.db"));
    }

    [Fact]
    public void DerivePassword_IsPurposeBoundHex()
    {
        var key = Convert.FromBase64String(KeyA);

        var messages = StorageKeyRing.DerivePassword(key, "messages.db");

        Assert.Equal(64, messages.Length);
        Assert.Matches("^[0-9a-f]+$", messages);
        Assert.NotEqual(messages, StorageKeyRing.DerivePassword(key, "history-index"));
    }
}
