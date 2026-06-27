namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Heuristic-first interrupt classifier. Pure decision rules; an optional LLM
/// delegate resolves the Unsure band. On any LLM error/timeout the verdict is
/// Real (give the user the floor — never talk over them on uncertainty).
/// </summary>
internal sealed class HeuristicInterruptClassifier : IInterruptClassifier
{
    private const float LowPEou = 0.05f;
    private readonly Func<string, CancellationToken, Task<InterruptClass>>? llm;

    public HeuristicInterruptClassifier(
        Func<string, CancellationToken, Task<InterruptClass>>? llm)
    {
        this.llm = llm;
    }

    public async Task<InterruptClass> ClassifyAsync(
        string partialTranscript, float pEou, CancellationToken ct)
    {
        var text = (partialTranscript ?? string.Empty).Trim();
        var words = BackchannelLexicon.WordCount(text);

        if (words >= 3 || pEou >= 0.30f)
        {
            return InterruptClass.Real;
        }

        if (words > 0
            && words <= 2
            && BackchannelLexicon.IsBackchannelOnly(text)
            && pEou <= LowPEou)
        {
            return InterruptClass.Backchannel;
        }

        // Unsure band.
        if (this.llm is null)
        {
            return InterruptClass.Real;
        }

        try
        {
            var verdict = await this.llm(text, ct).ConfigureAwait(false);
            return verdict == InterruptClass.Backchannel
                ? InterruptClass.Backchannel
                : InterruptClass.Real;
        }
        catch (Exception)
        {
            return InterruptClass.Real;
        }
    }
}
