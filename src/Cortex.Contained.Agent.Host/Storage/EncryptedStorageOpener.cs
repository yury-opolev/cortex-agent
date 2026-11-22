using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Opens the encrypted stores as soon as the Bridge pushes the storage key, so the one-time migration of a
/// plaintext store runs right after the restart that enabled encryption instead of on the first request.
/// Records the key fingerprint in the marker when an encrypted agent has none yet (configured or detected).
/// </summary>
internal sealed partial class EncryptedStorageOpener : BackgroundService
{
    private readonly StorageEncryptionContext context;
    private readonly StorageKeyRing keys;
    private readonly IStorageState messages;
    private readonly EncryptedMemoryStore memory;
    private readonly TimeProvider time;
    private readonly ILogger<EncryptedStorageOpener> logger;

    public EncryptedStorageOpener(
        StorageEncryptionContext context,
        StorageKeyRing keys,
        IStorageState messages,
        EncryptedMemoryStore memory,
        TimeProvider time,
        ILogger<EncryptedStorageOpener> logger)
    {
        this.context = context;
        this.keys = keys;
        this.messages = messages;
        this.memory = memory;
        this.time = time;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await this.keys.WhenAvailableAsync(stoppingToken).ConfigureAwait(false);
            await this.messages.WhenOpenAsync(stoppingToken).ConfigureAwait(false);
            await this.memory.WhenOpenAsync(stoppingToken).ConfigureAwait(false);
            if (this.context.EnsureMarker(this.keys.Fingerprint!, this.time.GetUtcNow()))
            {
                this.LogMarkerWritten();
            }

            this.LogStoresOpen();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is StorageKeyMismatchException or StorageLockedException or InvalidDataException or IOException or SqliteException)
        {
            this.LogOpenFailed(ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Encrypted stores are open (messages, history, memory)")]
    private partial void LogStoresOpen();

    [LoggerMessage(Level = LogLevel.Information, Message = "Encryption marker written with the fingerprint of the key that opened the stores")]
    private partial void LogMarkerWritten();

    [LoggerMessage(Level = LogLevel.Error, Message = "Encrypted stores did not open ({ErrorType}); they stay locked")]
    private partial void LogOpenFailed(string errorType);
}
