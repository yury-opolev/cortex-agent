namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Pure policy mapping (current language, detection result) to the new current
/// language: returns the candidate only when the text is long enough AND the
/// candidate's confidence is high enough AND its margin over the current
/// language's confidence is wide enough. Otherwise the current is preserved.
/// </summary>
public static class LanguageSwitchPolicy
{
    /// <summary>
    /// Decide the new current language. Returns either the unchanged
    /// <paramref name="current"/> or the <paramref name="candidate"/>.
    /// </summary>
    public static string Decide(
        string current,
        string candidate,
        double confTop,
        double confCurrent,
        int textLength,
        LanguageSwitchThresholds thresholds)
    {
        if (string.Equals(candidate, current, StringComparison.Ordinal))
        {
            return current;
        }
        if (textLength < thresholds.MinSwitchChars)
        {
            return current;
        }
        if (confTop < thresholds.SwitchConfidence)
        {
            return current;
        }
        if ((confTop - confCurrent) < thresholds.SwitchMargin)
        {
            return current;
        }
        return candidate;
    }

    /// <summary>
    /// Script-first language resolution. The dominant Unicode script of
    /// <paramref name="text"/> decides deterministically when it maps to exactly
    /// one configured language (e.g. Cyrillic → ru). When the script is shared by
    /// several configured languages (Latin → en/da), the statistical confidences
    /// arbitrate via <see cref="Decide"/> with hysteresis. Text with no letters,
    /// or in a script no configured language uses, keeps <paramref name="current"/>.
    /// </summary>
    /// <param name="current">The current sticky language (ISO 639-1).</param>
    /// <param name="text">The text to route.</param>
    /// <param name="confidences">Per-configured-language relative confidences; its keys define the configured set.</param>
    /// <param name="thresholds">Hysteresis thresholds for the same-script case.</param>
    public static string Resolve(
        string current,
        string text,
        IReadOnlyDictionary<string, double> confidences,
        LanguageSwitchThresholds thresholds)
    {
        var dominant = ScriptDetector.DetectDominantScript(text);
        if (dominant == TextScript.None)
        {
            return current;
        }

        var candidates = new List<string>();
        foreach (var code in confidences.Keys)
        {
            if (ScriptDetector.ScriptOf(code) == dominant)
            {
                candidates.Add(code);
            }
        }

        if (candidates.Count == 0)
        {
            return current;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        // Shared script: pick the most-confident candidate, then apply hysteresis.
        var top = current;
        var topConf = 0d;
        foreach (var code in candidates)
        {
            var c = confidences.TryGetValue(code, out var v) ? v : 0d;
            if (c > topConf)
            {
                topConf = c;
                top = code;
            }
        }

        var confCurrent = confidences.TryGetValue(current, out var cc) ? cc : 0d;
        return Decide(current, top, topConf, confCurrent, text.Length, thresholds);
    }
}
