using System.Globalization;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Identifies everything that changes the content of an index generation. An index whose
/// stored fingerprint differs from the current one is rebuilt.
/// </summary>
public static class HistoryConfigFingerprint
{
    /// <summary>Version of the generation-file schema.</summary>
    public const int SchemaVersion = 4;

    /// <summary>FTS5 tokenizer and prefix configuration shared by the index and the re-matcher.</summary>
    public const string TokenizerSpec = "unicode61 remove_diacritics 2";

    /// <summary>FTS5 prefix index configuration.</summary>
    public const string PrefixSpec = "3 4";

    /// <summary>FTS5 tokenizer of the trigram ablation table.</summary>
    public const string TrigramTokenizerSpec = "trigram remove_diacritics 1";

    /// <summary>Computes the fingerprint for <paramref name="options"/> (no embedder).</summary>
    public static string Compute(HistoryOptions options) => Compute(options, embedderFingerprint: null);

    /// <summary>
    /// Computes the fingerprint for <paramref name="options"/>. With dense retrieval enabled it also
    /// covers the chunking parameters and <paramref name="embedderFingerprint"/> (model, dimensions,
    /// context), so a different embedder rebuilds the index instead of mixing vector spaces.
    /// </summary>
    public static string Compute(HistoryOptions options, string? embedderFingerprint)
    {
        ArgumentNullException.ThrowIfNull(options);
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"schema={SchemaVersion};segmenter={HistorySegmenter.Version};folder={TextFolder.Version};tokenizer={TokenizerSpec};prefix={PrefixSpec};tz={options.EffectiveTimeZoneId};idle={options.IdleGapMinutes};words={options.MaxSegmentWords};kw={options.KeywordVariant}");
        if (options.KeywordVariant == HistoryKeywordVariant.Stemmed)
        {
            text += string.Create(CultureInfo.InvariantCulture, $";stemmer={HistoryStemmer.Version}");
        }

        if (options.Dense.Enabled)
        {
            var dense = options.Dense;
            text += string.Create(
                CultureInfo.InvariantCulture,
                $";chunker={HistoryChunker.Version};chunk={dense.ChunkWords}/{dense.ChunkOverlapWords}/{dense.MaxChunkChars};embedder={embedderFingerprint ?? "none"}");
        }

        return text;
    }
}
