using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

public sealed class PackageCipherTests
{
    private const string Passphrase = "correct horse battery staple";
    private const int Chunk = 64;

    private static byte[] Seal(byte[] plaintext)
    {
        using var output = new MemoryStream();
        using (var writer = PackageCipher.CreateWriter(output, Passphrase, PassphraseKdf.MinimumIterations, Chunk))
        {
            writer.Write(plaintext);
        }

        return output.ToArray();
    }

    private static byte[] Open(byte[] package, string passphrase = Passphrase)
    {
        using var reader = PackageCipher.OpenReader(new MemoryStream(package), passphrase);
        using var plaintext = new MemoryStream();
        reader.CopyTo(plaintext);
        return plaintext.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData((3 * Chunk) + 5)]
    public void RoundTrip_AnyLength_ReturnsThePlaintext(int length)
    {
        var plaintext = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

        Assert.Equal(plaintext, Open(Seal(plaintext)));
    }

    [Fact]
    public void Open_WrongPassphrase_Fails()
    {
        Assert.Throws<InvalidDataException>(() => Open(Seal([1, 2, 3]), "another passphrase"));
    }

    [Theory]
    [InlineData("truncate-chunk")]
    [InlineData("drop-last-chunk")]
    [InlineData("append")]
    [InlineData("flip-header")]
    [InlineData("flip-body")]
    public void Open_TamperedPackage_Fails(string tamper)
    {
        var package = Seal(Enumerable.Range(0, 3 * Chunk).Select(i => (byte)i).ToArray());
        var sealedChunk = Chunk + 16;
        byte[] damaged = tamper switch
        {
            "truncate-chunk" => package[..^5],
            "drop-last-chunk" => package[..^16],
            "append" => [.. package, 0],
            "flip-header" => Flip(package, 20),
            _ => Flip(package, package.Length - sealedChunk - 3),
        };

        Assert.ThrowsAny<Exception>(() => Open(damaged));
    }

    [Fact]
    public void Open_ChunksReordered_Fails()
    {
        var package = Seal(Enumerable.Range(0, 3 * Chunk).Select(i => (byte)i).ToArray());
        var sealedChunk = Chunk + 16;
        var first = package.Length - (3 * sealedChunk);
        var swapped = (byte[])package.Clone();
        package.AsSpan(first + sealedChunk, sealedChunk).CopyTo(swapped.AsSpan(first));
        package.AsSpan(first, sealedChunk).CopyTo(swapped.AsSpan(first + sealedChunk));

        Assert.ThrowsAny<Exception>(() => Open(swapped));
    }

    [Fact]
    public void CreateWriter_ShortPassphrase_Refused()
    {
        Assert.Throws<ArgumentException>(() => PackageCipher.CreateWriter(new MemoryStream(), "short"));
    }

    private static byte[] Flip(byte[] bytes, int index)
    {
        var copy = (byte[])bytes.Clone();
        copy[index] ^= 0x01;
        return copy;
    }
}
