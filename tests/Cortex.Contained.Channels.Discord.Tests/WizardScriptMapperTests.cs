using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class WizardScriptMapperTests
{
    [Fact]
    public void Enrolling_Captured0_IntroWithFirstEnrollPhrase()
    {
        var line = WizardScriptMapper.LineFor(WizardPhase.Enrolling, capturedInPhase: 0, samplesRequired: 3, matchesRequired: 2);
        Assert.Equal(EnrollmentLineKind.SpokenIntro, line.Kind);
        Assert.Contains(EnrollmentScript.EnrollPhrases[0], line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrolling_Captured1_SecondEnrollPhrase()
    {
        var line = WizardScriptMapper.LineFor(WizardPhase.Enrolling, 1, 3, 2);
        Assert.Equal(EnrollmentLineKind.SpokenPhrase, line.Kind);
        Assert.Contains(EnrollmentScript.EnrollPhrases[1], line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirming_Captured0_ConfirmIntro()
    {
        var line = WizardScriptMapper.LineFor(WizardPhase.Confirming, 0, 3, 2);
        Assert.Equal(EnrollmentLineKind.SpokenIntro, line.Kind);
        Assert.Contains(EnrollmentScript.ConfirmPhrases[0], line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirming_Captured1_SecondConfirmPhrase()
    {
        var line = WizardScriptMapper.LineFor(WizardPhase.Confirming, 1, 3, 2);
        Assert.Equal(EnrollmentLineKind.SpokenPhrase, line.Kind);
        Assert.Contains(EnrollmentScript.ConfirmPhrases[1], line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_SpokenDone()
    {
        var line = WizardScriptMapper.LineFor(WizardPhase.Complete, 0, 3, 2);
        Assert.Equal(EnrollmentLineKind.SpokenDone, line.Kind);
        Assert.Contains("enrolled", line.Text, StringComparison.OrdinalIgnoreCase);
    }
}
