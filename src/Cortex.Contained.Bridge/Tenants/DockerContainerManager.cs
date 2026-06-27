using System.Diagnostics;

namespace Cortex.Contained.Bridge.Tenants;

/// <summary>
/// Manages agent containers via Docker CLI.
/// Container names follow the convention <c>cortex-{tenantId}</c>.
/// </summary>
public sealed partial class DockerContainerManager : IContainerManager
{
    private readonly ILogger<DockerContainerManager> logger;

    /// <summary>Maximum time to wait for a single container to stop.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    public DockerContainerManager(ILogger<DockerContainerManager> logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> StopContainerAsync(string tenantId, CancellationToken cancellationToken)
    {
        var containerName = $"cortex-{tenantId}";

        try
        {
            this.LogStoppingContainer(containerName, tenantId);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"stop -t 10 {containerName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(StopTimeout);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                this.LogContainerStopped(containerName, tenantId);
                return true;
            }

            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            this.LogContainerStopFailed(containerName, tenantId, stderr.Trim());
            return false;
        }
        catch (OperationCanceledException)
        {
            this.LogContainerStopTimeout(containerName, tenantId);
            return false;
        }
        catch (Exception ex)
        {
            this.LogContainerStopError(containerName, tenantId, ex.Message);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping container {ContainerName} for tenant '{TenantId}'")]
    private partial void LogStoppingContainer(string containerName, string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Container {ContainerName} stopped for tenant '{TenantId}'")]
    private partial void LogContainerStopped(string containerName, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Container {ContainerName} stop failed for tenant '{TenantId}': {Error}")]
    private partial void LogContainerStopFailed(string containerName, string tenantId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Container {ContainerName} stop timed out for tenant '{TenantId}'")]
    private partial void LogContainerStopTimeout(string containerName, string tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error stopping container {ContainerName} for tenant '{TenantId}': {Error}")]
    private partial void LogContainerStopError(string containerName, string tenantId, string error);
}
