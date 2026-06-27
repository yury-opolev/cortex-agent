namespace Cortex.Contained.Channels.Discord.Tests;

using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Speech.SpeakerId;

public sealed class DiscordVoiceHandlerVerifyGateTests
{
    [Fact]
    public async Task GateDecision_NullVerifier_AlwaysPasses()
    {
        var decision = await DiscordVoiceGate.EvaluateAsync(
            verifier: null,
            tenantId: "tenant-a",
            pcm16: new short[16000],
            CancellationToken.None);

        Assert.True(decision.PassesTranscript);
        Assert.Null(decision.Result);
    }

    [Fact]
    public async Task GateDecision_AcceptResult_PassesTranscript()
    {
        var verifier = new FakeVerifier(new VerificationResult.Accept(0.9f));
        var decision = await DiscordVoiceGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);

        Assert.True(decision.PassesTranscript);
        Assert.IsType<VerificationResult.Accept>(decision.Result);
    }

    [Fact]
    public async Task GateDecision_RejectResult_DropsTranscript()
    {
        var verifier = new FakeVerifier(new VerificationResult.Reject(0.1f));
        var decision = await DiscordVoiceGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);

        Assert.False(decision.PassesTranscript);
        Assert.IsType<VerificationResult.Reject>(decision.Result);
    }

    [Theory]
    [InlineData(VerificationResult.SkipReason.FeatureOff)]
    [InlineData(VerificationResult.SkipReason.EnrollmentInProgress)]
    [InlineData(VerificationResult.SkipReason.TooShort)]
    [InlineData(VerificationResult.SkipReason.Error)]
    public async Task GateDecision_SkippedVariants_PassTranscript(VerificationResult.SkipReason reason)
    {
        var verifier = new FakeVerifier(new VerificationResult.Skipped(reason));
        var decision = await DiscordVoiceGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);

        Assert.True(decision.PassesTranscript);
        Assert.IsType<VerificationResult.Skipped>(decision.Result);
    }

    [Fact]
    public async Task GateDecision_NotEnrolled_PassesTranscript()
    {
        var verifier = new FakeVerifier(VerificationResult.NotEnrolled);
        var decision = await DiscordVoiceGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);

        Assert.True(decision.PassesTranscript);
    }

    [Fact]
    public async Task GateDecision_VerifierThrows_PassesTranscript()
    {
        var verifier = new ThrowingVerifier();
        var decision = await DiscordVoiceGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);

        // Infrastructure failure is never user-facing — pass through.
        Assert.True(decision.PassesTranscript);
        Assert.Null(decision.Result);
    }

    [Fact]
    public async Task GateDecision_VerifierHangs_TimesOutAndPassesTranscript()
    {
        var verifier = new HangingVerifier();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var decision = await DiscordVoiceGate.EvaluateAsync(verifier, "tenant-a", new short[16000], CancellationToken.None);
        sw.Stop();

        Assert.True(decision.PassesTranscript);
        Assert.Null(decision.Result);
        // Should not wait significantly past the documented timeout.
        Assert.True(sw.Elapsed < DiscordVoiceGate.VerifyTimeout + TimeSpan.FromSeconds(1),
            $"Gate took {sw.ElapsedMilliseconds}ms; timeout was {DiscordVoiceGate.VerifyTimeout.TotalMilliseconds}ms.");
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
            // Block until cancelled — simulates an embedder that has wedged.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new VerificationResult.Accept(1.0f);
        }
    }
}
