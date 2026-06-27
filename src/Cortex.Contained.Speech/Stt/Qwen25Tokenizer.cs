// Hand-rolled byte-level BPE tokenizer compatible with the Qwen2.5 family.
//
// Required by LiveKitTurnDetector — the multilingual turn-detector ONNX model
// expects token ids produced by Qwen2.5's tokenizer, which Microsoft.ML.Tokenizers
// doesn't ship support for as of 2.0.0 (special-token mapping and byte-level merges
// both diverged). We port the well-specified GPT-2 byte-level BPE algorithm
// directly and validate byte-identical output against @huggingface/transformers
// across 82 fixtures spanning specials, contractions, RTL scripts, CJK, Thai,
// Devanagari, ZWJ emoji and NFC/NFD normalization.
//
// Matches the configuration in livekit/turn-detector's tokenizer.json:
//   normalizer   : NFC
//   pre_tokenizer: Sequence(Split(regex, Isolated), ByteLevel)
//   model        : BPE (vocab + merges, no byte_fallback, no unk_token)
//
// Algorithm, for each Encode(text):
//   1. Scan for added/special tokens and split text into interleaved
//      (regular, special) segments. Special segments emit their id directly.
//   2. For each regular segment:
//        a. NFC normalize.
//        b. Split with the Qwen pre-tokenizer regex (Isolated: matches are pre-tokens).
//        c. For each pre-token, encode to UTF-8 bytes, map each byte through the
//           GPT-2 byte-to-unicode table, apply BPE merges, then vocab-lookup.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cortex.Contained.Speech.Stt;

/// <summary>
/// Byte-level BPE tokenizer compatible with Qwen2.5 / GPT-2 lineage. Loads
/// directly from a HuggingFace <c>tokenizer.json</c> and produces token ids
/// byte-identical to the <c>@huggingface/transformers</c> tokenizer used during
/// the turn-detector's training.
/// </summary>
public sealed class Qwen25Tokenizer
{
    // Qwen2.5 / GPT-2 style pre-tokenizer pattern. Taken verbatim from
    // tokenizer.json "pre_tokenizer".pretokenizers[0].pattern.Regex.
    private static readonly Regex PreTokenizeRegex = new(
        @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, int> vocab;
    private readonly Dictionary<(string, string), int> bpeRanks;
    private readonly (string content, int id)[] specialTokensOrdered;
    private readonly Regex? specialTokensRegex;

    // GPT-2 byte-to-unicode mapping: each of the 256 bytes → a printable unicode char.
    private readonly char[] byteEncoder;

    /// <summary>Number of entries in the loaded BPE vocabulary (excluding added_tokens).</summary>
    public int VocabSize => this.vocab.Count;

    public Qwen25Tokenizer(
        Dictionary<string, int> vocab,
        IReadOnlyList<(string a, string b)> merges,
        IReadOnlyList<(string content, int id)> specialTokens)
    {
        ArgumentNullException.ThrowIfNull(vocab);
        ArgumentNullException.ThrowIfNull(merges);
        ArgumentNullException.ThrowIfNull(specialTokens);

        this.vocab = new Dictionary<string, int>(vocab, StringComparer.Ordinal);

        this.bpeRanks = new Dictionary<(string, string), int>(capacity: merges.Count);
        for (var i = 0; i < merges.Count; i++)
        {
            this.bpeRanks[merges[i]] = i;
        }

        // Longest-first — a multi-character special token must win over any shorter prefix.
        this.specialTokensOrdered = specialTokens
            .OrderByDescending(t => t.content.Length)
            .ThenBy(t => t.content, StringComparer.Ordinal)
            .ToArray();

        if (this.specialTokensOrdered.Length > 0)
        {
            var pattern = string.Join("|", this.specialTokensOrdered.Select(t => Regex.Escape(t.content)));
            this.specialTokensRegex = new Regex(pattern, RegexOptions.Compiled);
        }

        this.byteEncoder = BuildByteEncoder();
    }

    /// <summary>
    /// Loads the tokenizer by parsing a HuggingFace <c>tokenizer.json</c> file.
    /// Extracts the BPE vocab, merges list, and added/special tokens.
    /// </summary>
    public static Qwen25Tokenizer LoadFromHuggingFaceTokenizerJson(string tokenizerJsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerJsonPath);

        using var stream = File.OpenRead(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var modelElt = root.GetProperty("model");

        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in modelElt.GetProperty("vocab").EnumerateObject())
        {
            vocab[kv.Name] = kv.Value.GetInt32();
        }

        var merges = new List<(string, string)>();
        foreach (var m in modelElt.GetProperty("merges").EnumerateArray())
        {
            if (m.ValueKind == JsonValueKind.String)
            {
                var parts = m.GetString()!.Split(' ', 2);
                if (parts.Length == 2)
                {
                    merges.Add((parts[0], parts[1]));
                }
            }
            else if (m.ValueKind == JsonValueKind.Array && m.GetArrayLength() == 2)
            {
                merges.Add((m[0].GetString()!, m[1].GetString()!));
            }
        }

        var specials = new List<(string, int)>();
        if (root.TryGetProperty("added_tokens", out var addedTokens))
        {
            foreach (var at in addedTokens.EnumerateArray())
            {
                specials.Add((at.GetProperty("content").GetString()!, at.GetProperty("id").GetInt32()));
            }
        }

        return new Qwen25Tokenizer(vocab, merges, specials);
    }

    /// <summary>
    /// Tokenize <paramref name="text"/> to a list of vocabulary ids, honoring
    /// added/special tokens. No BOS/EOS auto-injected — the caller assembles
    /// the chat template explicitly (mirrors transformers with
    /// <c>add_special_tokens=False</c>).
    /// </summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        if (string.IsNullOrEmpty(text))
        {
            return ids;
        }

        if (this.specialTokensRegex is null)
        {
            EncodeRegular(text, ids);
            return ids;
        }

        var cursor = 0;
        foreach (Match m in this.specialTokensRegex.Matches(text))
        {
            if (m.Index > cursor)
            {
                EncodeRegular(text[cursor..m.Index], ids);
            }

            var matched = m.Value;
            foreach (var (content, id) in this.specialTokensOrdered)
            {
                if (content == matched)
                {
                    ids.Add(id);
                    break;
                }
            }

            cursor = m.Index + m.Length;
        }

        if (cursor < text.Length)
        {
            EncodeRegular(text[cursor..], ids);
        }

        return ids;
    }

    private void EncodeRegular(string text, List<int> output)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var normalized = text.Normalize(NormalizationForm.FormC);

        foreach (Match m in PreTokenizeRegex.Matches(normalized))
        {
            if (m.Length == 0)
            {
                continue;
            }

            var preToken = m.Value;
            var utf8 = Encoding.UTF8.GetBytes(preToken);
            var sb = new StringBuilder(utf8.Length);
            for (var i = 0; i < utf8.Length; i++)
            {
                sb.Append(this.byteEncoder[utf8[i]]);
            }
            var byteEncoded = sb.ToString();

            foreach (var piece in Bpe(byteEncoded))
            {
                if (this.vocab.TryGetValue(piece, out var id))
                {
                    output.Add(id);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Token '{piece}' (from pre-token '{preToken}') not in vocabulary.");
                }
            }
        }
    }

    /// <summary>
    /// Apply BPE merges to a byte-encoded pre-token. Standard GPT-2 BPE: greedily
    /// merge the pair with the lowest rank until no known pair remains.
    /// </summary>
    private List<string> Bpe(string byteEncodedWord)
    {
        var word = new List<string>(byteEncodedWord.Length);
        foreach (var ch in byteEncodedWord)
        {
            word.Add(ch.ToString());
        }

        if (word.Count < 2)
        {
            return word;
        }

        while (true)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;
            for (var i = 0; i < word.Count - 1; i++)
            {
                if (this.bpeRanks.TryGetValue((word[i], word[i + 1]), out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    bestIndex = i;
                }
            }

            if (bestIndex == -1)
            {
                break;
            }

            var pairA = word[bestIndex];
            var pairB = word[bestIndex + 1];
            var merged = new List<string>(word.Count);
            var i2 = 0;
            while (i2 < word.Count)
            {
                if (i2 < word.Count - 1 && word[i2] == pairA && word[i2 + 1] == pairB)
                {
                    merged.Add(pairA + pairB);
                    i2 += 2;
                }
                else
                {
                    merged.Add(word[i2]);
                    i2++;
                }
            }
            word = merged;

            if (word.Count == 1)
            {
                break;
            }
        }

        return word;
    }

    /// <summary>
    /// Builds the GPT-2 / Qwen byte-to-unicode mapping. Printable ASCII stays as-is,
    /// non-printable / non-Latin1-printable bytes are remapped into U+0100+N to
    /// preserve round-trippability and keep all tokens as safe printable chars.
    /// </summary>
    private static char[] BuildByteEncoder()
    {
        // Printable ranges from the canonical GPT-2 code:
        //   range(ord('!'), ord('~')+1)    → 33..126
        //   range(ord('¡'), ord('¬')+1)    → 0xA1..0xAC
        //   range(ord('®'), ord('ÿ')+1)    → 0xAE..0xFF
        var isPrintable = new bool[256];
        for (var b = '!'; b <= '~'; b++) { isPrintable[b] = true; }
        for (var b = 0xA1; b <= 0xAC; b++) { isPrintable[b] = true; }
        for (var b = 0xAE; b <= 0xFF; b++) { isPrintable[b] = true; }

        var encoder = new char[256];
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            if (isPrintable[b])
            {
                encoder[b] = (char)b;
            }
            else
            {
                encoder[b] = (char)(256 + n);
                n++;
            }
        }
        return encoder;
    }
}
