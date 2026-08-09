namespace Cortex.Contained.Bridge.Connectors.Media;

/// <summary>
/// Identifies image content from its leading bytes so a declared MIME type can be verified
/// rather than trusted. A connector is an untrusted local process; accepting its
/// <c>mimeType</c> at face value would let it smuggle arbitrary content past the allow-list
/// simply by labelling it <c>image/png</c>.
/// </summary>
/// <remarks>
/// Only the container signature is read — nothing is decoded, so there is no decompression-bomb
/// surface here. Deliberately narrow: it recognises exactly the four image formats the agent
/// itself accepts and reports null for everything else.
/// </remarks>
public static class ImageContentSniffer
{
    /// <summary>Longest signature this sniffer needs to inspect (RIFF/WEBP at 12 bytes).</summary>
    public const int MaxSignatureLength = 12;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> Gif87Signature => "GIF87a"u8;

    private static ReadOnlySpan<byte> Gif89Signature => "GIF89a"u8;

    private static ReadOnlySpan<byte> RiffSignature => "RIFF"u8;

    private static ReadOnlySpan<byte> WebpSignature => "WEBP"u8;

    /// <summary>
    /// Returns the MIME type implied by the leading bytes of <paramref name="content"/>, or null
    /// when the content is not one of the recognised image formats.
    /// </summary>
    /// <param name="content">The raw (already decoded) attachment bytes.</param>
    public static string? DetectMimeType(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(PngSignature))
        {
            return "image/png";
        }

        if (content.StartsWith(JpegSignature))
        {
            return "image/jpeg";
        }

        if (content.StartsWith(Gif87Signature) || content.StartsWith(Gif89Signature))
        {
            return "image/gif";
        }

        // WebP is a RIFF container: "RIFF" <4-byte little-endian length> "WEBP".
        if (content.Length >= MaxSignatureLength
            && content.StartsWith(RiffSignature)
            && content[8..12].SequenceEqual(WebpSignature))
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="content"/> actually is the format named by
    /// <paramref name="declaredMimeType"/>.
    /// </summary>
    /// <param name="content">The raw (already decoded) attachment bytes.</param>
    /// <param name="declaredMimeType">The MIME type the sender claimed.</param>
    public static bool MatchesDeclaredType(ReadOnlySpan<byte> content, string? declaredMimeType)
    {
        var declared = ConnectorMediaPolicy.NormalizeMimeType(declaredMimeType);
        if (declared is null)
        {
            return false;
        }

        var actual = DetectMimeType(content);
        return actual is not null && string.Equals(actual, declared, StringComparison.Ordinal);
    }
}
