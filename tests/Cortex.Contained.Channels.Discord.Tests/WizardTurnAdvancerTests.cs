using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Speech.SpeakerId;

namespace Cortex.Contained.Channels.Discord.Tests;

public class WizardTurnAdvancerTests
{
    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public string ModelId => "test-model";

        public int EmbeddingDim => 4;

        public ValueTask<float[]> EmbedAsync(ReadOnlyMemory<short> pcm, CancellationToken ct)
        {
            if (pcm.Span.Length == 0)
            {
                return ValueTask.FromResult(Array.Empty<float>());
            }

            float[] v = pcm.Span[0] == 1 ? [1f, 0f, 0f, 0f] : [0f, 1f, 0f, 0f];
            return ValueTask.FromResult(SpeakerEmbeddingMath.L2Normalise(v));
        }
    }

    private static short[] Owner() => [1, 1, 1];

    [Fact]
    public async Task FullRun_SpeaksPromptsThenSubmitsVoiceprintAndSpeaksDone()
    {
        var session = new WizardEnrollmentSession(new FakeEmbedder(), 3, 2, 0.5f);
        var spoken = new List<string>();
        float[]? submitted = null;
        string? submittedModel = null;
        Func<string, Task> speak = t => { spoken.Add(t); return Task.CompletedTask; };
        Func<float[], string, Task> submit = (vp, m) => { submitted = vp; submittedModel = m; return Task.CompletedTask; };

        // 3 enroll + 2 confirm utterances
        WizardAdvanceResult last = WizardAdvanceResult.InProgress;
        for (var i = 0; i < 5; i++)
        {
            last = await WizardTurnAdvancer.AdvanceAsync(session, Owner(), speak, submit, 3, 2, default);
        }

        Assert.Equal(WizardAdvanceResult.Completed, last);
        Assert.NotNull(submitted);
        Assert.Equal("test-model", submittedModel);
        Assert.Equal(5, spoken.Count); // one spoken line per utterance (incl. the final "done")
        Assert.Contains("enrolled", spoken[^1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitHappensBeforeDoneIsSpoken()
    {
        var session = new WizardEnrollmentSession(new FakeEmbedder(), 1, 1, 0.5f);
        var order = new List<string>();
        Func<string, Task> speak = t => { order.Add("speak"); return Task.CompletedTask; };
        Func<float[], string, Task> submit = (vp, m) => { order.Add("submit"); return Task.CompletedTask; };

        await WizardTurnAdvancer.AdvanceAsync(session, Owner(), speak, submit, 1, 1, default); // enroll -> Confirming
        var r = await WizardTurnAdvancer.AdvanceAsync(session, Owner(), speak, submit, 1, 1, default); // confirm -> Complete

        Assert.Equal(WizardAdvanceResult.Completed, r);
        Assert.Equal("submit", order[^2]);
        Assert.Equal("speak", order[^1]);
    }
}
