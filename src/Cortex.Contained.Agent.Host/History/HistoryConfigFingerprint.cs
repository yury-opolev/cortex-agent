using System.Globalization;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Identifies everything that changes the content of an index generation. An index whose
/// stored fingerprint differs from the current one is rebuilt.
/// </summary>
public static class HistoryConfigFingerprint
{
    /// <summary>Version of the generation-file schema.</summary>
    public const int SchemaVersion = 1;

    /// <summary>FTS5 tokenizer and prefix configuration shared by the index and the re-matcher.</summary>
    public const string TokenizerSpec = "unicode61 remove_diacritics 2";

    /// <summary>FTS5 prefix index configuration.</summary>
    public const string PrefixSpec = "3 4";

    /// <summary>Computes the fingerprint for <paramref name="options"/>.</summary>
    public static string Compute(HistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"schema={SchemaVersion};segmenter={HistorySegmenter.Version};folder={TextFolder.Version};tokenizer={TokenizerSpec};prefix={PrefixSpec};tz={options.TimeZone};idle={options.IdleGapMinutes};words={options.MaxSegmentWords}");
    }
}
