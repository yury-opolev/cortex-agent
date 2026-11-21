namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Whether an encrypted store can serve requests.</summary>
public enum StorageState
{
    /// <summary>Waiting for the Bridge to provide the storage key (nothing is served).</summary>
    AwaitingKey,

    /// <summary>Open and serving.</summary>
    Open,

    /// <summary>The provided key does not open the existing database; it stays locked (fail closed).</summary>
    Failed,
}

/// <summary>Readiness of the encrypted message store, for health and background services.</summary>
public interface IStorageState
{
    /// <summary>The current state.</summary>
    StorageState State { get; }

    /// <summary>Completes once the store is open (waits for the key without a time limit).</summary>
    Task WhenOpenAsync(CancellationToken cancellationToken);
}

/// <summary>Encryption at rest for <c>messages.db</c> (D4).</summary>
/// <param name="Keys">The key ring the Bridge fills.</param>
/// <param name="KeyWait">How long a request waits for the key before failing with <see cref="StorageLockedException"/>.</param>
public sealed record MessageStoreEncryption(StorageKeyRing Keys, TimeSpan KeyWait);
