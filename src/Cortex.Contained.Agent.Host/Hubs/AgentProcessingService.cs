using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary>
/// Hosted service that starts and stops the <see cref="AgentRuntime"/> consumer loop.
/// On startup, restores session state from a snapshot file (if present).
/// On shutdown, saves all active sessions to the snapshot file for resilient restart.
/// With encryption at rest the snapshot is written encrypted (never in plaintext: without the key it is
/// skipped), and an encrypted snapshot is restored once the Bridge has pushed the key — the consumer loop
/// starts after that restore so restored history always precedes new messages.
/// </summary>
internal sealed partial class AgentProcessingService : IHostedService, IDisposable
{
    private readonly IAgentRuntime runtime;
    private readonly AgentSessionStore sessionStore;
    private readonly string stateDir;
    private readonly ILogger<AgentProcessingService> logger;
    private readonly StorageEncryptionContext? encryption;
    private readonly StorageKeyRing? keys;
    private readonly TimeSpan keyWait;
    private readonly CancellationTokenSource stopping = new();
    private Task deferredStart = Task.CompletedTask;

    public AgentProcessingService(
        IAgentRuntime runtime,
        AgentSessionStore sessionStore,
        string stateDir,
        ILogger<AgentProcessingService> logger,
        StorageEncryptionContext? encryption = null,
        StorageKeyRing? keys = null,
        TimeSpan? keyWait = null)
    {
        this.runtime = runtime;
        this.sessionStore = sessionStore;
        this.stateDir = stateDir;
        this.logger = logger;
        this.encryption = encryption;
        this.keys = keys;
        this.keyWait = keyWait ?? TimeSpan.FromSeconds(120);
    }

    /// <summary>The deferred restore-then-start of an encrypted snapshot (completed when there is none).</summary>
    internal Task DeferredStart => this.deferredStart;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (this.keys is not null && SessionSnapshotSerializer.IsEncrypted(this.stateDir))
        {
            this.deferredStart = Task.Run(() => this.RestoreEncryptedThenStartAsync(this.stopping.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        // Restore sessions from snapshot before starting the consumer loop.
        // If restore fails or no snapshot exists, sessions start empty and
        // the Bridge will re-seed them from MessageStore.
        var restored = SessionSnapshotSerializer.TryRestore(this.sessionStore, this.stateDir, this.logger);
        if (restored > 0)
        {
            this.LogSessionsRestored(restored);
        }

        return this.runtime.StartProcessingAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await this.stopping.CancelAsync().ConfigureAwait(false);
        await this.deferredStart.ConfigureAwait(false);

        // Stop processing first — drains in-flight work so sessions are in a clean state.
        await this.runtime.StopProcessingAsync(cancellationToken).ConfigureAwait(false);

        // Save all active sessions to disk for restoration on next startup.
        var snapshot = SessionSnapshotSerializer.CaptureSnapshot(this.sessionStore);
        if (snapshot.Sessions.Count == 0)
        {
            return;
        }

        StorageFileCipher? cipher = null;
        if (this.encryption?.ProtectsNewWrites == true)
        {
            if (this.keys is not { IsAvailable: true })
            {
                this.LogSnapshotSkipped(snapshot.Sessions.Count);
                return;
            }

            cipher = new StorageFileCipher(this.keys.KeyFor(SessionSnapshotSerializer.Purpose), SessionSnapshotSerializer.Purpose);
        }

        await SessionSnapshotSerializer.SaveAsync(snapshot, this.stateDir, this.logger, cipher).ConfigureAwait(false);
    }

    public void Dispose() => this.stopping.Dispose();

    private async Task RestoreEncryptedThenStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var key = await this.keys!.WaitForKeyAsync(SessionSnapshotSerializer.Purpose, this.keyWait, cancellationToken).ConfigureAwait(false);
            var restored = SessionSnapshotSerializer.TryRestore(
                this.sessionStore, this.stateDir, this.logger, new StorageFileCipher(key, SessionSnapshotSerializer.Purpose));
            if (restored > 0)
            {
                this.LogSessionsRestored(restored);
            }
        }
        catch (TimeoutException)
        {
            this.LogSnapshotRestoreDeferred();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await this.runtime.StartProcessingAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restored {Count} sessions from snapshot")]
    private partial void LogSessionsRestored(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session snapshot of {Count} sessions not saved: encryption at rest is on and the storage key is not available")]
    private partial void LogSnapshotSkipped(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encrypted session snapshot not restored: the storage key did not arrive in time; processing starts without it and the snapshot is kept")]
    private partial void LogSnapshotRestoreDeferred();
}
