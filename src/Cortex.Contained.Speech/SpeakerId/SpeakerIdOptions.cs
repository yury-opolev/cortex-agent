namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Selects the in-process vs sidecar backend for speaker-ID inference.
/// </summary>
public enum SpeakerIdBackend
{
    /// <summary>
    /// In-process <see cref="OnnxSpeakerEmbedder"/>. Each process loads its
    /// own ONNX model. Best for single-tenant deployments and dev workflows
    /// without Docker.
    /// </summary>
    Local,

    /// <summary>
    /// Remote <see cref="HttpSpeakerEmbedder"/> targeting the voice-id sidecar.
    /// One model instance serves all tenants. Recommended for multi-tenant
    /// deployments.
    /// </summary>
    Remote,
}

/// <summary>
/// Tunable parameters for the speaker-verification gate. Per-tenant overrides
/// (e.g. <see cref="VoiceprintRecord.ThresholdOverride"/>) take precedence
/// over the values here.
/// </summary>
public sealed record SpeakerIdOptions
{
    /// <summary>
    /// Default cosine similarity threshold above which an utterance is
    /// accepted as the enrolled speaker. Calibrated for 3D-Speaker
    /// ERes2NetV2 ONNX exports.
    /// </summary>
    public float DefaultCosineThreshold { get; init; } = 0.55f;

    /// <summary>
    /// Minimum voiced audio length (after VAD/silence trim) required to run
    /// a meaningful verification.
    /// </summary>
    public TimeSpan MinUtteranceLength { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Sample rate the embedder expects.
    /// </summary>
    public int EmbedderSampleRate { get; init; } = 16000;

    /// <summary>
    /// Which embedder implementation to construct at DI time.
    /// </summary>
    public SpeakerIdBackend Backend { get; init; } = SpeakerIdBackend.Local;

    /// <summary>
    /// Sidecar endpoint URL when <see cref="Backend"/> is <see cref="SpeakerIdBackend.Remote"/>.
    /// </summary>
    public string RemoteEndpoint { get; init; } = "http://voice-id:5200";

    /// <summary>
    /// Per-call HTTP timeout when <see cref="Backend"/> is <see cref="SpeakerIdBackend.Remote"/>.
    /// </summary>
    public TimeSpan RemoteTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
}
