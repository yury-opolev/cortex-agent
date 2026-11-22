using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>Chunking parameters derived from <see cref="HistoryDenseOptions"/>.</summary>
public sealed record HistoryChunkSettings(int ChunkWords, int OverlapWords, int MaxChars)
{
    /// <summary>Builds settings from options.</summary>
    public static HistoryChunkSettings From(HistoryDenseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HistoryChunkSettings(options.ChunkWords, options.ChunkOverlapWords, options.MaxChunkChars);
    }
}

/// <summary>
/// One embedding unit of a segment: a run of consecutive transcript words. The text itself is never
/// stored in the index (it is recomputed from the source); <see cref="TextHash"/> is its revision.
/// </summary>
/// <param name="Ordinal">Position within the segment, from 0.</param>
/// <param name="MsgIds">Member <c>Messages.Id</c>s in transcript order (exact membership).</param>
/// <param name="Words">Whitespace words in the chunk text.</param>
/// <param name="AllInternal">True when every member row is Internal (excluded unless <c>includeInternal</c>).</param>
/// <param name="TextHash">SHA-256 (hex) of the chunk text.</param>
public sealed record HistoryChunk(int Ordinal, IReadOnlyList<long> MsgIds, int Words, bool AllInternal, string TextHash)
{
    /// <summary>First member id.</summary>
    public long FirstMsgId => this.MsgIds[0];

    /// <summary>Last member id.</summary>
    public long LastMsgId => this.MsgIds[^1];
}

/// <summary>The deterministic transcript line of a stored row (what dense retrieval embeds).</summary>
public static class HistoryTranscript
{
    /// <summary><c>label: content</c>, plus <c>[tools: a, b]</c> when the row called tools. No timestamps.</summary>
    public static string Line(MessageRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var toolNames = ToolCallSummary.ParseJson(row.ToolCalls)
            .Where(e => !ToolCallSummary.IsRecallTurnMarker(e))
            .Select(e => e.Name)
            .ToList();
        var line = HistoryLabels.Text(HistoryLabels.For(row)) + ": " + HistoryRowText.DisplayContent(row);
        return toolNames.Count == 0 ? line : line + " [tools: " + string.Join(", ", toolNames) + "]";
    }
}

/// <summary>
/// Splits a segment's transcript into ~<see cref="HistoryChunkSettings.ChunkWords"/>-word chunks with
/// overlap (§4.2). Pure and deterministic: the same rows always give the same chunks and hashes, so
/// incremental projection equals a rebuild and unchanged chunks keep their vectors.
/// </summary>
public static class HistoryChunker
{
    /// <summary>Chunker version; bump on any behaviour change (part of the index fingerprint).</summary>
    public const int Version = 1;

    /// <summary>Chunks <paramref name="rows"/> (already in <c>(Timestamp, Id)</c> order) with their texts.</summary>
    public static IReadOnlyList<(HistoryChunk Chunk, string Text)> Chunk(IReadOnlyList<MessageRecord> rows, HistoryChunkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(settings);
        var units = Units(rows, settings);
        var chunks = new List<(HistoryChunk, string)>();
        var start = 0;
        while (start < units.Count)
        {
            var end = start;
            var chars = 0;
            while (end < units.Count && end - start < settings.ChunkWords && chars + units[end].Word.Length + 1 <= settings.MaxChars)
            {
                chars += units[end].Word.Length + 1;
                end++;
            }

            end = Math.Max(end, start + 1);
            if (end < units.Count)
            {
                // Prefer ending at a line (message) boundary in the second half of the window.
                for (var j = end; j > start + (settings.ChunkWords / 2); j--)
                {
                    if (units[j].LineStart)
                    {
                        end = j;
                        break;
                    }
                }
            }

            chunks.Add(Build(chunks.Count, units, start, end));
            if (end >= units.Count)
            {
                break;
            }

            start = Math.Max(start + 1, end - settings.OverlapWords);
        }

        return chunks;
    }

    /// <summary>SHA-256 (lower-case hex) of a chunk text.</summary>
    public static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static List<Unit> Units(IReadOnlyList<MessageRecord> rows, HistoryChunkSettings settings)
    {
        var maxWordChars = Math.Max(16, settings.MaxChars / 8);
        var units = new List<Unit>();
        foreach (var row in rows)
        {
            var internalRow = row.Category == MessageCategory.Internal;
            var first = true;
            foreach (var word in HistoryTranscript.Line(row).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                for (var offset = 0; offset < word.Length; offset += maxWordChars)
                {
                    units.Add(new Unit(row.Id, word.Substring(offset, Math.Min(maxWordChars, word.Length - offset)), first, internalRow));
                    first = false;
                }
            }
        }

        return units;
    }

    private static (HistoryChunk, string) Build(int ordinal, List<Unit> units, int start, int end)
    {
        var text = new StringBuilder();
        var ids = new List<long>();
        for (var i = start; i < end; i++)
        {
            if (i > start)
            {
                text.Append(units[i].LineStart ? '\n' : ' ');
            }

            text.Append(units[i].Word);
            if (ids.Count == 0 || ids[^1] != units[i].MsgId)
            {
                ids.Add(units[i].MsgId);
            }
        }

        var body = text.ToString();
        var allInternal = true;
        for (var i = start; i < end; i++)
        {
            allInternal &= units[i].Internal;
        }

        return (new HistoryChunk(ordinal, ids, end - start, allInternal, Hash(body)), body);
    }

    private readonly record struct Unit(long MsgId, string Word, bool LineStart, bool Internal);
}
