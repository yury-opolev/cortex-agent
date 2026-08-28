namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Narrow seam over the `docker compose` CLI for the danish profile. Abstracted
/// so DanishTtsLifecycle is unit-testable without invoking Docker.
/// </summary>
public interface IComposeCommandRunner
{
    /// <summary>`docker compose --profile danish up -d danish-tts`. Returns true on exit 0.</summary>
    Task<bool> StartDanishAsync(CancellationToken cancellationToken);

    /// <summary>`docker compose --profile tts stop uni-voices`. Returns true on exit 0.</summary>
    Task<bool> StopDanishAsync(CancellationToken cancellationToken);

    /// <summary>
    /// `docker compose --profile tts restart uni-voices`. Returns true on exit 0.
    /// Used to recover a sidecar whose CUDA context has been poisoned — that fault is
    /// sticky for the process lifetime, so only a fresh process fixes it.
    /// </summary>
    Task<bool> RestartDanishAsync(CancellationToken cancellationToken);

    /// <summary>True if the `cortex-danish-tts` container is currently running.</summary>
    Task<bool> IsDanishRunningAsync(CancellationToken cancellationToken);
}
