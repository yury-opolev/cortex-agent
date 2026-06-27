using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Speech.SpeakerId;

namespace Cortex.Contained.Channels.Discord.Tests;

public class WizardEnrollmentSessionTests
{
    // Fake embedder: owner utterances (sample[0]==1) -> one unit vector;
    // impostor utterances (sample[0]==9) -> an orthogonal unit vector.
    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public string ModelId => "test-model";
        public int EmbeddingDim => 4;
        public ValueTask<float[]> EmbedAsync(ReadOnlyMemory<short> pcm, CancellationToken ct)
        {
            if (pcm.Span.Length == 0)
            {
                return ValueTask.FromResult(Array.Empty<float>());
            }
            var owner = pcm.Span[0] == 1;
            float[] v = owner ? [1f, 0f, 0f, 0f] : [0f, 1f, 0f, 0f];
            return ValueTask.FromResult(SpeakerEmbeddingMath.L2Normalise(v));
        }
    }

    private static short[] Owner() => [1, 1, 1];
    private static short[] Impostor() => [9, 9, 9];

    [Fact]
    public async Task CapturesSamples_ThenBuildsVoiceprint_ThenConfirms()
    {
        var s = new WizardEnrollmentSession(new FakeEmbedder(), samplesRequired: 3, matchesRequired: 2, confirmThreshold: 0.5f);

        Assert.Equal(WizardPhase.Enrolling, s.Phase);
        await s.AddUtteranceAsync(Owner(), default);
        Assert.Equal(1, s.CapturedInPhase);
        await s.AddUtteranceAsync(Owner(), default);
        await s.AddUtteranceAsync(Owner(), default);
        Assert.Equal(WizardPhase.Confirming, s.Phase);
        Assert.Equal(0, s.CapturedInPhase);

        await s.AddUtteranceAsync(Owner(), default);
        await s.AddUtteranceAsync(Owner(), default);
        Assert.Equal(WizardPhase.Complete, s.Phase);
        Assert.NotNull(s.Voiceprint);
        Assert.Equal(4, s.Voiceprint!.Length);
        Assert.Equal("test-model", s.ModelId);
    }

    [Fact]
    public async Task ConfirmMismatch_DoesNotCount()
    {
        var s = new WizardEnrollmentSession(new FakeEmbedder(), 3, 2, 0.5f);
        await s.AddUtteranceAsync(Owner(), default);
        await s.AddUtteranceAsync(Owner(), default);
        await s.AddUtteranceAsync(Owner(), default); // -> Confirming
        await s.AddUtteranceAsync(Impostor(), default); // mismatch: no progress
        Assert.Equal(WizardPhase.Confirming, s.Phase);
        Assert.Equal(0, s.CapturedInPhase);
        await s.AddUtteranceAsync(Owner(), default);
        await s.AddUtteranceAsync(Owner(), default);
        Assert.Equal(WizardPhase.Complete, s.Phase);
    }

    [Fact]
    public async Task EmptyEmbedding_IsIgnored()
    {
        var s = new WizardEnrollmentSession(new FakeEmbedder(), 3, 2, 0.5f);
        await s.AddUtteranceAsync(Array.Empty<short>(), default);
        Assert.Equal(0, s.CapturedInPhase);
        Assert.Equal(WizardPhase.Enrolling, s.Phase);
    }

    [Fact]
    public async Task VoiceprintNull_BeforeComplete()
    {
        var s = new WizardEnrollmentSession(new FakeEmbedder(), 3, 2, 0.5f);
        await s.AddUtteranceAsync(Owner(), default);
        Assert.Null(s.Voiceprint);
    }
}
