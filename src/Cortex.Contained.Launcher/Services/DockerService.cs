using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Launcher.Services;

/// <summary>
/// Manages Docker containers via the <c>docker</c> CLI (Docker Desktop).
/// </summary>
public sealed partial class DockerService
{
    private readonly IProcessRunner processRunner;
    private readonly ILogger<DockerService> logger;

    public DockerService(IProcessRunner processRunner, ILogger<DockerService> logger)
    {
        this.processRunner = processRunner;
        this.logger = logger;
    }

    public async Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken = default)
    {
        var result = await this.processRunner.RunAsync(
            "docker",
            "info",
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0;
    }

    public async Task StartContainersAsync(string composeFilePath, CancellationToken cancellationToken = default)
    {
        this.LogStartingContainers(composeFilePath);

        var result = await this.processRunner.RunAsync(
            "docker",
            $"compose -f \"{composeFilePath}\" up -d",
            timeout: TimeSpan.FromSeconds(120),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to start containers: {result.StandardError}");
        }
    }

    public async Task StopContainersAsync(string composeFilePath, CancellationToken cancellationToken = default)
    {
        this.LogStoppingContainers(composeFilePath);

        await this.processRunner.RunAsync(
            "docker",
            $"compose -f \"{composeFilePath}\" down",
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsContainerImageOutdatedAsync(
        string containerName,
        string imageName,
        CancellationToken cancellationToken = default)
    {
        var containerResult = await this.processRunner.RunAsync(
            "docker",
            $"inspect {containerName} --format \"{{{{.Image}}}}\"",
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        if (containerResult.ExitCode != 0)
        {
            return true;
        }

        var imageResult = await this.processRunner.RunAsync(
            "docker",
            $"inspect {imageName} --format \"{{{{.Id}}}}\"",
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        if (imageResult.ExitCode != 0)
        {
            return false;
        }

        var runningImageId = containerResult.StandardOutput.Trim();
        var latestImageId = imageResult.StandardOutput.Trim();

        if (runningImageId != latestImageId)
        {
            this.LogImageOutdated(containerName, runningImageId, latestImageId);
            return true;
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Container {ContainerName} image outdated: running={RunningId}, latest={LatestId}")]
    private partial void LogImageOutdated(string containerName, string runningId, string latestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting containers from {ComposeFile}")]
    private partial void LogStartingContainers(string composeFile);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping containers from {ComposeFile}")]
    private partial void LogStoppingContainers(string composeFile);
}
