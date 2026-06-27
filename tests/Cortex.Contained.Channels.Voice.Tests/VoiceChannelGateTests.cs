namespace Cortex.Contained.Channels.Voice.Tests;

using Cortex.Contained.Channels.Voice;
using Cortex.Contained.Speech.SpeakerId;

public sealed class VoiceChannelGateTests
{
    [Fact]
    public async Task NullVerifier_PassesTranscript()
    {
        var decision = await VoiceChannelGate.EvaluateAsync(
            verifier: null,
            tenantId: "tenant-a",
            pcm16Mono16k: new short[16000],
            CancellationToken.None);

        Assert.True(decision.PassesTranscript);
        Assert.Null(decision.Result);
    }

    [Fact]
    public async Task Accept_PassesTranscript()
    {
        var verifier = new FakeVerifier(new VerificationResult.Accept(0.9f));
        var decision = await VoiceChannelGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);

        Assert.True(decision.PassesTranscript);
        Assert.IsType<VerificationResult.Accept>(decision.Result);
    }

    [Fact]
    public async Task Reject_DropsTranscript()
    {
        var verifier = new FakeVerifier(new VerificationResult.Reject(0.1f));
        var decision = await VoiceChannelGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);

        Assert.False(decision.PassesTranscript);
        Assert.IsType<VerificationResult.Reject>(decision.Result);
    }

    [Theory]
    [InlineData(VerificationResult.SkipReason.FeatureOff)]
    [InlineData(VerificationResult.SkipReason.EnrollmentInProgress)]
    [InlineData(VerificationResult.SkipReason.TooShort)]
    [InlineData(VerificationResult.SkipReason.Error)]
    public async Task SkippedVariants_PassTranscript(VerificationResult.SkipReason reason)
    {
        var verifier = new FakeVerifier(new VerificationResult.Skipped(reason));
        var decision = await VoiceChannelGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);

        Assert.True(decision.PassesTranscript);
    }

    [Fact]
    public async Task NotEnrolled_PassesTranscript()
    {
        var verifier = new FakeVerifier(VerificationResult.NotEnrolled);
        var decision = await VoiceChannelGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);
        Assert.True(decision.PassesTranscript);
    }

    [Fact]
    public async Task VerifierThrows_PassesTranscript()
    {
        var verifier = new ThrowingVerifier();
        var decision = await VoiceChannelGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);

        Assert.True(decision.PassesTranscript);
        Assert.Null(decision.Result);
    }

    [Fact]
    public async Task VerifierHangs_TimesOutAndPassesTranscript()
    {
        var verifier = new HangingVerifier();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var decision = await VoiceChannelGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);
        sw.Stop();

        Assert.True(decision.PassesTranscript);
        Assert.True(sw.Elapsed < VoiceChannelGate.VerifyTimeout + TimeSpan.FromSeconds(1));
    }

    private sealed class FakeVerifier(VerificationResult result) : ISpeakerVerifier
    {
        public ValueTask<VerificationResult> VerifyAsync(ReadOnlyMemory<short> pcm16Mono16k, string tenantId, CancellationToken ct)
            => ValueTask.FromResult(result);
    }

    private sealed class ThrowingVerifier : ISpeakerVerifier
    {
        public ValueTask<VerificationResult> VerifyAsync(ReadOnlyMemory<short> pcm16Mono16k, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException("simulated");
    }

    private sealed class HangingVerifier : ISpeakerVerifier
    {
        public async ValueTask<VerificationResult> VerifyAsync(ReadOnlyMemory<short> pcm16Mono16k, string tenantId, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new VerificationResult.Accept(1.0f);
        }
    }
}
