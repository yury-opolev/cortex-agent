using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>
/// The passphrase encryption of an evaluation package (<c>.cxpkg</c>): <see cref="PassphraseKdf"/> (PBKDF2-HMAC-SHA256,
/// 600,000 iterations, the recovery kit's derivation) into an AES-256-GCM key, then a chunked AEAD stream (the STREAM
/// construction): the plaintext is cut into chunks of <see cref="DefaultChunkSize"/>, each sealed with the nonce
/// <c>prefix(7) ‖ counter(4, big-endian) ‖ last(1)</c> and the associated data <c>SHA-256(header) ‖ counter ‖ last</c>.
/// Reordered, dropped, truncated or appended chunks and any modified byte (header included) fail authentication; a
/// wrong passphrase fails on the first chunk. Layout: <c>CXPKG1\n</c>, header length (4 bytes, big-endian), header
/// JSON, chunks.
/// </summary>
public static class PackageCipher
{
    /// <summary>Plaintext bytes per chunk.</summary>
    public const int DefaultChunkSize = 1 << 20;

    /// <summary>Format identifier in the header.</summary>
    public const string Format = "cortex-eval-package/1";

    internal const int TagBytes = 16;
    internal const int PrefixBytes = 7;
    private const int MaxHeaderBytes = 4096;

    private static readonly byte[] Magic = "CXPKG1\n"u8.ToArray();

    /// <summary>Starts an encrypted package on <paramref name="inner"/>; dispose the result to write the final chunk.</summary>
    public static Stream CreateWriter(Stream inner, string passphrase, int iterations = PassphraseKdf.DefaultIterations, int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(passphrase);
        if (passphrase.Length < PassphraseKdf.MinimumPassphraseLength)
        {
            throw new ArgumentException($"The passphrase needs at least {PassphraseKdf.MinimumPassphraseLength} characters.", nameof(passphrase));
        }

        var header = new Header
        {
            Format = Format,
            Kdf = PassphraseKdf.Name,
            Iterations = iterations,
            Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(PassphraseKdf.SaltBytes)),
            Cipher = "AES-256-GCM-STREAM",
            ChunkSize = chunkSize,
            NoncePrefix = Convert.ToBase64String(RandomNumberGenerator.GetBytes(PrefixBytes)),
        };
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, headerBytes.Length);
        inner.Write(Magic);
        inner.Write(length);
        inner.Write(headerBytes);
        var key = PassphraseKdf.DeriveKey(passphrase, Convert.FromBase64String(header.Salt), iterations);
        return new PackageWriteStream(inner, key, Convert.FromBase64String(header.NoncePrefix), HeaderHash(length, headerBytes), chunkSize);
    }

    /// <summary>Opens an encrypted package; throws <see cref="InvalidDataException"/> for a file that is not a package.</summary>
    public static Stream OpenReader(Stream inner, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(passphrase);
        Span<byte> magic = stackalloc byte[7];
        Span<byte> length = stackalloc byte[4];
        if (inner.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) != magic.Length || !magic.SequenceEqual(Magic)
            || inner.ReadAtLeast(length, 4, throwOnEndOfStream: false) != 4)
        {
            throw new InvalidDataException("The file is not an evaluation package.");
        }

        var headerLength = BinaryPrimitives.ReadInt32BigEndian(length);
        if (headerLength is <= 0 or > MaxHeaderBytes)
        {
            throw new InvalidDataException("The evaluation package header is damaged.");
        }

        var headerBytes = new byte[headerLength];
        inner.ReadExactly(headerBytes);
        Header? header;
        try
        {
            header = JsonSerializer.Deserialize<Header>(headerBytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The evaluation package header is damaged.", ex);
        }

        if (header is not { Format: Format, Kdf: PassphraseKdf.Name, Cipher: "AES-256-GCM-STREAM" }
            || header.Iterations is < PassphraseKdf.MinimumIterations or > PassphraseKdf.MaximumIterations
            || header.ChunkSize is < 16 or > 16 << 20)
        {
            throw new InvalidDataException("The file is not a supported evaluation package.");
        }

        byte[] salt, prefix;
        try
        {
            salt = Convert.FromBase64String(header.Salt);
            prefix = Convert.FromBase64String(header.NoncePrefix);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The evaluation package header is damaged.", ex);
        }

        if (prefix.Length != PrefixBytes)
        {
            throw new InvalidDataException("The evaluation package header is damaged.");
        }

        var key = PassphraseKdf.DeriveKey(passphrase, salt, header.Iterations);
        return new PackageReadStream(inner, key, prefix, HeaderHash(length.ToArray(), headerBytes), header.ChunkSize);
    }

    internal static byte[] Nonce(byte[] prefix, uint counter, bool last)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(PrefixBytes), counter);
        nonce[11] = last ? (byte)1 : (byte)0;
        return nonce;
    }

    // Chunk index and the final-chunk flag are authenticated twice: in the nonce and in the associated data.
    internal static byte[] ChunkAssociatedData(byte[] headerHash, uint counter, bool last)
    {
        var data = new byte[headerHash.Length + 5];
        headerHash.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(headerHash.Length), counter);
        data[^1] = last ? (byte)1 : (byte)0;
        return data;
    }

    private static byte[] HeaderHash(byte[] length, byte[] header) => SHA256.HashData([.. Magic, .. length, .. header]);

    private sealed record Header
    {
        public string Format { get; init; } = string.Empty;

        public string Kdf { get; init; } = string.Empty;

        public int Iterations { get; init; }

        public string Salt { get; init; } = string.Empty;

        public string Cipher { get; init; } = string.Empty;

        public int ChunkSize { get; init; }

        public string NoncePrefix { get; init; } = string.Empty;
    }

    private sealed class PackageWriteStream(Stream inner, byte[] key, byte[] prefix, byte[] headerHash, int chunkSize) : Stream
    {
        private readonly AesGcm aes = new(key, TagBytes);
        private readonly byte[] buffer = new byte[chunkSize];
        private int filled;
        private uint counter;
        private bool finished;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !this.finished;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(this.finished, this);
            while (data.Length > 0)
            {
                if (this.filled == this.buffer.Length)
                {
                    // Only now is it known that this full chunk is not the last one.
                    this.Seal(last: false);
                }

                var take = Math.Min(data.Length, this.buffer.Length - this.filled);
                data[..take].CopyTo(this.buffer.AsSpan(this.filled));
                this.filled += take;
                data = data[take..];
            }
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !this.finished)
            {
                this.Seal(last: true);
                this.finished = true;
                inner.Flush();
                this.aes.Dispose();
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(this.buffer);
            }

            base.Dispose(disposing);
        }

        private void Seal(bool last)
        {
            if (this.counter == uint.MaxValue)
            {
                throw new InvalidOperationException("The evaluation package is too large.");
            }

            var output = new byte[this.filled + TagBytes];
            var index = this.counter++;
            this.aes.Encrypt(Nonce(prefix, index, last), this.buffer.AsSpan(0, this.filled), output.AsSpan(0, this.filled), output.AsSpan(this.filled), ChunkAssociatedData(headerHash, index, last));
            inner.Write(output);
            this.filled = 0;
        }
    }

    private sealed class PackageReadStream(Stream inner, byte[] key, byte[] prefix, byte[] headerHash, int chunkSize) : Stream
    {
        private readonly AesGcm aes = new(key, TagBytes);
        private readonly byte[] sealedChunk = new byte[chunkSize + TagBytes];
        private byte[] plain = [];
        private int offset;
        private uint counter;
        private bool lastSeen;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            while (this.offset == this.plain.Length)
            {
                if (this.lastSeen)
                {
                    return 0;
                }

                this.Open();
            }

            var take = Math.Min(destination.Length, this.plain.Length - this.offset);
            this.plain.AsSpan(this.offset, take).CopyTo(destination);
            this.offset += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.aes.Dispose();
                CryptographicOperations.ZeroMemory(key);
            }

            base.Dispose(disposing);
        }

        private void Open()
        {
            var read = inner.ReadAtLeast(this.sealedChunk, this.sealedChunk.Length, throwOnEndOfStream: false);
            if (read < TagBytes)
            {
                throw new InvalidDataException("The evaluation package is truncated.");
            }

            var length = read - TagBytes;
            var plaintext = new byte[length];
            var sealedBytes = this.sealedChunk.AsSpan(0, length);
            var tag = this.sealedChunk.AsSpan(length, TagBytes);

            // A short chunk can only be the last; a full one is the last only when the "last" nonce authenticates it.
            var candidates = read < this.sealedChunk.Length ? new[] { true } : [false, true];
            foreach (var last in candidates)
            {
                try
                {
                    this.aes.Decrypt(Nonce(prefix, this.counter, last), sealedBytes, tag, plaintext, ChunkAssociatedData(headerHash, this.counter, last));
                    this.counter++;
                    this.lastSeen = last;
                    this.plain = plaintext;
                    this.offset = 0;
                    if (last && inner.ReadByte() >= 0)
                    {
                        throw new InvalidDataException("The evaluation package has data after its last chunk.");
                    }

                    return;
                }
                catch (AuthenticationTagMismatchException)
                {
                    // try the other position, then fail
                }
            }

            throw new InvalidDataException("Wrong passphrase, or the evaluation package was modified or truncated.");
        }
    }
}
