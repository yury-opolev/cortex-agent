namespace Cortex.Contained.Speech.Tests.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;

public sealed class VoiceEnrollmentStateTests
{
    [Fact]
    public void Enum_HasExpectedMembers()
    {
        Assert.Equal(0, (int)VoiceEnrollmentState.Unknown);
        Assert.Equal(1, (int)VoiceEnrollmentState.Declined);
        Assert.Equal(2, (int)VoiceEnrollmentState.Enrolling);
        Assert.Equal(3, (int)VoiceEnrollmentState.Confirming);
        Assert.Equal(4, (int)VoiceEnrollmentState.PendingReenroll);
        Assert.Equal(5, (int)VoiceEnrollmentState.Enrolled);
    }
}
