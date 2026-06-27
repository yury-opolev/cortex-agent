using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary>
/// Hosted service that starts and stops the <see cref="AgentRuntime"/> consumer loop.
/// On startup, restores session state from a snapshot file (if present).
/// On shutdown, saves all active sessions to the snapshot file for resilient restart.
/// </summary>
internal sealed partial class AgentProcessingService : IHostedService
{
    private readonly IAgentRuntime runtime;
    private readonly AgentSessionStore sessionStore;
    private readonly string stateDir;
    private readonly ILogger<AgentProcessingService> logger;

    public AgentProcessingService(
        IAgentRuntime runtime,
        AgentSessionStore sessionStore,
        string stateDir,
        ILogger<AgentProcessingService> logger)
    {
        this.runtime = runtime;
        this.sessionStore = sessionStore;
        this.stateDir = stateDir;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
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
        // Stop processing first — drains in-flight work so sessions are in a clean state.
        await this.runtime.StopProcessingAsync(cancellationToken).ConfigureAwait(false);

        // Save all active sessions to disk for restoration on next startup.
        var snapshot = SessionSnapshotSerializer.CaptureSnapshot(this.sessionStore);
        if (snapshot.Sessions.Count > 0)
        {
            await SessionSnapshotSerializer.SaveAsync(snapshot, this.stateDir, this.logger).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restored {Count} sessions from snapshot")]
    private partial void LogSessionsRestored(int count);
}
