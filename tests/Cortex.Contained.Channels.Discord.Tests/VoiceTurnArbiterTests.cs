using Cortex.Contained.Channels.Discord;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Channels.Discord.Tests;

public class VoiceTurnArbiterTests
{
    private static (VoiceTurnArbiter arb, List<string> calls) Build(
        InterruptClass classify = InterruptClass.Real,
        bool bargeInEnabled = true)
    {
        var calls = new List<string>();
        var cb = new VoiceTurnArbiterCallbacks
        {
            StopPlayback = () =>
            {
                calls.Add("stop");
                return Task.FromResult(new PlaybackProgress(["A."], "bee see", 0.5));
            },
            ReenqueueSentence = s => calls.Add($"reenqueue:{s}"),
            CancelGenerationAndReabsorb = r => { calls.Add($"cancelgen:{r}"); return Task.CompletedTask; },
            CommitInterrupt = p => { calls.Add($"commit:{PlaybackTruncation.BuildPlayedText(p)}"); return Task.CompletedTask; },
            Classify = (_, _, _) => Task.FromResult(classify),
        };
        return (new VoiceTurnArbiter(cb, bargeInEnabled, NullLogger.Instance), calls);
    }

    [Fact]
    public async Task SpeakingOnset_RealInterrupt_StopsThenCommits()
    {
        var (arb, calls) = Build(InterruptClass.Real);
        arb.SetPhase(ConversationPhase.Speaking);

        await arb.OnUserSpeechOnsetAsync("bee see brown", pEou: 0.4f, CancellationToken.None);

        Assert.Equal("stop", calls[0]);
        Assert.StartsWith("commit:", calls[1]);
        Assert.Equal(ConversationPhase.Listening, arb.Phase);
    }

    [Fact]
    public async Task SpeakingOnset_Backchannel_StopsThenResumes()
    {
        var (arb, calls) = Build(InterruptClass.Backchannel);
        arb.SetPhase(ConversationPhase.Speaking);

        await arb.OnUserSpeechOnsetAsync("mhm", pEou: 0.01f, CancellationToken.None);

        Assert.Equal("stop", calls[0]);
        Assert.Equal("reenqueue:bee see", calls[1]);
        Assert.Equal(ConversationPhase.Speaking, arb.Phase);
    }

    [Fact]
    public async Task ThinkingOnset_CancelsGeneration()
    {
        var (arb, calls) = Build();
        arb.SetPhase(ConversationPhase.Thinking);

        await arb.OnUserSpeechOnsetAsync("wait no", pEou: 0.3f, CancellationToken.None);

        Assert.Contains("cancelgen:thinking", calls);
        Assert.Equal(ConversationPhase.Listening, arb.Phase);
    }

    [Fact]
    public async Task SpeakingOnset_BargeInDisabled_NoStop()
    {
        var (arb, calls) = Build(bargeInEnabled: false);
        arb.SetPhase(ConversationPhase.Speaking);

        await arb.OnUserSpeechOnsetAsync("hello", pEou: 0.3f, CancellationToken.None);

        Assert.Empty(calls);
        Assert.Equal(ConversationPhase.Speaking, arb.Phase);
    }
}
