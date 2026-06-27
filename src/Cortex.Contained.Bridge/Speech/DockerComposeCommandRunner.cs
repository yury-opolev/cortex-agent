using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Real <see cref="IComposeCommandRunner"/> that shells out to the `docker` CLI
/// for the danish compose profile. Mirrors the Process shell-out style of
/// <see cref="Tenants.DockerContainerManager"/>. The compose file is resolved to
/// <c>%LOCALAPPDATA%\Cortex\docker-compose.yml</c>.
/// </summary>
public sealed partial class DockerComposeCommandRunner : IComposeCommandRunner, ISttComposeRunner
{
    private const string DanishContainerName = "cortex-uni-voices";
    private const string SttContainerName = "cortex-stt";

    /// <summary>Image pull/start can be slow on first run, so allow a generous window.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Stop and ps are quick; cap them tightly.</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger<DockerComposeCommandRunner> logger;
    private readonly string composeFilePath;

    public DockerComposeCommandRunner(ILogger<DockerComposeCommandRunner> logger)
    {
        this.logger = logger;
        this.composeFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cortex",
            "docker-compose.yml");
    }

    /// <inheritdoc />
    public async Task<bool> StartDanishAsync(CancellationToken cancellationToken)
    {
        return await this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile tts up -d uni-voices",
            StartTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> StopDanishAsync(CancellationToken cancellationToken)
    {
        return await this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile tts stop uni-voices",
            ShortTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> IsDanishRunningAsync(CancellationToken cancellationToken)
        => this.IsContainerRunningAsync(DanishContainerName, cancellationToken);

    /// <inheritdoc />
    public Task<bool> StartSttAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile voice up -d stt",
            StartTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> StopSttAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile voice stop stt",
            ShortTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsSttRunningAsync(CancellationToken cancellationToken)
        => this.IsContainerRunningAsync(SttContainerName, cancellationToken);

    private async Task<bool> IsContainerRunningAsync(string containerName, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"ps --filter name={containerName} --filter status=running --format {{{{.Names}}}}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ShortTimeout);

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            return stdout.Contains(containerName, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            this.LogPsTimeout();
            return false;
        }
        catch (Exception ex)
        {
            this.LogPsError(ex.Message);
            return false;
        }
    }

    private async Task<bool> RunCommandAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            this.LogRunningCommand(arguments);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                return true;
            }

            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            this.LogCommandFailed(arguments, stderr.Trim());
            return false;
        }
        catch (OperationCanceledException)
        {
            this.LogCommandTimeout(arguments);
            return false;
        }
        catch (Exception ex)
        {
            this.LogCommandError(arguments, ex.Message);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Running docker {Arguments}")]
    private partial void LogRunningCommand(string arguments);

    [LoggerMessage(Level = LogLevel.Warning, Message = "docker {Arguments} failed: {Error}")]
    private partial void LogCommandFailed(string arguments, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "docker {Arguments} timed out")]
    private partial void LogCommandTimeout(string arguments);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error running docker {Arguments}: {Error}")]
    private partial void LogCommandError(string arguments, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "docker ps for danish-tts timed out")]
    private partial void LogPsTimeout();

    [LoggerMessage(Level = LogLevel.Error, Message = "Error checking danish-tts container status: {Error}")]
    private partial void LogPsError(string error);
}
