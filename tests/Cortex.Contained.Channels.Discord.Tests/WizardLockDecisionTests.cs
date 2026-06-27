using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class WizardLockDecisionTests
{
    [Fact]
    public void WizardActive_RoutesToCapture()
        => Assert.Equal(UtteranceRoute.EnrollmentCapture, WizardLockDecision.Route(wizardActive: true));

    [Fact]
    public void WizardInactive_RoutesToNormal()
        => Assert.Equal(UtteranceRoute.NormalDispatch, WizardLockDecision.Route(wizardActive: false));
}
