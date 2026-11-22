using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Outcome of <see cref="StorageKeyRing.Provide"/>.</summary>
public enum StorageKeyAcceptance
{
    /// <summary>First key of this process; storage can open.</summary>
    Accepted,

    /// <summary>The same key again (reconnect, credential refresh).</summary>
    Unchanged,

    /// <summary>Malformed, or different from the key already in use (never switched at runtime).</summary>
    Rejected,
}

/// <summary>
/// Holds the storage master key pushed by the Bridge (D4) in memory and derives one SQLite3
/// Multiple Ciphers passphrase per database (HKDF-SHA256, purpose-bound). Fail-closed: until the
/// key arrives nothing can open an encrypted database, and a different key is never accepted.
/// </summary>
public sealed class StorageKeyRing
{
    /// <summary>Required master key length in bytes.</summary>
    public const int KeyBytes = 32;

    private readonly TaskCompletionSource<byte[]> master = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock sync = new();
    private string? expectedFingerprint;

    /// <summary>True once a key has been accepted.</summary>
    public bool IsAvailable => this.master.Task.IsCompletedSuccessfully;

    /// <summary>True after a key was refused because its fingerprint is not the one the encrypted data belongs to.</summary>
    public bool RejectedForData { get; private set; }

    /// <summary>Fingerprint of the accepted key (never the key itself), or null before one arrived.</summary>
    public string? Fingerprint => this.IsAvailable ? StorageKeyFingerprint.Compute(this.master.Task.Result) : null;

    /// <summary>
    /// The fingerprint recorded when encryption was enabled (the marker). A key with another fingerprint
    /// is rejected before anything tries to open the data with it.
    /// </summary>
    public string? ExpectedFingerprint
    {
        get => this.expectedFingerprint;
        set => this.expectedFingerprint = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Offers the Base64 master key; the first valid key wins for the process lifetime.</summary>
    public StorageKeyAcceptance Provide(string base64Key)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException)
        {
            return StorageKeyAcceptance.Rejected;
        }

        if (key.Length != KeyBytes)
        {
            return StorageKeyAcceptance.Rejected;
        }

        if (this.expectedFingerprint is { } expected
            && !string.Equals(StorageKeyFingerprint.Compute(key), expected, StringComparison.Ordinal))
        {
            this.RejectedForData = true;
            return StorageKeyAcceptance.Rejected;
        }

        lock (this.sync)
        {
            if (this.master.TrySetResult(key))
            {
                return StorageKeyAcceptance.Accepted;
            }

            return CryptographicOperations.FixedTimeEquals(this.master.Task.Result, key)
                ? StorageKeyAcceptance.Unchanged
                : StorageKeyAcceptance.Rejected;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the key; throws <see cref="TimeoutException"/> otherwise.</summary>
    public async Task<string> WaitForPasswordAsync(string purpose, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var key = await this.master.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return DerivePassword(key, purpose);
    }

    /// <summary>Waits for the key without a time limit (background services).</summary>
    public Task WhenAvailableAsync(CancellationToken cancellationToken) => this.master.Task.WaitAsync(cancellationToken);

    /// <summary>The passphrase for <paramref name="purpose"/>; throws <see cref="StorageLockedException"/> before the key arrived.</summary>
    public string PasswordFor(string purpose) =>
        this.IsAvailable
            ? DerivePassword(this.master.Task.Result, purpose)
            : throw new StorageLockedException($"The storage key for {purpose} has not been provided by the Bridge yet.");

    /// <summary>The raw 256-bit key for <paramref name="purpose"/>; throws <see cref="StorageLockedException"/> before the key arrived.</summary>
    public byte[] KeyFor(string purpose) =>
        this.IsAvailable
            ? DeriveKey(this.master.Task.Result, purpose)
            : throw new StorageLockedException($"The storage key for {purpose} has not been provided by the Bridge yet.");

    /// <summary>Waits up to <paramref name="timeout"/> for the key and returns the raw key for <paramref name="purpose"/>.</summary>
    public async Task<byte[]> WaitForKeyAsync(string purpose, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var key = await this.master.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return DeriveKey(key, purpose);
    }

    /// <summary>Deterministic, purpose-bound passphrase (64 hex characters).</summary>
    public static string DerivePassword(byte[] masterKey, string purpose)
    {
        var derived = DeriveKey(masterKey, purpose);
        try
        {
            return Convert.ToHexStringLower(derived);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <summary>Deterministic, purpose-bound 256-bit key: HKDF-SHA256(master, info <c>cortex-storage/{purpose}</c>).</summary>
    public static byte[] DeriveKey(byte[] masterKey, string purpose)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, KeyBytes, info: Encoding.UTF8.GetBytes("cortex-storage/" + purpose));
    }
}

/// <summary>Encrypted storage cannot open: the Bridge has not provided the storage key.</summary>
public sealed class StorageLockedException : Exception
{
    public StorageLockedException()
    {
    }

    public StorageLockedException(string message)
        : base(message)
    {
    }

    public StorageLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
