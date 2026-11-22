using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Opaque <c>history_search</c> cursors. A cursor is bound to the query, filters and index
/// generation; any change makes it stale (§12). It is deliberately not bound to the checkpoint,
/// which advances constantly in live use: time order resumes from a <c>(Ts, Id)</c> keyset that stays
/// valid across checkpoints, and relevance order may repeat or skip a hit when the index is
/// re-projected between pages.
/// </summary>
public static class HistoryCursorCodec
{
    /// <summary>
    /// The binding of <paramref name="request"/> on an index generation. <paramref name="rerank"/> (the
    /// effective rerank budget's label) is part of the binding only when reranking applies, so cursors of
    /// searches without reranking are unchanged.
    /// </summary>
    public static string Binding(HistorySearchRequest request, long? generation, string? rerank = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = string.Join(
            '\u001f',
            request.Query.Trim(),
            request.Order.ToString(),
            request.Channel ?? string.Empty,
            request.Since?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.Until?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.IncludeInternal ? "1" : "0",
            request.IncludeEchoes ? "1" : "0",
            request.Limit.ToString(CultureInfo.InvariantCulture),
            generation?.ToString(CultureInfo.InvariantCulture) ?? "none",
            request.Mode.ToString());
        if (rerank is not null)
        {
            text += "\u001frerank=" + rerank;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>Encodes a cursor.</summary>
    public static string Encode(string binding, HistoryCursorPosition position)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new CursorPayload(binding, position.IndexOffset, position.TailSkip, position.AfterTs, position.AfterId, position.RerankHead?.ToArray()));
        return Base64Url.EncodeToString(payload);
    }

    /// <summary>Decodes a cursor; false when it is malformed.</summary>
    public static bool TryDecode(string cursor, out string binding, out HistoryCursorPosition position)
    {
        binding = string.Empty;
        position = HistoryCursorPosition.Start;
        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(Base64Url.DecodeFromChars(cursor));
            if (payload is null || payload.B is null)
            {
                return false;
            }

            binding = payload.B;
            position = new HistoryCursorPosition(payload.O, payload.T, payload.Ts, payload.Id, payload.R);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record CursorPayload(
        string? B, int O, int T, string? Ts, long Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long[]? R = null);
}
