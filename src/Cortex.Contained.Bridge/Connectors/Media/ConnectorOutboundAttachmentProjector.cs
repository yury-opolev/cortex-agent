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
/// Dropping is always preferred to sending something unsafe. An attachment that cannot be
/// carried is omitted and counted, never squeezed into a frame that would exceed
/// <c>MaxFrameBytes</c> — that is a FATAL <c>frame_too_large</c> close, so one oversized image
/// would take the whole session down with it.
/// <para>
/// Pure apart from the optional issuer, so the carrying-mode decisions are directly testable.
/// </para>
/// </remarks>
public sealed class ConnectorOutboundAttachmentProjector
{
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
    public ConnectorOutboundAttachmentProjection Project(
        IReadOnlyList<MediaAttachment>? attachments,
        string channelId,
        bool supportsMedia)
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
        var totalInlineBytes = 0L;

        foreach (var attachment in attachments)
        {
            if (projected.Count == this.policy.MaxAttachmentsPerMessage)
            {
                dropped++;
                continue;
            }

            var payload = this.ProjectOne(attachment, channelId, ref totalInlineBytes);
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

    private ConnectorAttachmentPayload? ProjectOne(
        MediaAttachment attachment,
        string channelId,
        ref long totalInlineBytes)
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

        var fitsInline = data.Length <= this.policy.MaxInlineBytes
            && totalInlineBytes + data.Length <= this.policy.MaxTotalInlineBytes;

        var fileName = ConnectorText.Truncate(
            ConnectorAttachmentValidator.SanitizeFileName(attachment.FileName),
            ConnectorAttachmentValidator.MaxFileNameLength);
        var caption = ConnectorText.Truncate(attachment.Caption, ConnectorAttachmentValidator.MaxCaptionLength);

        if (fitsInline)
        {
            totalInlineBytes += data.Length;

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

        return new ConnectorAttachmentPayload
        {
            MimeType = mimeType,
            FileName = fileName,
            Caption = caption,
            SizeBytes = data.LongLength,
            Handle = handle,
        };
    }
}
