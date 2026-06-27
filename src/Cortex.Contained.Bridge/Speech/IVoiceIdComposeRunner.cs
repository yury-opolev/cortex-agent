namespace Cortex.Contained.Bridge.Speech;

/// <summary>Narrow seam over the docker-compose CLI for the cortex-voice-id sidecar.</summary>
public interface IVoiceIdComposeRunner
{
    /// <summary>`docker compose --profile voiceid up -d voice-id`. Returns true on exit 0.</summary>
    Task<bool> StartVoiceIdAsync(CancellationToken cancellationToken);

    /// <summary>`docker compose --profile voiceid stop voice-id`. Returns true on exit 0.</summary>
    Task<bool> StopVoiceIdAsync(CancellationToken cancellationToken);

    /// <summary>True if the `cortex-voice-id` container is currently running.</summary>
    Task<bool> IsVoiceIdRunningAsync(CancellationToken cancellationToken);
}
