namespace Cortex.Contained.Speech.Tests.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;

public sealed class VerificationResultTests
{
    [Fact]
    public void Accept_CarriesScore_AndIsTreatedAsPassThrough()
    {
        var result = new VerificationResult.Accept(0.71f);
        Assert.Equal(0.71f, result.Score);
        Assert.True(result.PassesTranscript);
    }

    [Fact]
    public void Reject_CarriesScore_AndDropsTranscript()
    {
        var result = new VerificationResult.Reject(0.21f);
        Assert.Equal(0.21f, result.Score);
        Assert.False(result.PassesTranscript);
    }

    [Fact]
    public void NotEnrolled_PassesTranscript()
    {
        var result = VerificationResult.NotEnrolled;
        Assert.True(result.PassesTranscript);
    }

    [Theory]
    [InlineData(VerificationResult.SkipReason.FeatureOff)]
    [InlineData(VerificationResult.SkipReason.EnrollmentInProgress)]
    [InlineData(VerificationResult.SkipReason.TooShort)]
    [InlineData(VerificationResult.SkipReason.Error)]
    public void Skipped_VariantsPassTranscript(VerificationResult.SkipReason reason)
    {
        var result = new VerificationResult.Skipped(reason);
        Assert.True(result.PassesTranscript);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void OnlyRejectFailsTranscriptPassage()
    {
        VerificationResult[] passes =
        [
            new VerificationResult.Accept(0.9f),
            VerificationResult.NotEnrolled,
            new VerificationResult.Skipped(VerificationResult.SkipReason.FeatureOff),
            new VerificationResult.Skipped(VerificationResult.SkipReason.EnrollmentInProgress),
            new VerificationResult.Skipped(VerificationResult.SkipReason.TooShort),
            new VerificationResult.Skipped(VerificationResult.SkipReason.Error),
        ];
        Assert.All(passes, r => Assert.True(r.PassesTranscript));

        VerificationResult fail = new VerificationResult.Reject(0.1f);
        Assert.False(fail.PassesTranscript);
    }
}
