using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class TurnDecisionTests
{
    [Fact]
    public void Enums_HaveExpectedMembers()
    {
        Assert.Equal(0, (int)ConversationPhase.Listening);
        Assert.True(Enum.IsDefined(ConversationPhase.Speaking));
        Assert.True(Enum.IsDefined(TurnEvent.UserSpeechOnset));
        Assert.True(Enum.IsDefined(InterruptClass.Backchannel));
        Assert.True(Enum.IsDefined(TurnAction.CommitInterrupt));
    }

    [Theory]
    [InlineData(ConversationPhase.Listening, TurnAction.Accumulate)]
    [InlineData(ConversationPhase.Committed, TurnAction.CancelCommitAndReabsorb)]
    [InlineData(ConversationPhase.Thinking, TurnAction.CancelGenAndReabsorb)]
    public void Onset_PreSpeechPhases(ConversationPhase phase, TurnAction expected)
    {
        var a = TurnDecision.Decide(phase, TurnEvent.UserSpeechOnset, InterruptClass.None, bargeInEnabled: true);
        Assert.Equal(expected, a);
    }

    [Fact]
    public void Onset_Speaking_BargeInEnabled_StopsPendingClassify()
    {
        var a = TurnDecision.Decide(ConversationPhase.Speaking, TurnEvent.UserSpeechOnset, InterruptClass.None, bargeInEnabled: true);
        Assert.Equal(TurnAction.StopPlaybackPendingClassify, a);
    }

    [Fact]
    public void Onset_Speaking_BargeInDisabled_Ignored()
    {
        var a = TurnDecision.Decide(ConversationPhase.Speaking, TurnEvent.UserSpeechOnset, InterruptClass.None, bargeInEnabled: false);
        Assert.Equal(TurnAction.Ignore, a);
    }

    [Theory]
    [InlineData(InterruptClass.Backchannel, TurnAction.ResumeFromSentenceStart)]
    [InlineData(InterruptClass.Real, TurnAction.CommitInterrupt)]
    [InlineData(InterruptClass.Unsure, TurnAction.CommitInterrupt)] // resolved-unsure defaults to Real
    public void ClassifierResult_DrivesResumeOrCommit(InterruptClass cls, TurnAction expected)
    {
        var a = TurnDecision.Decide(ConversationPhase.Speaking, TurnEvent.ClassifierResult, cls, bargeInEnabled: true);
        Assert.Equal(expected, a);
    }

    [Fact]
    public void UnrelatedEvent_Ignored()
    {
        var a = TurnDecision.Decide(ConversationPhase.Listening, TurnEvent.AgentFinished, InterruptClass.None, bargeInEnabled: true);
        Assert.Equal(TurnAction.Ignore, a);
    }
}
