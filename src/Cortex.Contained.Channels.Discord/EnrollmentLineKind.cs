namespace Cortex.Contained.Channels.Discord;

/// <summary>How an enrollment line should be delivered.</summary>
public enum EnrollmentLineKind
{
    /// <summary>Spoken: milestone intro + the phrase to repeat.</summary>
    SpokenIntro,
    /// <summary>Spoken: the next phrase to repeat.</summary>
    SpokenPhrase,
    /// <summary>Spoken: success confirmation.</summary>
    SpokenDone,
    /// <summary>Text-only (fallback / non-voice surfaces).</summary>
    TextOnly,
}
