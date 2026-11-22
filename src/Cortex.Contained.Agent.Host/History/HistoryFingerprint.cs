using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// SHA-256 over the canonical encoding of every member row's
/// <c>(Id, Timestamp, Category, Role, Content, ToolCalls)</c> (§10), each field length-prefixed.
/// Any edit — including a same-length edit — changes it.
/// </summary>
public static class HistoryFingerprint
{
    /// <summary>Computes the fingerprint of <paramref name="rows"/> in the given order.</summary>
    public static string Compute(IEnumerable<MessageRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rows)
        {
            Append(hash, row.Id.ToString(CultureInfo.InvariantCulture));
            Append(hash, SqliteDateTimeText.Format(row.Timestamp));
            Append(hash, ((int)row.Category).ToString(CultureInfo.InvariantCulture));
            Append(hash, row.Role);
            Append(hash, row.Content);
            Append(hash, row.ToolCalls ?? string.Empty);
            hash.AppendData([0x1E]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
