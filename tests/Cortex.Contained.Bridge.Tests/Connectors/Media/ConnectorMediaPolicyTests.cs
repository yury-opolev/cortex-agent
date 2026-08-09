using Cortex.Contained.Bridge.Connectors.Media;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Connectors.Media;

/// <summary>
/// Tests <see cref="ConnectorMediaPolicy"/> — the single place raw media config is turned into
/// effective limits. The clamping rules here are what stop a hand-edited or hostile
/// <c>cortex.yml</c> from pushing inline attachments past the FATAL frame cap, so they are
/// asserted individually rather than through the components that consume them.
/// </summary>
public sealed class ConnectorMediaPolicyTests
{
    private const int OneMebibyte = 1_048_576;

    private static ConnectorMediaPolicy Build(
        Action<ConnectorMediaConfig>? configure = null,
        int maxFrameBytes = OneMebibyte)
    {
        var config = new ConnectorMediaConfig();
        configure?.Invoke(config);
        return ConnectorMediaPolicy.From(config, maxFrameBytes);
    }

    // ── Defaults ─────────────────────────────────────────────────────

    [Fact]
    public void From_Defaults_MirrorsAgentAllowances()
    {
        var policy = Build();

        Assert.True(policy.Enabled);
        Assert.Equal(4, policy.MaxAttachmentsPerMessage);
        Assert.Equal(8L * 1024 * 1024, policy.MaxAttachmentBytes);
        Assert.Equal(256 * 1024, policy.MaxInlineBytes);
        Assert.Equal(TimeSpan.FromMinutes(10), policy.HandleTtl);
    }

    [Fact]
    public void From_NullConfig_YieldsDefaults()
    {
        var policy = ConnectorMediaPolicy.From(null, OneMebibyte);

        Assert.True(policy.Enabled);
        Assert.Equal(4, policy.MaxAttachmentsPerMessage);
    }

    [Fact]
    public void From_EmptyAllowedMimeTypes_ResolvesToDefaults()
    {
        var policy = Build(c => c.AllowedMimeTypes = []);

        Assert.True(policy.IsMimeTypeAllowed("image/png"));
        Assert.True(policy.IsMimeTypeAllowed("image/jpeg"));
        Assert.True(policy.IsMimeTypeAllowed("image/gif"));
        Assert.True(policy.IsMimeTypeAllowed("image/webp"));
        Assert.Equal(4, policy.AllowedMimeTypes.Count);
    }

    // ── The allow-list must be narrowable ────────────────────────────

    [Fact]
    public void From_ConfiguredAllowedMimeTypes_ReplacesDefaultsRatherThanAddingToThem()
    {
        var policy = Build(c => c.AllowedMimeTypes = ["image/png"]);

        Assert.True(policy.IsMimeTypeAllowed("image/png"));
        Assert.False(policy.IsMimeTypeAllowed("image/jpeg"));
        Assert.False(policy.IsMimeTypeAllowed("image/gif"));
        Assert.False(policy.IsMimeTypeAllowed("image/webp"));
    }

    [Theory]
    [InlineData("IMAGE/PNG")]
    [InlineData("  image/png  ")]
    [InlineData("image/png; charset=binary")]
    public void IsMimeTypeAllowed_NormalisesCaseWhitespaceAndParameters(string declared)
    {
        var policy = Build();

        Assert.True(policy.IsMimeTypeAllowed(declared));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("application/pdf")]
    [InlineData("text/html")]
    public void IsMimeTypeAllowed_RejectsUnknownAndBlank(string? declared)
    {
        var policy = Build();

        Assert.False(policy.IsMimeTypeAllowed(declared));
    }

    [Fact]
    public void From_AllowedMimeTypesWithBlankEntries_DropsThem()
    {
        var policy = Build(c => c.AllowedMimeTypes = ["image/png", "  ", string.Empty]);

        Assert.Single(policy.AllowedMimeTypes);
        Assert.True(policy.IsMimeTypeAllowed("image/png"));
    }

    [Fact]
    public void From_AllowedMimeTypesEntirelyBlank_FallsBackToDefaultsRatherThanBlockingEverything()
    {
        // A list of blanks passes the "did the operator configure something?" check but
        // normalises to nothing. Treating that as "allow nothing" would be an invisible
        // kill-switch: Enabled still reads true but no attachment could ever pass.
        var policy = Build(c => c.AllowedMimeTypes = [string.Empty, "  ", "\t"]);

        Assert.Equal(4, policy.AllowedMimeTypes.Count);
        Assert.True(policy.IsMimeTypeAllowed("image/png"));
    }

    // ── Inline budget is frame-derived, not merely configured ────────

    [Fact]
    public void From_MaxInlineBytesAboveFrameCeiling_IsClampedDown()
    {
        // 4 MB requested inline against a 1 MiB frame would be a guaranteed fatal
        // frame_too_large; the policy must refuse to hand that number out.
        var policy = Build(c => c.MaxInlineBytes = 4 * 1024 * 1024);

        Assert.True(policy.MaxInlineBytes < OneMebibyte);

        // Whatever survives must still fit the frame once base64-encoded, with envelope room left.
        AssertFitsFrame(policy.MaxInlineBytes, OneMebibyte);
    }

    [Fact]
    public void From_TotalInlineBudget_FitsTheFrameEvenAtMaxAttachmentCount()
    {
        // The proposal's own defaults are the trap: 4 x 256 KB is 1 MB raw, which is ~1.37 MB
        // base64-encoded and would blow a 1 MiB frame with every individual attachment legal.
        var policy = Build();

        var worstCase = (long)policy.MaxInlineBytes * policy.MaxAttachmentsPerMessage;
        Assert.True(
            EncodedSize(worstCase) > OneMebibyte,
            "precondition: per-attachment caps alone overflow the frame once base64-encoded");

        AssertFitsFrame(policy.MaxTotalInlineBytes, OneMebibyte);
    }

    [Fact]
    public void From_SmallFrame_ShrinksInlineBudgetsToMatch()
    {
        const int frame = 64 * 1024;
        var policy = Build(maxFrameBytes: frame);

        AssertFitsFrame(policy.MaxInlineBytes, frame);
        AssertFitsFrame(policy.MaxTotalInlineBytes, frame);
    }

    [Fact]
    public void From_InlineBudgetNeverExceedsPerAttachmentCap()
    {
        var policy = Build(c =>
        {
            c.MaxAttachmentBytes = 1024;
            c.MaxInlineBytes = 512 * 1024;
        });

        Assert.Equal(1024, policy.MaxInlineBytes);
    }

    [Fact]
    public void From_MaxAttachmentsPerMessageOfOne_TotalInlineBudgetEqualsPerAttachmentBudget()
    {
        var policy = Build(c => c.MaxAttachmentsPerMessage = 1);

        Assert.Equal(policy.MaxInlineBytes, policy.MaxTotalInlineBytes);
    }

    [Fact]
    public void From_SmallAttachmentCap_TotalInlineBudgetTracksItRatherThanTheFrame()
    {
        var policy = Build(c =>
        {
            c.MaxAttachmentBytes = 100;
            c.MaxAttachmentsPerMessage = 4;
        });

        Assert.Equal(100, policy.MaxInlineBytes);
        Assert.Equal(400, policy.MaxTotalInlineBytes);
    }

    [Fact]
    public void From_PathologicalFrameSize_StillProducesAnInlineBudgetThatFitsTheFrame()
    {
        // Defence in depth: MaxFrameBytes is clamped elsewhere, but the policy must not advertise
        // an inline budget that cannot physically survive base64 inside the frame it was told about.
        foreach (var frameBytes in new[] { -1, 0, 1, 64, 16 * 1024 })
        {
            var policy = Build(maxFrameBytes: frameBytes);

            AssertFitsFrame(policy.MaxInlineBytes, ConnectorMediaPolicy.MinFrameBytes);
            AssertFitsFrame(policy.MaxTotalInlineBytes, ConnectorMediaPolicy.MinFrameBytes);
        }
    }

    // ── Remaining clamps ─────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(99, 16)]
    public void From_MaxAttachmentsPerMessage_IsClampedToSaneRange(int configured, int expected)
    {
        var policy = Build(c => c.MaxAttachmentsPerMessage = configured);

        Assert.Equal(expected, policy.MaxAttachmentsPerMessage);
    }

    [Fact]
    public void From_MaxAttachmentBytes_IsClampedToCeiling()
    {
        var policy = Build(c => c.MaxAttachmentBytes = long.MaxValue);

        Assert.Equal(64L * 1024 * 1024, policy.MaxAttachmentBytes);
    }

    [Fact]
    public void From_NonPositiveMaxAttachmentBytes_ClampsToOne()
    {
        var policy = Build(c => c.MaxAttachmentBytes = 0);

        Assert.Equal(1, policy.MaxAttachmentBytes);
    }

    [Fact]
    public void From_HandleTtlBelowFloor_IsRaised()
    {
        var policy = Build(c => c.HandleTtl = TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(10), policy.HandleTtl);
    }

    [Fact]
    public void From_HandleTtlAboveCeiling_IsLowered()
    {
        var policy = Build(c => c.HandleTtl = TimeSpan.FromDays(7));

        Assert.Equal(TimeSpan.FromHours(1), policy.HandleTtl);
    }

    [Fact]
    public void From_StoredBytesQuota_AlwaysHoldsAtLeastOneMaxAttachment()
    {
        var policy = Build(c =>
        {
            c.MaxAttachmentBytes = 8 * 1024 * 1024;
            c.MaxStoredBytesPerConnector = 1;
        });

        Assert.Equal(policy.MaxAttachmentBytes, policy.MaxStoredBytesPerConnector);
    }

    [Fact]
    public void From_StoredBytesQuota_IsClampedToCeiling()
    {
        var policy = Build(c => c.MaxStoredBytesPerConnector = long.MaxValue);

        Assert.Equal(1024L * 1024 * 1024, policy.MaxStoredBytesPerConnector);
    }

    [Fact]
    public void From_NegativeUploadsPerMinute_BecomesUnlimited()
    {
        var policy = Build(c => c.MaxUploadsPerMinute = -1);

        Assert.Equal(0, policy.MaxUploadsPerMinute);
    }

    [Fact]
    public void From_DisabledFlag_IsPreserved()
    {
        var policy = Build(c => c.Enabled = false);

        Assert.False(policy.Enabled);
    }

    private static long EncodedSize(long rawBytes) => (rawBytes + 2) / 3 * 4;

    /// <summary>
    /// Asserts the invariant the whole class exists to guarantee: base64 of <paramref name="rawBytes"/>
    /// fits inside <paramref name="frameBytes"/> with envelope headroom to spare. Expressed as a
    /// concrete byte reserve rather than a percentage so tightening the reserve cannot silently
    /// pass a test that a proportional assertion would have let through.
    /// </summary>
    private static void AssertFitsFrame(long rawBytes, int frameBytes)
    {
        const int minEnvelopeReserve = 8 * 1024;
        var encoded = EncodedSize(rawBytes);

        Assert.True(
            encoded <= frameBytes - minEnvelopeReserve,
            $"base64 of {rawBytes} raw bytes is {encoded}, which leaves less than {minEnvelopeReserve} bytes of a {frameBytes}-byte frame for the envelope");
    }
}
