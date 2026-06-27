using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class WizardEntryDecisionTests
{
    [Fact]
    public void EnrollFailed_None()
        => Assert.Equal(WizardEntryAction.None, WizardEntryDecision.Decide(enrollStarted: false, EnrollGateDecision.Proceed));

    [Fact]
    public void Started_InVoice_StartNow()
        => Assert.Equal(WizardEntryAction.StartNow, WizardEntryDecision.Decide(true, EnrollGateDecision.Proceed));

    [Fact]
    public void Started_NotInVoice_RingThenStart()
        => Assert.Equal(WizardEntryAction.RingThenStart, WizardEntryDecision.Decide(true, EnrollGateDecision.RingAndProceed));
}
