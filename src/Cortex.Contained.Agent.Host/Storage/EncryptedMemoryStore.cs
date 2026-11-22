using System.Text;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Read access to the curated memory files for code that bypasses <see cref="IMemoryStore"/> (memory list, quarantine times).</summary>
public interface IMemoryAtRest
{
    /// <summary>The SQLite3 Multiple Ciphers passphrase of <c>memory.db</c>, or null when the store is plaintext. Waits for the open.</summary>
    Task<string?> GetDatabasePasswordAsync(CancellationToken cancellationToken);

    /// <summary>The text of a <c>*.memory.data</c> file (decrypted when encrypted).</summary>
    Task<string> ReadContentAsync(string path, CancellationToken cancellationToken);
}

/// <summary><see cref="IMemoryAtRest"/> of an unencrypted memory store.</summary>
public sealed class PlaintextMemoryAtRest : IMemoryAtRest
{
    /// <inheritdoc />
    public Task<string?> GetDatabasePasswordAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<string> ReadContentAsync(string path, CancellationToken cancellationToken) => File.ReadAllTextAsync(path, cancellationToken);
}

/// <summary>
/// The curated memory store encrypted with the tenant key (D4): <c>memory.db</c> (chunks, sqlite-vec vectors,
/// FTS5 keyword index) through SQLite3 Multiple Ciphers and every <c>*.memory.data</c> file through
/// AES-256-GCM. Opens lazily once the Bridge has pushed the key, migrating a plaintext store first (database by export to a sibling file, then content files)
/// (database, then content files). Fail closed: nothing is served or created before the key arrives, and a
/// key that does not open the data locks the store for the process lifetime without touching a byte.
/// </summary>
public sealed partial class EncryptedMemoryStore : IMemoryStore, IMemoryAtRest, IDisposable
{
    /// <summary>Purpose of the <c>memory.db</c> passphrase.</summary>
    public const string DatabasePurpose = "memory.db";

    /// <summary>Purpose (and associated-data label) of the content file key.</summary>
    public const string ContentPurpose = "memory-content";

    private readonly IOptions<MemoryMcpOptions> options;
    private readonly StorageKeyRing keys;
    private readonly TimeSpan keyWait;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<EncryptedMemoryStore> logger;
    private readonly SemaphoreSlim openGate = new(1, 1);
    private volatile SqliteVecMemoryStore? inner;
    private volatile StorageState state = StorageState.AwaitingKey;
    private string? password;
    private StorageFileCipher? cipher;

    public EncryptedMemoryStore(IOptions<MemoryMcpOptions> options, StorageKeyRing keys, TimeSpan keyWait, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.options = options;
        this.keys = keys;
        this.keyWait = keyWait;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<EncryptedMemoryStore>();
    }

    /// <summary>Whether the store is serving.</summary>
    public StorageState State => this.state;

    /// <summary>Test seam for the content migration (<see cref="MemoryContentMigrator.Migrate"/>) and the export stages of <see cref="EncryptedSqlite.Prepare"/>.</summary>
    internal Action<string>? FaultInjector { get; set; }

    /// <inheritdoc />
    public string? KeywordIndexUnavailableReason =>
        this.inner?.KeywordIndexUnavailableReason
        ?? (this.state == StorageState.Failed ? "The memory store is locked: the storage key does not open it." : null);

    /// <summary>Nothing happens at startup: the store opens when the key arrives (see <see cref="WhenOpenAsync"/>).</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Waits for the key without a time limit, then opens (and migrates) the store.</summary>
    public async Task WhenOpenAsync(CancellationToken cancellationToken)
    {
        await this.keys.WhenAvailableAsync(cancellationToken).ConfigureAwait(false);
        await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StoreMemoryAsync(string memoryId, string content, List<ChunkRecord> chunks, List<float[]> vectors, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).StoreMemoryAsync(memoryId, content, chunks, vectors, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<MemoryResult?> GetMemoryAsync(string memoryId, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).GetMemoryAsync(memoryId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(string memoryId, string? title, List<string>? tags, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).UpdateMetadataAsync(memoryId, title, tags, updatedAt, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> DeleteMemoryAsync(string memoryId, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).DeleteMemoryAsync(memoryId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> DeleteMemoriesAsync(IReadOnlyCollection<string> memoryIds, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).DeleteMemoriesAsync(memoryIds, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<List<SearchResult>> SearchAsync(float[] queryVector, int limit, float? minScore = null, List<string>? tags = null, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).SearchAsync(queryVector, limit, minScore, tags, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<List<SearchResult>> SearchDenseAsync(float[] queryVector, int limit, float? minScore, MemorySearchFilter filter, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).SearchDenseAsync(queryVector, limit, minScore, filter, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<List<KeywordHit>> KeywordSearchAsync(string matchExpression, int limit, MemorySearchFilter filter, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).KeywordSearchAsync(matchExpression, limit, filter, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> RebuildKeywordIndexAsync(CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).RebuildKeywordIndexAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string memoryId, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).ExistsAsync(memoryId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<string?> GetDatabasePasswordAsync(CancellationToken cancellationToken)
    {
        await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        return this.password;
    }

    /// <inheritdoc />
    public async Task<string> ReadContentAsync(string path, CancellationToken cancellationToken)
    {
        await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        var blob = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(this.cipher!.Decrypt(blob));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.inner?.Dispose();
        this.openGate.Dispose();
    }

    private async ValueTask<SqliteVecMemoryStore> EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (this.inner is { } open)
        {
            return open;
        }

        await this.openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.inner is { } raced)
            {
                return raced;
            }

            if (this.state == StorageState.Failed)
            {
                throw new StorageLockedException("memory.db does not open with the provided storage key; it stays locked.");
            }

            byte[] contentKey;
            try
            {
                this.password = await this.keys.WaitForPasswordAsync(DatabasePurpose, this.keyWait, cancellationToken).ConfigureAwait(false);
                contentKey = await this.keys.WaitForKeyAsync(ContentPurpose, this.keyWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new StorageLockedException("The memory store is locked: the Bridge has not provided the storage key.", ex);
            }

            this.cipher = new StorageFileCipher(contentKey, ContentPurpose);
            var settings = this.options.Value;
            try
            {
                Directory.CreateDirectory(settings.DataDirectory);
                var outcome = EncryptedSqlite.Prepare(settings.DatabasePath, this.password, this.FaultInjector);
                var content = MemoryContentMigrator.Migrate(settings.MemoriesDirectory, this.cipher, this.FaultInjector);
                var store = new SqliteVecMemoryStore(this.options, this.cipher, this.loggerFactory.CreateLogger<SqliteVecMemoryStore>(), this.password);
                await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
                this.inner = store;
                this.state = StorageState.Open;
                this.LogOpened(outcome, content.Migrated, content.Recovered);
                return store;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory store opened encrypted (database {Outcome}; {Migrated} content files encrypted, {Recovered} interrupted conversions completed)")]
    private partial void LogOpened(EncryptedOpenOutcome outcome, int migrated, int recovered);
}
