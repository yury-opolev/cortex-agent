using System.Globalization;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Shared helpers for round-tripping <see cref="DateTimeOffset"/> values through SQLite TEXT columns.
/// All stores that persist timestamps as ISO-8601 "O" strings in UTC use these methods so the
/// format is defined exactly once and remains consistent across migrations.
/// </summary>
public static class SqliteDateTimeText
{
    /// <summary>
    /// Formats a <see cref="DateTimeOffset"/> as a UTC ISO-8601 round-trip string
    /// (<c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>) suitable for storage in a SQLite TEXT column.
    /// </summary>
    /// <param name="value">The value to format. The UTC instant is used regardless of the original offset.</param>
    /// <returns>An ISO-8601 "O" string in UTC.</returns>
    public static string Format(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a UTC ISO-8601 string previously written by <see cref="Format"/> back to a
    /// <see cref="DateTimeOffset"/> with <see cref="DateTimeOffset.Offset"/> == <see cref="TimeSpan.Zero"/>.
    /// </summary>
    /// <param name="text">The stored string.</param>
    /// <returns>The parsed value.</returns>
    public static DateTimeOffset Parse(string text)
        => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// Parses a nullable UTC ISO-8601 string. Returns <c>null</c> when <paramref name="text"/> is <c>null</c>.
    /// </summary>
    /// <param name="text">The stored string, or <c>null</c>.</param>
    /// <returns>The parsed value, or <c>null</c>.</returns>
    public static DateTimeOffset? ParseNullable(string? text)
        => text is null ? null : Parse(text);
}
