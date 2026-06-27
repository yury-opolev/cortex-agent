namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Narrow seam over the `docker compose` CLI for the STT sidecar (whisper-stt,
/// container <c>cortex-stt</c>, profile <c>voice</c>). Separate from
/// <see cref="IComposeCommandRunner"/> (the TTS seam) so each sidecar's lifecycle
/// is independently testable; both are implemented by the one
/// <see cref="DockerComposeCommandRunner"/> instance, which shares the underlying
/// process shell-out.
/// </summary>
public interface ISttComposeRunner
{
    /// <summary>`docker compose --profile voice up -d stt`. Returns true on exit 0.</summary>
    Task<bool> StartSttAsync(CancellationToken cancellationToken);

    /// <summary>`docker compose --profile voice stop stt`. Returns true on exit 0.</summary>
    Task<bool> StopSttAsync(CancellationToken cancellationToken);

    /// <summary>True if the `cortex-stt` container is currently running.</summary>
    Task<bool> IsSttRunningAsync(CancellationToken cancellationToken);
}
