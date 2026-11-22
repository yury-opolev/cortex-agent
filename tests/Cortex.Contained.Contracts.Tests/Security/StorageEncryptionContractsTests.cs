using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Contracts.Tests.Security;

public sealed class StorageEncryptionContractsTests : IDisposable
{
    private static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"sqlite-header-{Guid.NewGuid():N}");

    public StorageEncryptionContractsTests()
    {
        Directory.CreateDirectory(this.dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Fingerprint_IsStableHexAndKeySpecific()
    {
        var a = StorageKeyFingerprint.Compute(KeyA);

        Assert.Equal(a, StorageKeyFingerprint.Compute(Convert.ToBase64String(KeyA)));
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.NotEqual(a, StorageKeyFingerprint.Compute(KeyB));
        Assert.DoesNotContain(Convert.ToHexStringLower(KeyA)[..16], a, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_DiffersFromEveryStoragePurposeDerivation()
    {
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, KeyA, 32, info: Encoding.UTF8.GetBytes("cortex-storage/messages.db"));

        Assert.False(Convert.ToHexStringLower(derived).StartsWith(StorageKeyFingerprint.Compute(KeyA), StringComparison.Ordinal));
    }

    [Fact]
    public void Fingerprint_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => StorageKeyFingerprint.Compute(new byte[16]));
    }

    [Fact]
    public void Display_GroupsOfFour()
    {
        Assert.Equal("0123-4567-89ab-cdef", StorageKeyFingerprint.Display("0123456789abcdef"));
    }

    [Fact]
    public void Classify_ByHeader()
    {
        var plain = Path.Combine(this.dir, "plain.db");
        var encrypted = Path.Combine(this.dir, "enc.db");
        var empty = Path.Combine(this.dir, "empty.db");
        File.WriteAllBytes(plain, [.. "SQLite format 3\0"u8.ToArray(), .. new byte[100]]);
        File.WriteAllBytes(encrypted, RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(empty, []);

        Assert.Equal(StoredFileState.Plaintext, SqliteFileHeader.Classify(plain));
        Assert.Equal(StoredFileState.Encrypted, SqliteFileHeader.Classify(encrypted));
        Assert.Equal(StoredFileState.Empty, SqliteFileHeader.Classify(empty));
        Assert.Equal(StoredFileState.Absent, SqliteFileHeader.Classify(Path.Combine(this.dir, "none.db")));
    }
}
