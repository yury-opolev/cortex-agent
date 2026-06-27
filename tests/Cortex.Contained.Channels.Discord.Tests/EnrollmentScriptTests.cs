using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class EnrollmentScriptTests
{
    [Fact]
    public void Enrolling_FirstSample_GivesSpokenIntroPlusPhrase1()
    {
        var line = EnrollmentScript.LineFor("Enrolling", captured: 0, required: 3);
        Assert.Equal(EnrollmentLineKind.SpokenIntro, line.Kind);
        Assert.Contains(EnrollmentScript.EnrollPhrases[0], line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrolling_MidCapture_GivesNextPhrase()
    {
        var line = EnrollmentScript.LineFor("Enrolling", captured: 1, required: 3);
        Assert.Equal(EnrollmentLineKind.SpokenPhrase, line.Kind);
        Assert.Contains(EnrollmentScript.EnrollPhrases[1], line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirming_FirstSample_GivesConfirmIntroPlusPhrase()
    {
        var line = EnrollmentScript.LineFor("Confirming", captured: 0, required: 2);
        Assert.Equal(EnrollmentLineKind.SpokenIntro, line.Kind);
        Assert.Contains(EnrollmentScript.ConfirmPhrases[0], line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrolled_GivesSpokenDone()
    {
        var line = EnrollmentScript.LineFor("Enrolled", 0, 0);
        Assert.Equal(EnrollmentLineKind.SpokenDone, line.Kind);
        Assert.Contains("enrolled", line.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_GivesRetryText()
    {
        var line = EnrollmentScript.LineFor("Unknown", 0, 0);
        Assert.Contains("/voice-id enroll", line.Text, StringComparison.Ordinal);
    }
}
