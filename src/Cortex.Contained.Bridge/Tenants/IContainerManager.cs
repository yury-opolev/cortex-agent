namespace Cortex.Contained.Bridge.Tenants;

/// <summary>
/// Manages the lifecycle of agent containers.
/// Abstracted for testability — the real implementation shells out to Docker CLI.
/// </summary>
public interface IContainerManager
{
    /// <summary>
    /// Gracefully stops a tenant's agent container.
    /// Sends SIGTERM and waits for the container to exit.
    /// </summary>
    /// <param name="tenantId">Tenant identifier (used to derive container name).</param>
    /// <param name="cancellationToken">Cancellation token (typically the host shutdown timeout).</param>
    /// <returns>True if the container was stopped successfully, false otherwise.</returns>
    Task<bool> StopContainerAsync(string tenantId, CancellationToken cancellationToken);
}
