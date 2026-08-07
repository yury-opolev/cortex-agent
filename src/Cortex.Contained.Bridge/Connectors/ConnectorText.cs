namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Length-capping helpers for untrusted connector-supplied text.
/// </summary>
public static class ConnectorText
{
    /// <summary>
    /// Truncates <paramref name="value"/> to at most <paramref name="maxLength"/> UTF-16 code
    /// units without splitting a surrogate pair.
    /// </summary>
    /// <param name="value">The untrusted text, which may be null.</param>
    /// <param name="maxLength">Maximum length in UTF-16 code units.</param>
    /// <returns>The original value when it already fits, otherwise a safely truncated copy.</returns>
    /// <remarks>
    /// Slicing a UTF-16 string at a fixed offset can land between the high and low surrogate of a
    /// supplementary character (an emoji, for example). The orphaned high surrogate is not valid
    /// UTF-16 and encodes as U+FFFD, corrupting the value and anything derived from it.
    /// </remarks>
    public static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength || maxLength <= 0)
        {
            return value is not null && maxLength <= 0 ? string.Empty : value;
        }

        var cut = maxLength;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value[..cut];
    }
}
