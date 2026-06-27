namespace Cortex.Contained.Speech;

/// <summary>
/// Voice gender preference for TTS synthesis.
/// Determines whether male or female voices are used for each language.
/// </summary>
public enum VoiceGender
{
    /// <summary>Use female voice variants.</summary>
    Female,

    /// <summary>Use male voice variants.</summary>
    Male,
}
