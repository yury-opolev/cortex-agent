using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Covers the step that decides whether an attachment is usable as vision input.
/// <para>
/// This classification used to be an inline LINQ filter that returned null when nothing matched,
/// so an attachment with an unexpected MIME type was dropped in total silence: the message was
/// still delivered, as text only, and the model answered as though no image had been sent. That
/// is indistinguishable from a model with no vision, which is exactly how it gets misdiagnosed.
/// Pulling it out makes both halves — what survives and what was rejected — assertable.
/// </para>
/// </summary>
public class AgentRuntimeImageClassificationTests
{
    private static MediaAttachment Attachment(string mimeType) =>
        new() { MimeType = mimeType, FileName = "shot", Data = [1, 2, 3] };

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public void SupportedImageTypes_AreUsable(string mimeType)
    {
        var (usable, rejected) = AgentRuntime.ClassifyImageAttachments([Attachment(mimeType)]);

        Assert.Single(usable);
        Assert.Empty(rejected);
    }

    [Theory]
    [InlineData("IMAGE/PNG")]
    [InlineData("Image/Jpeg")]
    public void MimeTypeMatching_IsCaseInsensitive(string mimeType)
    {
        var (usable, rejected) = AgentRuntime.ClassifyImageAttachments([Attachment(mimeType)]);

        Assert.Single(usable);
        Assert.Empty(rejected);
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/svg+xml")]
    [InlineData("image/bmp")]
    public void UnsupportedTypes_AreReportedAsRejected_NotSilentlyDropped(string mimeType)
    {
        var (usable, rejected) = AgentRuntime.ClassifyImageAttachments([Attachment(mimeType)]);

        Assert.Empty(usable);
        Assert.Equal([mimeType], rejected);
    }

    [Theory]
    [InlineData("image/png; charset=binary")]
    [InlineData(" image/png")]
    [InlineData("image/png ")]
    public void MimeTypeWithParametersOrWhitespace_IsRejected(string mimeType)
    {
        // Documents a sharp edge rather than endorsing it: matching is exact, so a sender that
        // decorates the type gets no image. It is now at least VISIBLE in the rejected list
        // instead of vanishing, which is what makes this diagnosable from a log line.
        var (usable, rejected) = AgentRuntime.ClassifyImageAttachments([Attachment(mimeType)]);

        Assert.Empty(usable);
        Assert.Single(rejected);
    }

    [Fact]
    public void MixedAttachments_PartitionBothWays()
    {
        var (usable, rejected) = AgentRuntime.ClassifyImageAttachments(
        [
            Attachment("image/png"),
            Attachment("application/zip"),
            Attachment("image/webp"),
        ]);

        Assert.Equal(2, usable.Count);
        Assert.Equal(["application/zip"], rejected);
    }

    [Fact]
    public void NoAttachments_YieldsNothingToReport()
    {
        var (usable, rejected) = AgentRuntime.ClassifyImageAttachments([]);

        Assert.Empty(usable);
        Assert.Empty(rejected);
    }
}
