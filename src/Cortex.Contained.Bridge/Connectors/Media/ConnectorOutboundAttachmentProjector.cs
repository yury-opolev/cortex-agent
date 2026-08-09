using System.Text;
using Cortex.Contained.Bridge.Connectors.Protocol;
using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Bridge.Connectors.Media;

/// <summary>Outcome of projecting agent attachments onto the connector wire.</summary>
public sealed record ConnectorOutboundAttachmentProjection
{
    /// <summary>
    /// The wire attachments, or null when the message carries none that may be delivered.
    /// Null rather than empty so the <c>attachments</c> field is omitted entirely, keeping the
    /// frame byte-for-byte identical to what a pre-media connector received.
    /// </summary>
    public IReadOnlyList<ConnectorAttachmentPayload>? Attachments { get; init; }

    /// <summary>Attachments that could not be delivered and were dropped.</summary>
    public int DroppedCount { get; init; }

    /// <summary>A projection carrying nothing.</summary>
    public static ConnectorOutboundAttachmentProjection Empty { get; } = new();
}

/// <summary>
/// Turns the agent's <see cref="MediaAttachment"/>s into connector wire attachments, choosing a
/// carrying mode per attachment: inline base64 while it fits the frame budget, otherwise a
/// Bridge-issued handle the connector fetches out of band.
/// </summary>
/// <remarks>
/// Dropping is always preferred to sending something oversized. An attachment that cannot be
/// carried is omitted and counted, never squeezed into a frame that would exceed
/// <c>MaxFrameBytes</c>.
/// <para>
/// The consequence differs by direction, which is worth being precise about: an oversized
/// INBOUND frame is fatal — the Bridge sends <c>frame_too_large</c> and closes. Outbound, the
/// transport's own cap check throws, the session survives, and only that one message is lost.
/// Neither is acceptable, but the outbound failure mode is the recoverable one.
/// </para>
/// <para>
/// The inline budget is supplied per message rather than read from policy, because the frame
/// also has to hold the message text, which can run to <c>maxMessageLength</c> characters. Any
/// fixed reserve large enough for that would be too small to allow useful inlining, and any
/// reserve small enough to allow inlining would be overrun by a long message.
/// </para>
/// <para>
/// NOTE ON REACH: only the proactive path currently carries attachments to a channel.
/// <c>HubMessageDispatcher.OnAgentResponseCompleteAsync</c> builds its outbound message from
/// <c>ResponseCompleteMessage</c>, which has no attachments field, so a normal agent reply never
/// has media to project. Attachments reach a connector when the agent calls its
/// <c>send_message</c> tool. This mirrors the existing Discord behaviour and is not specific to
/// connectors.
/// </para>
/// <para>
/// Pure apart from the optional issuer, so the carrying-mode decisions are directly testable.
/// </para>
/// </remarks>
public sealed class ConnectorOutboundAttachmentProjector
{
    /// <summary>
    /// Fixed JSON cost of one attachment object: field names, braces, quotes, commas, the MIME
    /// type, and the digits of <c>sizeBytes</c>. Generous on purpose — being wrong in the
    /// cautious direction costs a little inline headroom; being wrong the other way is a fatal
    /// <c>frame_too_large</c> close.
    /// </summary>
    internal const int PerAttachmentJsonOverheadBytes = 256;

    /// <summary>
    /// Worst-case expansion of one metadata byte inside a JSON string, where every character
    /// could require a six-character <c>\uXXXX</c> escape.
    /// </summary>
    internal const int JsonStringEscapeWorstCase = 6;

    private readonly ConnectorMediaPolicy policy;
    private readonly IConnectorAttachmentIssuer? issuer;

    /// <summary>Initialises a new <see cref="ConnectorOutboundAttachmentProjector"/>.</summary>
    /// <param name="policy">The effective media policy supplying every limit.</param>
    /// <param name="issuer">
    /// Issues handles for attachments too large to inline. Null means no out-of-band channel is
    /// available, so oversized attachments are dropped rather than carried.
    /// </param>
    public ConnectorOutboundAttachmentProjector(
        ConnectorMediaPolicy policy,
        IConnectorAttachmentIssuer? issuer = null)
    {
        this.policy = policy;
        this.issuer = issuer;
    }

    /// <summary>
    /// Projects <paramref name="attachments"/> for delivery to <paramref name="channelId"/>.
    /// </summary>
    /// <param name="attachments">The agent's attachments; may be null or empty.</param>
    /// <param name="channelId">The receiving channel, used to scope any issued handle.</param>
    /// <param name="supportsMedia">Whether the receiving connector negotiated media support.</param>
    /// <param name="inlineBudgetBytes">
    /// Wire bytes still available in this specific frame for inline attachment payloads, after
    /// the envelope and message text have been accounted for. Pass
    /// <see cref="int.MaxValue"/> only when the frame budget genuinely does not apply.
    /// </param>
    public ConnectorOutboundAttachmentProjection Project(
        IReadOnlyList<MediaAttachment>? attachments,
        string channelId,
        bool supportsMedia,
        int inlineBudgetBytes)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return ConnectorOutboundAttachmentProjection.Empty;
        }

        // A connector that did not ask for media must observe exactly the behaviour it did
        // before media existed: no field, not an empty array.
        if (!this.policy.Enabled || !supportsMedia)
        {
            return new ConnectorOutboundAttachmentProjection { DroppedCount = attachments.Count };
        }

        var projected = new List<ConnectorAttachmentPayload>(
            Math.Min(attachments.Count, this.policy.MaxAttachmentsPerMessage));
        var dropped = 0;
        var remainingInlineBudget = (long)Math.Min(inlineBudgetBytes, this.policy.MaxTotalInlineBytes);

        for (var i = 0; i < attachments.Count; i++)
        {
            if (projected.Count == this.policy.MaxAttachmentsPerMessage)
            {
                // Nothing after this point can be carried; do not walk the rest of the list.
                dropped += attachments.Count - i;
                break;
            }

            var payload = this.ProjectOne(attachments[i], channelId, ref remainingInlineBudget);
            if (payload is null)
            {
                dropped++;
                continue;
            }

            projected.Add(payload);
        }

        return new ConnectorOutboundAttachmentProjection
        {
            Attachments = projected.Count > 0 ? projected : null,
            DroppedCount = dropped,
        };
    }

    /// <summary>
    /// Upper bound on the wire bytes an inline attachment payload costs: the base64 body plus
    /// its JSON field names, quoting, and the UTF-8 encoding of its metadata.
    /// </summary>
    /// <param name="rawByteCount">Decoded size of the attachment.</param>
    /// <param name="fileName">The sanitised file name, or null.</param>
    /// <param name="caption">The truncated caption, or null.</param>
    internal static long EstimateInlineWireCost(long rawByteCount, string? fileName, string? caption)
    {
        // Base64 encodes 3 bytes as 4 characters, rounded up to a 4-character group.
        var base64Length = ((rawByteCount + 2) / 3) * 4;

        var metadata = (fileName is null ? 0 : Encoding.UTF8.GetByteCount(fileName))
            + (caption is null ? 0 : Encoding.UTF8.GetByteCount(caption));

        // Metadata is JSON string content, so every character could need a six-byte \uXXXX
        // escape in the worst case. Assuming that is cheap and keeps the bound honest.
        return base64Length + (metadata * JsonStringEscapeWorstCase) + PerAttachmentJsonOverheadBytes;
    }

    private ConnectorAttachmentPayload? ProjectOne(
        MediaAttachment attachment,
        string channelId,
        ref long remainingInlineBudget)
    {
        // Only attachments the Bridge physically holds can be delivered. A MediaAttachment
        // carrying a Url instead of Data belongs to a channel that fetches its own media; the
        // connector protocol never passes a location through, so there is nothing to send.
        if (attachment.Data is not { Length: > 0 } data)
        {
            return null;
        }

        var mimeType = ConnectorMediaPolicy.NormalizeMimeType(attachment.MimeType);
        if (mimeType is null || !this.policy.IsMimeTypeAllowed(mimeType))
        {
            return null;
        }

        // Verify outbound content too. The agent is trusted, but a mislabelled attachment would
        // fail the connector's own type check anyway, and catching it here makes the reason
        // visible in the Bridge log rather than in a third party's.
        if (!ImageContentSniffer.MatchesDeclaredType(data, mimeType))
        {
            return null;
        }

        if (data.LongLength > this.policy.MaxAttachmentBytes)
        {
            return null;
        }

        var fileName = ConnectorText.Truncate(
            ConnectorAttachmentValidator.SanitizeFileName(attachment.FileName),
            ConnectorAttachmentValidator.MaxFileNameLength);
        var caption = ConnectorText.Truncate(attachment.Caption, ConnectorAttachmentValidator.MaxCaptionLength);

        // Metadata is priced into the decision, not just the payload: four maximum-length
        // captions are several kilobytes on their own, and a budget that ignored them would
        // authorise a frame it cannot actually fit.
        var fitsInline = data.Length <= this.policy.MaxInlineBytes;
        long inlineWireCost = 0;

        if (fitsInline)
        {
            inlineWireCost = EstimateInlineWireCost(data.LongLength, fileName, caption);
            fitsInline = inlineWireCost <= remainingInlineBudget;
        }

        if (fitsInline)
        {
            remainingInlineBudget -= inlineWireCost;

            return new ConnectorAttachmentPayload
            {
                MimeType = mimeType,
                FileName = fileName,
                Caption = caption,
                SizeBytes = data.LongLength,
                Data = Convert.ToBase64String(data),
            };
        }

        var handle = this.issuer?.Issue(channelId, new ConnectorAttachmentContent
        {
            MimeType = mimeType,
            Data = data,
            FileName = fileName,
            Caption = caption,
        });

        if (handle is null)
        {
            return null;
        }

        // A handle payload is small but not free — it still carries the metadata. Charging it to
        // the same budget keeps the accounting complete rather than merely almost complete.
        remainingInlineBudget -= EstimateHandleWireCost(handle, fileName, caption);

        return new ConnectorAttachmentPayload
        {
            MimeType = mimeType,
            FileName = fileName,
            Caption = caption,
            SizeBytes = data.LongLength,
            Handle = handle,
        };
    }

    /// <summary>
    /// Upper bound on the wire bytes a handle-carried attachment payload costs. Far smaller than
    /// the inline case — the bytes travel out of band — but the metadata still rides the frame.
    /// </summary>
    /// <param name="handle">The issued handle.</param>
    /// <param name="fileName">The sanitised file name, or null.</param>
    /// <param name="caption">The truncated caption, or null.</param>
    internal static long EstimateHandleWireCost(string handle, string? fileName, string? caption)
    {
        var metadata = handle.Length
            + (fileName is null ? 0 : Encoding.UTF8.GetByteCount(fileName))
            + (caption is null ? 0 : Encoding.UTF8.GetByteCount(caption));

        return (metadata * JsonStringEscapeWorstCase) + PerAttachmentJsonOverheadBytes;
    }
}
