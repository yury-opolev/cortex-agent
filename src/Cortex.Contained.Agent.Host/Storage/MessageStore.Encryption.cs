using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Encryption at rest for <see cref="MessageStore"/> (D4): with <see cref="MessageStoreEncryption"/>
/// the store opens on first use after the Bridge has pushed the storage key, migrating a plaintext
/// database in place first. Every read and write funnels through <see cref="EnsureOpenAsync"/>, so
/// nothing is served — and no plaintext file is created — before the key arrives.
/// </summary>
public sealed partial class MessageStore : IStorageState
{
    /// <summary>Purpose label the <c>messages.db</c> passphrase is derived for.</summary>
    public const string PasswordPurpose = "messages.db";

    private readonly SemaphoreSlim openGate = new(1, 1);
    private volatile StorageState state = StorageState.AwaitingKey;

    /// <inheritdoc />
    public StorageState State => this.state;

    /// <inheritdoc />
    public bool CanServeWithoutWaiting =>
        this.encryption is not { } encryption
        || this.state == StorageState.Open
        || (this.state == StorageState.AwaitingKey && encryption.Keys.IsAvailable);

    /// <inheritdoc />
    public async Task WhenOpenAsync(CancellationToken cancellationToken)
    {
        if (this.encryption is { } encryption)
        {
            await encryption.Keys.WhenAvailableAsync(cancellationToken).ConfigureAwait(false);
        }

        await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the encrypted store on first use. Waits up to <see cref="MessageStoreEncryption.KeyWait"/>
    /// for the key, then throws <see cref="StorageLockedException"/>; a key that does not open the
    /// existing file locks the store for the process lifetime.
    /// </summary>
    internal async ValueTask EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (this.state == StorageState.Open || this.encryption is not { } encryption)
        {
            return;
        }

        await this.openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.state == StorageState.Open)
            {
                return;
            }

            if (this.state == StorageState.Failed)
            {
                throw new StorageLockedException("messages.db does not open with the provided storage key; it stays locked.");
            }

            string password;
            try
            {
                password = await encryption.Keys.WaitForPasswordAsync(PasswordPurpose, encryption.KeyWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new StorageLockedException("messages.db is locked: the Bridge has not provided the storage key.", ex);
            }

            try
            {
                var outcome = EncryptedSqlite.Prepare(this.databasePath, password, this.FaultInjector);
                this.Open(password);
                this.LogEncryptedOpen(outcome);
            }
            catch (StorageKeyMismatchException)
            {
                this.state = StorageState.Failed;
                throw;
            }
        }
        finally
        {
            this.openGate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "messages.db opened encrypted ({Outcome})")]
    private partial void LogEncryptedOpen(EncryptedOpenOutcome outcome);
}
