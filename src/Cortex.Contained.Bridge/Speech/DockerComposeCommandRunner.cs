using System.Diagnostics;
using Cortex.Contained.Common;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Real <see cref="IComposeCommandRunner"/> that shells out to the `docker` CLI
/// for the danish compose profile. Mirrors the Process shell-out style of
/// <see cref="Tenants.DockerContainerManager"/>. The compose file is resolved to
/// <c>%LOCALAPPDATA%\Cortex\docker-compose.yml</c>.
/// </summary>
public sealed partial class DockerComposeCommandRunner : IComposeCommandRunner, ISttComposeRunner, IEmbeddingsComposeRunner, IVoiceIdComposeRunner, IRerankerComposeRunner
{
    private const string DanishContainerName = "cortex-uni-voices";
    private const string SttContainerName = "cortex-stt";
    private const string EmbeddingsContainerName = "cortex-embeddings";
    private const string VoiceIdContainerName = "cortex-voice-id";
    private const string RerankerContainerName = "cortex-reranker";

    /// <summary>Environment variable compose interpolates into the reranker's <c>LLAMA_API_KEY</c>.</summary>
    internal const string RerankerKeyVariable = "CORTEX_RERANKER_API_KEY";

    /// <summary>Image pull/start can be slow on first run, so allow a generous window.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long compose waits for the reranker to become healthy (model load).</summary>
    internal const int RerankerWaitSeconds = 180;

    /// <summary>How long compose waits for the embeddings sidecar to become healthy.</summary>
    internal const int EmbeddingsWaitSeconds = 120;

    /// <summary>The override that adds the NVIDIA device reservation to the embeddings service.</summary>
    internal const string EmbeddingsGpuOverrideFileName = "docker-compose.embeddings-gpu.yml";

    /// <summary>The override that publishes the embeddings service on the host's loopback interface.</summary>
    internal const string EmbeddingsHostOverrideFileName = "docker-compose.embeddings-host.yml";

    /// <summary>The compose wait plus headroom for the container to be created.</summary>
    private static readonly TimeSpan EmbeddingsStartTimeout = TimeSpan.FromSeconds(EmbeddingsWaitSeconds + 30);

    /// <summary>The compose wait plus headroom for the container to be created.</summary>
    private static readonly TimeSpan RerankerStartTimeout = TimeSpan.FromSeconds(RerankerWaitSeconds + 30);

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
    public async Task<bool> RestartDanishAsync(CancellationToken cancellationToken)
    {
        // Model reload on boot is slow, so reuse the generous start window.
        return await this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile tts restart uni-voices",
            StartTimeout,
            cancellationToken).ConfigureAwait(false);
    }

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

    /// <inheritdoc />
    public Task<bool> RestartSttAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            // Model reload on boot is slow, so reuse the generous start window.
            $"compose -f \"{this.composeFilePath}\" --profile voice restart stt",
            StartTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> StartEmbeddingsAsync(bool useGpu, bool recreate, bool publishToHost, CancellationToken cancellationToken)
        => this.RunCommandAsync(
            EmbeddingsUpArguments(this.composeFilePath, useGpu, recreate, publishToHost),
            EmbeddingsStartTimeout,
            cancellationToken);

    /// <summary>
    /// <c>up -d --wait</c> returns once the container's HEALTHCHECK (<c>ollama list</c>) passes. The override
    /// files live next to the compose file (the Launcher syncs them): the GPU one is added only for the GPU,
    /// the host one only when the embedder is published on the host's loopback interface.
    /// </summary>
    internal static string EmbeddingsUpArguments(string composeFilePath, bool useGpu, bool recreate, bool publishToHost)
    {
        var directory = Path.GetDirectoryName(composeFilePath) ?? string.Empty;
        var files = $"-f \"{composeFilePath}\"";
        if (useGpu)
        {
            files += $" -f \"{Path.Combine(directory, EmbeddingsGpuOverrideFileName)}\"";
        }

        if (publishToHost)
        {
            files += $" -f \"{Path.Combine(directory, EmbeddingsHostOverrideFileName)}\"";
        }

        var force = recreate ? " --force-recreate" : string.Empty;
        return $"compose {files} --profile memory up -d{force} --wait --wait-timeout {EmbeddingsWaitSeconds} embeddings";
    }

    /// <inheritdoc />
    public Task<bool> StopEmbeddingsAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile memory stop embeddings",
            ShortTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsEmbeddingsRunningAsync(CancellationToken cancellationToken)
        => this.IsContainerRunningAsync(EmbeddingsContainerName, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> IsEmbeddingsOnGpuAsync(CancellationToken cancellationToken)
    {
        var output = await this.CaptureAsync(
            $"inspect {EmbeddingsContainerName} --format \"{{{{len .HostConfig.DeviceRequests}}}}\"",
            cancellationToken).ConfigureAwait(false);
        return int.TryParse(output?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var requests) && requests > 0;
    }

    /// <inheritdoc />
    public async Task<bool> IsEmbeddingsPublishedToHostAsync(CancellationToken cancellationToken)
    {
        var output = await this.CaptureAsync(
            $"inspect {EmbeddingsContainerName} --format \"{{{{len .HostConfig.PortBindings}}}}\"",
            cancellationToken).ConfigureAwait(false);
        return int.TryParse(output?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bindings) && bindings > 0;
    }

    /// <inheritdoc />
    public async Task<string?> GetEmbeddingsProcessorAsync(CancellationToken cancellationToken)
        => OllamaPs.ParseProcessor(await this.CaptureAsync($"exec {EmbeddingsContainerName} ollama ps", cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<bool> StartVoiceIdAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile voiceid up -d voice-id",
            StartTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> StopVoiceIdAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile voiceid stop voice-id",
            ShortTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsVoiceIdRunningAsync(CancellationToken cancellationToken)
        => this.IsContainerRunningAsync(VoiceIdContainerName, cancellationToken);

    /// <inheritdoc />
    public Task<bool> StartRerankerAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return this.RunCommandAsync(
            RerankerUpArguments(this.composeFilePath),
            RerankerStartTimeout,
            cancellationToken,
            new Dictionary<string, string> { [RerankerKeyVariable] = apiKey });
    }

    /// <summary>
    /// <c>up -d --wait</c> returns once the container's HEALTHCHECK (llama-server <c>/health</c>) passes,
    /// and fails when it exits or turns unhealthy, so "started" means "serving".
    /// </summary>
    internal static string RerankerUpArguments(string composeFilePath) =>
        $"compose -f \"{composeFilePath}\" --profile reranker up -d --wait --wait-timeout {RerankerWaitSeconds} reranker";

    /// <inheritdoc />
    public Task<bool> StopRerankerAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync(
            $"compose -f \"{this.composeFilePath}\" --profile reranker stop reranker",
            ShortTimeout,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsRerankerRunningAsync(CancellationToken cancellationToken)
        => this.IsContainerRunningAsync(RerankerContainerName, cancellationToken);

    /// <inheritdoc />
    public Task<bool> HasRerankerImageAsync(CancellationToken cancellationToken)
        => this.RunCommandAsync("image inspect cortex-reranker:latest --format {{.Id}}", ShortTimeout, cancellationToken);

    /// <summary>Runs `docker <paramref name="arguments"/>` and returns stdout, or null on a non-zero exit, a timeout or an error.</summary>
    private async Task<string?> CaptureAsync(string arguments, CancellationToken cancellationToken)
    {
        try
        {
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
            timeoutCts.CancelAfter(ShortTimeout);

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (OperationCanceledException)
        {
            this.LogCommandTimeout(arguments);
            return null;
        }
        catch (Exception ex)
        {
            this.LogCommandError(arguments, ex.Message);
            return null;
        }
    }

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

    /// <summary>
    /// The compose child's extra environment: the host time zone (so interpolating the agent's
    /// <c>History__TimeZone</c> never differs from the Launcher's) overlaid with the caller's variables.
    /// </summary>
    internal static Dictionary<string, string> BuildEnvironment(
        IReadOnlyDictionary<string, string>? extra, IReadOnlyDictionary<string, string> hostTimeZone)
    {
        var merged = new Dictionary<string, string>(hostTimeZone);
        foreach (var (name, value) in extra ?? new Dictionary<string, string>())
        {
            merged[name] = value;
        }

        return merged;
    }

    private async Task<bool> RunCommandAsync(
        string arguments, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
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

            // Secrets travel in the child's environment only: never on the command line, never logged.
            foreach (var (name, value) in BuildEnvironment(environment, HostTimeZone.ComposeEnvironment()))
            {
                process.StartInfo.Environment[name] = value;
            }

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
