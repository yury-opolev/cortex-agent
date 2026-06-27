namespace Cortex.Contained.Speech.Tts;

/// <summary>Thresholds the policy must clear to flip a channel's current language.</summary>
/// <param name="MinSwitchChars">Minimum text length to even consider a switch. Below this the decision is "keep".</param>
/// <param name="SwitchConfidence">Top candidate's confidence must be ≥ this to consider a switch.</param>
/// <param name="SwitchMargin">Top candidate's confidence must exceed the current language's confidence by ≥ this margin.</param>
public sealed record LanguageSwitchThresholds(int MinSwitchChars, double SwitchConfidence, double SwitchMargin)
{
    /// <summary>Conservative defaults: 60 chars, 0.80 top confidence, 0.20 margin over current.</summary>
    public static LanguageSwitchThresholds Default { get; } = new(MinSwitchChars: 60, SwitchConfidence: 0.80, SwitchMargin: 0.20);
}
