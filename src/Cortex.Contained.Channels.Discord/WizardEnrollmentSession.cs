using Cortex.Contained.Speech.SpeakerId;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Bridge-side capture state for one tenant's voice-ID enrollment. Embeds each
/// utterance via <see cref="ISpeakerEmbedder"/>, averages the first
/// <c>samplesRequired</c> embeddings into a candidate voiceprint, then requires
/// <c>matchesRequired</c> further utterances to score at or above
/// <c>confirmThreshold</c> against the candidate. Pure logic — no Discord or
/// SignalR dependencies — so it is fully unit-testable with a fake embedder.
/// </summary>
public sealed class WizardEnrollmentSession
{
    private readonly ISpeakerEmbedder embedder;
    private readonly int samplesRequired;
    private readonly int matchesRequired;
    private readonly float confirmThreshold;
    private readonly List<float[]> enrollEmbeddings = [];
    private float[]? candidate;
    private int confirmMatches;

    /// <summary>
    /// Initialises a new enrollment session.
    /// </summary>
    /// <param name="embedder">The speaker embedder used to produce per-utterance vectors.</param>
    /// <param name="samplesRequired">Number of enrollment utterances required to build the candidate voiceprint.</param>
    /// <param name="matchesRequired">Number of confirmation utterances that must score at or above <paramref name="confirmThreshold"/>.</param>
    /// <param name="confirmThreshold">Cosine-similarity threshold for a confirmation match.</param>
    public WizardEnrollmentSession(ISpeakerEmbedder embedder, int samplesRequired, int matchesRequired, float confirmThreshold)
    {
        this.embedder = embedder;
        this.samplesRequired = samplesRequired;
        this.matchesRequired = matchesRequired;
        this.confirmThreshold = confirmThreshold;
    }

    /// <summary>Current phase of the capture session.</summary>
    public WizardPhase Phase { get; private set; } = WizardPhase.Enrolling;

    /// <summary>Count captured within the current phase (drives the script index).</summary>
    public int CapturedInPhase => this.Phase == WizardPhase.Enrolling ? this.enrollEmbeddings.Count : this.confirmMatches;

    /// <summary>Identifier of the model that produced the voiceprint.</summary>
    public string ModelId => this.embedder.ModelId;

    /// <summary>The finished L2-normalised voiceprint once <see cref="Phase"/> is Complete; otherwise null.</summary>
    public float[]? Voiceprint => this.Phase == WizardPhase.Complete ? this.candidate : null;

    /// <summary>Embed one utterance and advance the capture state machine.</summary>
    /// <param name="pcm16Mono16k">Raw 16-bit mono 16 kHz PCM samples for one utterance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask AddUtteranceAsync(ReadOnlyMemory<short> pcm16Mono16k, CancellationToken cancellationToken)
    {
        if (this.Phase == WizardPhase.Complete)
        {
            return;
        }

        var embedding = await this.embedder.EmbedAsync(pcm16Mono16k, cancellationToken).ConfigureAwait(false);
        if (embedding.Length == 0)
        {
            return;
        }

        if (this.Phase == WizardPhase.Enrolling)
        {
            this.enrollEmbeddings.Add(embedding);
            if (this.enrollEmbeddings.Count >= this.samplesRequired)
            {
                this.candidate = SpeakerEmbeddingMath.MeanOfNormalised(this.enrollEmbeddings);
                this.Phase = WizardPhase.Confirming;
            }

            return;
        }

        var score = SpeakerEmbeddingMath.CosineSimilarity(embedding, this.candidate!);
        if (score >= this.confirmThreshold)
        {
            this.confirmMatches++;
            if (this.confirmMatches >= this.matchesRequired)
            {
                this.Phase = WizardPhase.Complete;
            }
        }
    }
}
