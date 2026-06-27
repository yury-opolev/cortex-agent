namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Deterministic script for the voice-ID enrollment wizard. Pure data plus a
/// single selector: given (state, captured count) return the line to deliver.
/// Speaker-ID is acoustic, so the wording does not affect accuracy — the phrases
/// exist only to give the user something natural to repeat.
/// </summary>
public static class EnrollmentScript
{
    /// <summary>Phrases read aloud during the Enrolling phase (user repeats each).</summary>
    public static readonly string[] EnrollPhrases =
    [
        "The harbor lights look calm this evening.",
        "I usually start my mornings with a coffee.",
        "Seven small boats crossed the quiet bay.",
    ];

    /// <summary>Phrases read aloud during the Confirming phase.</summary>
    public static readonly string[] ConfirmPhrases =
    [
        "Just a couple more lines to be sure.",
        "Thanks for your patience with this.",
    ];

    /// <summary>Select the line to deliver for the given state and progress.</summary>
    public static EnrollmentLine LineFor(string stateName, int captured, int required)
    {
        switch (stateName)
        {
            case "Enrolling" when captured == 0:
                return new EnrollmentLine(
                    $"Let's set up voice recognition. I'll say a short phrase — just repeat it back. Here's the first: {EnrollPhrases[0]}",
                    EnrollmentLineKind.SpokenIntro);
            case "Enrolling":
                var idx = Math.Min(captured, EnrollPhrases.Length - 1);
                return new EnrollmentLine(EnrollPhrases[idx], EnrollmentLineKind.SpokenPhrase);
            case "Confirming" when captured == 0:
                return new EnrollmentLine(
                    $"Great. Just a couple more to confirm. Repeat: {ConfirmPhrases[0]}",
                    EnrollmentLineKind.SpokenIntro);
            case "Confirming":
                var cidx = Math.Min(captured, ConfirmPhrases.Length - 1);
                return new EnrollmentLine(ConfirmPhrases[cidx], EnrollmentLineKind.SpokenPhrase);
            case "Enrolled":
                return new EnrollmentLine(
                    "Perfect — you're enrolled. From now on I'll only respond to your voice.",
                    EnrollmentLineKind.SpokenDone);
            case "Declined":
                return new EnrollmentLine("Voice enrollment cancelled.", EnrollmentLineKind.TextOnly);
            default:
                return new EnrollmentLine(
                    "I didn't catch enough to enroll your voice — run `/voice-id enroll` to try again.",
                    EnrollmentLineKind.TextOnly);
        }
    }
}
