using Cortex.Contained.Bridge.Connectors.Media;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Bridge.Tests.Connectors.Media;

/// <summary>
/// Tests <see cref="ConnectorOutboundAttachmentProjector"/> — the agent-to-connector direction.
/// The rule that shapes every case: an attachment that cannot be carried safely is DROPPED, never
/// squeezed into a frame that would exceed the cap, because that is a fatal close for the session.
/// </summary>
public sealed class ConnectorOutboundAttachmentProjectorTests
{
    private const string ChannelId = "plugin:terminal:default";
    private const int OneMebibyte = 1_048_576;

    private static ConnectorOutboundAttachmentProjector Build(
        Action<ConnectorMediaConfig>? configure = null,
        IConnectorAttachmentIssuer? issuer = null,
        int maxFrameBytes = OneMebibyte)
    {
        var config = new ConnectorMediaConfig();
        configure?.Invoke(config);
        return new ConnectorOutboundAttachmentProjector(
            ConnectorMediaPolicy.From(config, maxFrameBytes),
            issuer);
    }

    private static MediaAttachment Png(int totalBytes = 16, string? fileName = "chart.png", string? caption = null)
    {
        var data = new byte[totalBytes];
        ImageContentSnifferTests.Png.AsSpan(0, Math.Min(8, totalBytes)).CopyTo(data);

        return new MediaAttachment
        {
            MimeType = "image/png",
            FileName = fileName,
            Caption = caption,
            Data = data,
            SizeBytes = data.LongLength,
        };
    }

    private static IConnectorAttachmentIssuer StubIssuer(string? handle = "att_deadbeef")
    {
        var issuer = Substitute.For<IConnectorAttachmentIssuer>();
        issuer.Issue(Arg.Any<string>(), Arg.Any<ConnectorAttachmentContent>()).Returns(handle);
        return issuer;
    }

    // ── Nothing to carry ─────────────────────────────────────────────

    [Fact]
    public void Project_NullAttachments_YieldsNoField()
    {
        var projection = Build().Project(null, ChannelId, supportsMedia: true);

        Assert.Null(projection.Attachments);
        Assert.Equal(0, projection.DroppedCount);
    }

    [Fact]
    public void Project_EmptyAttachments_YieldsNoField()
    {
        var projection = Build().Project([], ChannelId, supportsMedia: true);

        Assert.Null(projection.Attachments);
    }

    // ── Connectors that did not opt in are unaffected ────────────────

    [Fact]
    public void Project_ConnectorWithoutMediaCapability_OmitsTheFieldEntirely()
    {
        // Null, not an empty array: the frame must be byte-for-byte what a pre-media connector
        // has always received.
        var projection = Build().Project([Png()], ChannelId, supportsMedia: false);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_MediaDisabledByPolicy_OmitsTheFieldEntirely()
    {
        var projector = Build(c => c.Enabled = false);

        var projection = projector.Project([Png()], ChannelId, supportsMedia: true);

        Assert.Null(projection.Attachments);
    }

    // ── Inline carrying mode ─────────────────────────────────────────

    [Fact]
    public void Project_SmallAttachment_IsCarriedInline()
    {
        var attachment = Png(caption: "the chart");

        var projection = Build().Project([attachment], ChannelId, supportsMedia: true);

        var payload = Assert.Single(projection.Attachments!);
        Assert.Equal("image/png", payload.MimeType);
        Assert.Equal("chart.png", payload.FileName);
        Assert.Equal("the chart", payload.Caption);
        Assert.Equal(16, payload.SizeBytes);
        Assert.Null(payload.Handle);
        Assert.Equal(attachment.Data, Convert.FromBase64String(payload.Data!));
    }

    [Fact]
    public void Project_NeverEmitsAUrlField()
    {
        var projection = Build().Project([Png()], ChannelId, supportsMedia: true);

        Assert.Null(Assert.Single(projection.Attachments!).Url);
    }

    [Fact]
    public void Project_MultipleSmallAttachments_AreAllCarried()
    {
        var projection = Build().Project([Png(), Png(), Png()], ChannelId, supportsMedia: true);

        Assert.Equal(3, projection.Attachments!.Count);
        Assert.Equal(0, projection.DroppedCount);
    }

    // ── Handle carrying mode ─────────────────────────────────────────

    [Fact]
    public void Project_AttachmentTooLargeToInline_BecomesAHandle()
    {
        var projector = Build(c => c.MaxInlineBytes = 1024, issuer: StubIssuer());

        var projection = projector.Project([Png(4096)], ChannelId, supportsMedia: true);

        var payload = Assert.Single(projection.Attachments!);
        Assert.Equal("att_deadbeef", payload.Handle);
        Assert.Null(payload.Data);
        Assert.Equal(4096, payload.SizeBytes);
    }

    [Fact]
    public void Project_HandleIsIssuedScopedToTheReceivingChannel()
    {
        var issuer = StubIssuer();
        var projector = Build(c => c.MaxInlineBytes = 1024, issuer: issuer);

        projector.Project([Png(4096)], ChannelId, supportsMedia: true);

        issuer.Received(1).Issue(ChannelId, Arg.Any<ConnectorAttachmentContent>());
    }

    [Fact]
    public void Project_TooLargeToInlineAndNoIssuer_IsDroppedNotSentInline()
    {
        // Sending it inline anyway would exceed MaxFrameBytes and fatally close the session.
        var projector = Build(c => c.MaxInlineBytes = 1024);

        var projection = projector.Project([Png(4096)], ChannelId, supportsMedia: true);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_IssuerRefuses_AttachmentIsDropped()
    {
        var projector = Build(c => c.MaxInlineBytes = 1024, issuer: StubIssuer(handle: null));

        var projection = projector.Project([Png(4096)], ChannelId, supportsMedia: true);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_AggregateInlineBudgetExhausted_SpillsToHandles()
    {
        var issuer = StubIssuer();
        var projector = new ConnectorOutboundAttachmentProjector(
            ConnectorMediaPolicy.From(
                new ConnectorMediaConfig { MaxInlineBytes = 4096, MaxAttachmentsPerMessage = 4 },
                maxFrameBytes: ConnectorMediaPolicy.MinFrameBytes),
            issuer);

        var projection = projector.Project(
            [Png(4096), Png(4096), Png(4096), Png(4096)],
            ChannelId,
            supportsMedia: true);

        Assert.Equal(4, projection.Attachments!.Count);
        Assert.Equal(0, projection.DroppedCount);

        // The first fits inline; once the whole-message budget is spent the rest must go
        // out of band rather than overflow the frame.
        Assert.NotNull(projection.Attachments[0].Data);
        Assert.Contains(projection.Attachments, a => a.Handle is not null);
    }

    // ── Limits and refusals ──────────────────────────────────────────

    [Fact]
    public void Project_MoreThanTheMaximum_CarriesTheFirstAndDropsTheRest()
    {
        var projection = Build().Project(
            [Png(), Png(), Png(), Png(), Png(), Png()],
            ChannelId,
            supportsMedia: true);

        Assert.Equal(4, projection.Attachments!.Count);
        Assert.Equal(2, projection.DroppedCount);
    }

    [Fact]
    public void Project_AttachmentOverTheAbsoluteSizeCap_IsDropped()
    {
        var projector = Build(c => c.MaxAttachmentBytes = 2048, issuer: StubIssuer());

        var projection = projector.Project([Png(8192)], ChannelId, supportsMedia: true);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_MimeTypeOutsideTheAllowList_IsDropped()
    {
        var projector = Build(c => c.AllowedMimeTypes = ["image/jpeg"]);

        var projection = projector.Project([Png()], ChannelId, supportsMedia: true);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_ContentNotMatchingItsDeclaredType_IsDropped()
    {
        var mislabelled = new MediaAttachment
        {
            MimeType = "image/png",
            Data = "<html>not an image</html>"u8.ToArray(),
        };

        var projection = Build().Project([mislabelled], ChannelId, supportsMedia: true);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_AttachmentCarryingAUrlInsteadOfBytes_IsDropped()
    {
        // The connector protocol never passes a location through; there is nothing to send.
        var urlOnly = new MediaAttachment
        {
            MimeType = "image/png",
            Url = "https://example.invalid/chart.png",
        };

        var projection = Build().Project([urlOnly], ChannelId, supportsMedia: true);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_EmptyAttachmentData_IsDropped()
    {
        var empty = new MediaAttachment { MimeType = "image/png", Data = [] };

        var projection = Build().Project([empty], ChannelId, supportsMedia: true);

        Assert.Null(projection.Attachments);
    }

    [Fact]
    public void Project_ValidAndInvalidMixed_CarriesOnlyTheValid()
    {
        var bad = new MediaAttachment { MimeType = "image/png", Data = "nope"u8.ToArray() };

        var projection = Build().Project([Png(), bad, Png()], ChannelId, supportsMedia: true);

        Assert.Equal(2, projection.Attachments!.Count);
        Assert.Equal(1, projection.DroppedCount);
    }

    // ── Metadata hygiene ─────────────────────────────────────────────

    [Fact]
    public void Project_FileNameIsSanitisedAndTruncated()
    {
        var attachment = Png(fileName: "../../etc/" + new string('n', 400) + ".png");

        var projection = Build().Project([attachment], ChannelId, supportsMedia: true);

        var payload = Assert.Single(projection.Attachments!);
        Assert.DoesNotContain('/', payload.FileName!);
        Assert.Equal(ConnectorAttachmentValidator.MaxFileNameLength, payload.FileName!.Length);
    }

    [Fact]
    public void Project_MimeTypeIsNormalised()
    {
        var attachment = Png() with { MimeType = "IMAGE/PNG" };

        var projection = Build().Project([attachment], ChannelId, supportsMedia: true);

        Assert.Equal("image/png", Assert.Single(projection.Attachments!).MimeType);
    }
}
