using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

public sealed class StorageFileCipherTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Encrypt_RoundTripsWithAFreshNonceAndNoPlaintext()
    {
        var cipher = new StorageFileCipher(Key, "memory-content");
        var plaintext = Encoding.UTF8.GetBytes("zqfilemarker secret");

        var a = cipher.Encrypt(plaintext);
        var b = cipher.Encrypt(plaintext);

        Assert.True(StorageFileCipher.IsEncrypted(a));
        Assert.NotEqual(a, b);
        Assert.Equal(-1, a.AsSpan().IndexOf("zqfilemarker"u8));
        Assert.Equal(plaintext, cipher.Decrypt(a));
    }

    [Fact]
    public void Decrypt_WrongKeyOrLabelOrTamperedByte_FailsClosed()
    {
        var blob = new StorageFileCipher(Key, "memory-content").Encrypt("x"u8.ToArray());
        var tampered = (byte[])blob.Clone();
        tampered[^1] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => new StorageFileCipher(OtherKey, "memory-content").Decrypt(blob));
        Assert.ThrowsAny<CryptographicException>(() => new StorageFileCipher(Key, "sessions-snapshot").Decrypt(blob));
        Assert.ThrowsAny<CryptographicException>(() => new StorageFileCipher(Key, "memory-content").Decrypt(tampered));
    }

    [Fact]
    public void Decrypt_Plaintext_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => new StorageFileCipher(Key, "memory-content").Decrypt("plain text"u8.ToArray()));
    }
}
