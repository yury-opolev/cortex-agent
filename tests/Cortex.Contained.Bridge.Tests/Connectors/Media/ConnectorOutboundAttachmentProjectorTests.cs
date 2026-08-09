using System.Text;
using System.Text.Json;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Media;
using Cortex.Contained.Bridge.Connectors.Protocol;
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
        var projection = Build().Project(null, ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Null(projection.Attachments);
        Assert.Equal(0, projection.DroppedCount);
    }

    [Fact]
    public void Project_EmptyAttachments_YieldsNoField()
    {
        var projection = Build().Project([], ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Null(projection.Attachments);
    }

    // ── Connectors that did not opt in are unaffected ────────────────

    [Fact]
    public void Project_ConnectorWithoutMediaCapability_OmitsTheFieldEntirely()
    {
        // Null, not an empty array: the frame must be byte-for-byte what a pre-media connector
        // has always received.
        var projection = Build().Project([Png()], ChannelId, supportsMedia: false, int.MaxValue);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_MediaDisabledByPolicy_OmitsTheFieldEntirely()
    {
        var projector = Build(c => c.Enabled = false);

        var projection = projector.Project([Png()], ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Null(projection.Attachments);
    }

    // ── Inline carrying mode ─────────────────────────────────────────

    [Fact]
    public void Project_SmallAttachment_IsCarriedInline()
    {
        var attachment = Png(caption: "the chart");

        var projection = Build().Project([attachment], ChannelId, supportsMedia: true, int.MaxValue);

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
        var projection = Build().Project([Png()], ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Null(Assert.Single(projection.Attachments!).Url);
    }

    [Fact]
    public void Project_MultipleSmallAttachments_AreAllCarried()
    {
        var projection = Build().Project([Png(), Png(), Png()], ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Equal(3, projection.Attachments!.Count);
        Assert.Equal(0, projection.DroppedCount);
    }

    // ── Handle carrying mode ─────────────────────────────────────────

    [Fact]
    public void Project_AttachmentTooLargeToInline_BecomesAHandle()
    {
        var projector = Build(c => c.MaxInlineBytes = 1024, issuer: StubIssuer());

        var projection = projector.Project([Png(4096)], ChannelId, supportsMedia: true, int.MaxValue);

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

        projector.Project([Png(4096)], ChannelId, supportsMedia: true, int.MaxValue);

        issuer.Received(1).Issue(ChannelId, Arg.Any<ConnectorAttachmentContent>());
    }

    [Fact]
    public void Project_TooLargeToInlineAndNoIssuer_IsDroppedNotSentInline()
    {
        // Sending it inline anyway would exceed MaxFrameBytes and fatally close the session.
        var projector = Build(c => c.MaxInlineBytes = 1024);

        var projection = projector.Project([Png(4096)], ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_IssuerRefuses_AttachmentIsDropped()
    {
        var projector = Build(c => c.MaxInlineBytes = 1024, issuer: StubIssuer(handle: null));

        var projection = projector.Project([Png(4096)], ChannelId, supportsMedia: true, int.MaxValue);

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
            supportsMedia: true,            int.MaxValue);

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
            supportsMedia: true,            int.MaxValue);

        Assert.Equal(4, projection.Attachments!.Count);
        Assert.Equal(2, projection.DroppedCount);
    }

    [Fact]
    public void Project_AttachmentOverTheAbsoluteSizeCap_IsDropped()
    {
        var projector = Build(c => c.MaxAttachmentBytes = 2048, issuer: StubIssuer());

        var projection = projector.Project([Png(8192)], ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_MimeTypeOutsideTheAllowList_IsDropped()
    {
        var projector = Build(c => c.AllowedMimeTypes = ["image/jpeg"]);

        var projection = projector.Project([Png()], ChannelId, supportsMedia: true, int.MaxValue);

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

        var projection = Build().Project([mislabelled], ChannelId, supportsMedia: true, int.MaxValue);

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

        var projection = Build().Project([urlOnly], ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void Project_EmptyAttachmentData_IsDropped()
    {
        var empty = new MediaAttachment { MimeType = "image/png", Data = [] };

        var projection = Build().Project([empty], ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Null(projection.Attachments);
    }

    [Fact]
    public void Project_ValidAndInvalidMixed_CarriesOnlyTheValid()
    {
        var bad = new MediaAttachment { MimeType = "image/png", Data = "nope"u8.ToArray() };

        var projection = Build().Project([Png(), bad, Png()], ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Equal(2, projection.Attachments!.Count);
        Assert.Equal(1, projection.DroppedCount);
    }

    // ── Metadata hygiene ─────────────────────────────────────────────

    [Fact]
    public void Project_FileNameIsSanitisedAndTruncated()
    {
        var attachment = Png(fileName: "../../etc/" + new string('n', 400) + ".png");

        var projection = Build().Project([attachment], ChannelId, supportsMedia: true, int.MaxValue);

        var payload = Assert.Single(projection.Attachments!);
        Assert.DoesNotContain('/', payload.FileName!);
        Assert.Equal(ConnectorAttachmentValidator.MaxFileNameLength, payload.FileName!.Length);
    }

    [Fact]
    public void Project_MimeTypeIsNormalised()
    {
        var attachment = Png() with { MimeType = "IMAGE/PNG" };

        var projection = Build().Project([attachment], ChannelId, supportsMedia: true, int.MaxValue);

        Assert.Equal("image/png", Assert.Single(projection.Attachments!).MimeType);
    }

    // ── The invariant this class exists for ──────────────────────────

    [Fact]
    public void Project_UnderAFrameBudget_TheSerialisedFrameFitsEvenWithWorstCaseMetadata()
    {
        // This is the test that a fixed "envelope reserve" could not satisfy. Message text alone
        // may run to MaxMessageLength characters, and four attachments can carry 4 x 256-char
        // file names plus 4 x 1024-char captions. Budgeting has to come from the real envelope.
        const int frameBytes = OneMebibyte;
        var issuer = StubIssuer();
        var projector = Build(issuer: issuer, maxFrameBytes: frameBytes);

        var text = new string('t', 32 * 1024);
        var caption = new string('C', ConnectorAttachmentValidator.MaxCaptionLength);
        var fileName = new string('F', ConnectorAttachmentValidator.MaxFileNameLength - 4) + ".png";

        MediaAttachment[] attachments =
        [
            .. Enumerable.Range(0, 4).Select(_ => new MediaAttachment
            {
                MimeType = "image/png",
                FileName = fileName,
                Caption = caption,
                Data = PngBytes(256 * 1024),
            }),
        ];

        var envelope = EnvelopeBytes(text);
        var budget = frameBytes - envelope - 4096;

        var projection = projector.Project(attachments, ChannelId, supportsMedia: true, budget);

        AssertFrameFits(text, projection.Attachments, frameBytes);
    }

    [Fact]
    public void Project_WithHugeMessageText_ShrinksTheInlineBudgetRatherThanOverflowing()
    {
        // A 100 000-character message plus four inline attachments is exactly the case the old
        // fixed reserve got wrong.
        const int frameBytes = OneMebibyte;
        var projector = Build(issuer: StubIssuer(), maxFrameBytes: frameBytes);

        var text = new string('t', 100_000);
        var attachments = Enumerable.Range(0, 4).Select(_ => Png(256 * 1024)).ToList();

        var budget = frameBytes - EnvelopeBytes(text) - 4096;
        var projection = projector.Project(attachments, ChannelId, supportsMedia: true, budget);

        AssertFrameFits(text, projection.Attachments, frameBytes);

        // Nothing is lost: what cannot be inlined goes out of band.
        Assert.Equal(4, projection.Attachments!.Count);
        Assert.Contains(projection.Attachments, a => a.Handle is not null);
    }

    [Fact]
    public void Project_MultiByteCaptions_AreCountedInUtf8NotCharacters()
    {
        // A caption of 1024 emoji is 1024 UTF-16 units but 4096 UTF-8 bytes. Counting characters
        // instead of bytes under-reserves by a factor of four.
        const int frameBytes = OneMebibyte;
        var projector = Build(issuer: StubIssuer(), maxFrameBytes: frameBytes);

        var caption = string.Concat(Enumerable.Repeat("\U0001F3AF", 512));
        var attachments = Enumerable.Range(0, 4).Select(_ => new MediaAttachment
        {
            MimeType = "image/png",
            FileName = "chart.png",
            Caption = caption,
            Data = PngBytes(256 * 1024),
        }).ToList();

        const string text = "here";
        var budget = frameBytes - EnvelopeBytes(text) - 4096;
        var projection = projector.Project(attachments, ChannelId, supportsMedia: true, budget);

        AssertFrameFits(text, projection.Attachments, frameBytes);
    }

    [Fact]
    public void Project_ZeroBudget_SpillsEverythingToHandles()
    {
        var projector = Build(issuer: StubIssuer());

        var projection = projector.Project([Png()], ChannelId, supportsMedia: true, 0);

        Assert.All(projection.Attachments!, a => Assert.NotNull(a.Handle));
    }

    [Fact]
    public void Project_ZeroBudgetAndNoIssuer_DropsRatherThanInlining()
    {
        var projector = Build();

        var projection = projector.Project([Png()], ChannelId, supportsMedia: true, 0);

        Assert.Null(projection.Attachments);
        Assert.Equal(1, projection.DroppedCount);
    }

    [Fact]
    public void EstimateInlineWireCost_IsNeverBelowTheActualSerialisedCost()
    {
        foreach (var rawBytes in new[] { 1, 3, 4, 100, 1024, 65_536 })
        {
            var data = PngBytes(rawBytes);
            var payload = new ConnectorAttachmentPayload
            {
                MimeType = "image/png",
                FileName = "chart.png",
                Caption = "a caption",
                SizeBytes = data.LongLength,
                Data = Convert.ToBase64String(data),
            };

            var actual = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload, ConnectorJson.Options));
            var estimate = ConnectorOutboundAttachmentProjector.EstimateInlineWireCost(
                rawBytes,
                "chart.png",
                "a caption");

            Assert.True(estimate >= actual, $"estimate {estimate} understated actual {actual} for {rawBytes} raw bytes");
        }
    }

    [Fact]
    public void EstimateHandleWireCost_IsNeverBelowTheActualSerialisedCost()
    {
        var payload = new ConnectorAttachmentPayload
        {
            MimeType = "image/png",
            FileName = "chart.png",
            Caption = "a caption",
            SizeBytes = 512 * 1024,
            Handle = "att_9f2c14e0d3b74a15aabb",
        };

        var actual = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload, ConnectorJson.Options));
        var estimate = ConnectorOutboundAttachmentProjector.EstimateHandleWireCost(
            payload.Handle,
            payload.FileName,
            payload.Caption);

        Assert.True(estimate >= actual, $"estimate {estimate} understated actual {actual}");
    }

    private static byte[] PngBytes(int totalBytes)
    {
        var data = new byte[totalBytes];
        ImageContentSnifferTests.Png.AsSpan(0, Math.Min(8, totalBytes)).CopyTo(data);
        return data;
    }

    /// <summary>Serialises the outbound envelope exactly as the session does, minus attachments.</summary>
    private static int EnvelopeBytes(string text) => Encoding.UTF8.GetByteCount(
        ConnectorFrame.Serialize(ConnectorFrameTypes.Outbound, new ConnectorOutboundPayload
        {
            MessageId = "00000000000000000000000000000000",
            ConversationId = "plugin:terminal:default",
            Content = new ConnectorContentPayload { Text = text },
            Cursor = "2026-08-09T12:00:00.0000000Z",
        }));

    /// <summary>
    /// Serialises the complete frame the session would send and asserts it fits the cap. This is
    /// the only assertion that proves the invariant end to end rather than by arithmetic.
    /// </summary>
    private static void AssertFrameFits(
        string text,
        IReadOnlyList<ConnectorAttachmentPayload>? attachments,
        int frameBytes)
    {
        var json = ConnectorFrame.Serialize(ConnectorFrameTypes.Outbound, new ConnectorOutboundPayload
        {
            MessageId = "00000000000000000000000000000000",
            ConversationId = "plugin:terminal:default",
            Content = new ConnectorContentPayload { Text = text, Attachments = attachments },
            Cursor = "2026-08-09T12:00:00.0000000Z",
        });

        var actual = Encoding.UTF8.GetByteCount(json);
        Assert.True(actual <= frameBytes, $"serialised frame is {actual} bytes; the cap is {frameBytes}");
    }
}
