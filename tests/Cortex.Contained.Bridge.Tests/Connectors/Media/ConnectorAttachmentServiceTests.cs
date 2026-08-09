using Cortex.Contained.Bridge.Connectors.Media;
using Cortex.Contained.Bridge.Connectors.Security;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Connectors.Media;

/// <summary>
/// Tests <see cref="ConnectorAttachmentService"/> — the authorisation core behind the attachment
/// REST endpoints. A connector has no Web UI session, so a bearer pairing token is the ONLY thing
/// standing between one connector and another's staged media; these tests are written accordingly.
/// </summary>
public sealed class ConnectorAttachmentServiceTests
{
    private const string ChannelA = "plugin:terminal:default";
    private const string ChannelB = "plugin:other:default";
    private const string TokenA = "token-for-channel-a";
    private const string TokenB = "token-for-channel-b";
    private const int OneMebibyte = 1_048_576;

    private sealed class Harness
    {
        public required ConnectorAttachmentService Service { get; init; }

        public required ConnectorAttachmentStore Store { get; init; }

        public required FakeConnectorSecretStore SecretStore { get; init; }

        public required FakeTimeProvider Time { get; init; }
    }

    private static Harness Build(Action<ConnectorMediaConfig>? configure = null, bool seedConnectors = true)
    {
        var config = new ConnectorMediaConfig();
        configure?.Invoke(config);
        var policy = ConnectorMediaPolicy.From(config, OneMebibyte);

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-09T12:00:00Z", null));
        var secretStore = new FakeConnectorSecretStore();
        var tokenStore = new ConnectorTokenStore(secretStore, NullLogger<ConnectorTokenStore>.Instance);

        if (seedConnectors)
        {
            tokenStore.Save(NewRecord(ChannelA, "terminal", TokenA));
            tokenStore.Save(NewRecord(ChannelB, "other", TokenB));
        }

        var attachmentStore = new ConnectorAttachmentStore(
            policy,
            time,
            NullLogger<ConnectorAttachmentStore>.Instance);

        return new Harness
        {
            Service = new ConnectorAttachmentService(
                tokenStore,
                attachmentStore,
                new ConnectorUploadRateLimiter(policy.MaxUploadsPerMinute, time),
                policy,
                time,
                NullLogger<ConnectorAttachmentService>.Instance),
            Store = attachmentStore,
            SecretStore = secretStore,
            Time = time,
        };
    }

    private static ConnectorRecord NewRecord(string channelId, string key, string token, bool enabled = true) => new()
    {
        ChannelId = channelId,
        Key = key,
        InstanceId = "default",
        DisplayName = key,
        Token = token,
        PairedAt = DateTimeOffset.UnixEpoch,
        Enabled = enabled,
    };

    private static byte[] Png(int totalBytes = 64)
    {
        var data = new byte[totalBytes];
        ImageContentSnifferTests.Png.AsSpan(0, Math.Min(8, totalBytes)).CopyTo(data);
        return data;
    }

    private static string Bearer(string token) => $"Bearer {token}";

    // ── Bearer token parsing ─────────────────────────────────────────

    [Theory]
    [InlineData("Bearer abc", "abc")]
    [InlineData("bearer abc", "abc")]
    [InlineData("BEARER   abc  ", "abc")]
    [InlineData("  Bearer abc", "abc")]
    public void ExtractBearerToken_ParsesTheScheme(string header, string expected)
    {
        Assert.Equal(expected, ConnectorAttachmentService.ExtractBearerToken(header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("Basic abc")]
    [InlineData("Bearer")]
    [InlineData("Bearer   ")]
    [InlineData("Bearerabc")]
    public void ExtractBearerToken_RejectsAnythingElse(string? header)
    {
        Assert.Null(ConnectorAttachmentService.ExtractBearerToken(header));
    }

    // ── Authorisation ────────────────────────────────────────────────

    [Fact]
    public void ResolveChannelId_ValidToken_ReturnsTheOwningChannel()
    {
        var h = Build();

        Assert.Equal(ChannelA, h.Service.ResolveChannelId(Bearer(TokenA)));
        Assert.Equal(ChannelB, h.Service.ResolveChannelId(Bearer(TokenB)));
    }

    [Fact]
    public void ResolveChannelId_UnknownToken_ReturnsNull()
    {
        var h = Build();

        Assert.Null(h.Service.ResolveChannelId(Bearer("not-a-real-token")));
    }

    [Fact]
    public void ResolveChannelId_DisabledConnector_ReturnsNull()
    {
        var h = Build(seedConnectors: false);
        var tokenStore = new ConnectorTokenStore(h.SecretStore, NullLogger<ConnectorTokenStore>.Instance);
        tokenStore.Save(NewRecord(ChannelA, "terminal", TokenA, enabled: false));

        Assert.Null(h.Service.ResolveChannelId(Bearer(TokenA)));
    }

    [Fact]
    public void Upload_WithoutToken_IsUnauthorized()
    {
        var h = Build();

        var result = h.Service.Upload(null, Png(), "image/png");

        Assert.Equal(ConnectorAttachmentAccessError.Unauthorized, result.Error);
    }

    [Fact]
    public void Fetch_WithoutToken_IsUnauthorized()
    {
        var h = Build();

        Assert.Equal(
            ConnectorAttachmentAccessError.Unauthorized,
            h.Service.Fetch(null, "att_whatever").Error);
    }

    // ── Upload happy path ────────────────────────────────────────────

    [Fact]
    public void Upload_ValidPng_IssuesAHandleWithExpiry()
    {
        var h = Build(c => c.HandleTtl = TimeSpan.FromMinutes(10));

        var result = h.Service.Upload(Bearer(TokenA), Png(), "image/png", "shot.png", "the dialog");

        Assert.True(result.Success);
        Assert.NotNull(result.Handle);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromMinutes(10), result.ExpiresAt);
    }

    [Fact]
    public void Upload_ThenFetch_RoundTripsTheContent()
    {
        var h = Build();
        var data = Png(128);

        var upload = h.Service.Upload(Bearer(TokenA), data, "image/png", "shot.png");
        var fetch = h.Service.Fetch(Bearer(TokenA), upload.Handle);

        Assert.True(fetch.Success);
        Assert.Equal(data, fetch.Content!.Data);
        Assert.Equal("image/png", fetch.Content.MimeType);
        Assert.Equal("shot.png", fetch.Content.FileName);
    }

    [Fact]
    public void Upload_OctetStreamContentType_IsAcceptedOnSniffedType()
    {
        // A multipart part routinely arrives as application/octet-stream; what the bytes ARE is
        // what matters, and it is what gets stored.
        var h = Build();

        var upload = h.Service.Upload(Bearer(TokenA), Png(), "application/octet-stream");

        Assert.True(upload.Success);
        Assert.Equal("image/png", h.Service.Fetch(Bearer(TokenA), upload.Handle).Content!.MimeType);
    }

    [Fact]
    public void Upload_NoDeclaredContentType_IsAcceptedOnSniffedType()
    {
        var h = Build();

        Assert.True(h.Service.Upload(Bearer(TokenA), Png(), null).Success);
    }

    // ── Content refusals ─────────────────────────────────────────────

    [Fact]
    public void Upload_NonImageContent_IsRejected()
    {
        var h = Build();

        var result = h.Service.Upload(Bearer(TokenA), "<html>nope</html>"u8.ToArray(), "image/png");

        Assert.Equal(ConnectorAttachmentAccessError.ContentRejected, result.Error);
    }

    [Fact]
    public void Upload_ContentContradictingTheDeclaredType_IsRejected()
    {
        var h = Build();

        var result = h.Service.Upload(Bearer(TokenA), Png(), "image/jpeg");

        Assert.Equal(ConnectorAttachmentAccessError.ContentRejected, result.Error);
    }

    [Fact]
    public void Upload_TypeOutsideTheAllowList_IsRejected()
    {
        var h = Build(c => c.AllowedMimeTypes = ["image/jpeg"]);

        Assert.Equal(
            ConnectorAttachmentAccessError.ContentRejected,
            h.Service.Upload(Bearer(TokenA), Png(), "image/png").Error);
    }

    [Fact]
    public void Upload_EmptyContent_IsRejected()
    {
        var h = Build();

        Assert.Equal(
            ConnectorAttachmentAccessError.ContentRejected,
            h.Service.Upload(Bearer(TokenA), [], "image/png").Error);
    }

    [Fact]
    public void Upload_OversizeContent_IsRejected()
    {
        var h = Build(c => c.MaxAttachmentBytes = 1024);

        Assert.Equal(
            ConnectorAttachmentAccessError.ContentRejected,
            h.Service.Upload(Bearer(TokenA), Png(4096), "image/png").Error);
    }

    // ── Cross-channel isolation ──────────────────────────────────────

    [Fact]
    public void Fetch_AnotherConnectorsHandle_IsNotFound()
    {
        var h = Build();
        var upload = h.Service.Upload(Bearer(TokenA), Png(), "image/png");

        var fetch = h.Service.Fetch(Bearer(TokenB), upload.Handle);

        // NotFound, never Unauthorized: a distinct code would confirm the handle exists.
        Assert.Equal(ConnectorAttachmentAccessError.NotFound, fetch.Error);
    }

    [Fact]
    public void Fetch_AnotherConnectorsHandle_DoesNotConsumeIt()
    {
        var h = Build();
        var upload = h.Service.Upload(Bearer(TokenA), Png(), "image/png");

        h.Service.Fetch(Bearer(TokenB), upload.Handle);

        Assert.True(h.Service.Fetch(Bearer(TokenA), upload.Handle).Success);
    }

    [Fact]
    public void Fetch_IsSingleUse()
    {
        var h = Build();
        var upload = h.Service.Upload(Bearer(TokenA), Png(), "image/png");

        Assert.True(h.Service.Fetch(Bearer(TokenA), upload.Handle).Success);
        Assert.Equal(
            ConnectorAttachmentAccessError.NotFound,
            h.Service.Fetch(Bearer(TokenA), upload.Handle).Error);
    }

    [Fact]
    public void Fetch_ExpiredHandle_IsNotFound()
    {
        var h = Build(c => c.HandleTtl = TimeSpan.FromMinutes(10));
        var upload = h.Service.Upload(Bearer(TokenA), Png(), "image/png");

        h.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(
            ConnectorAttachmentAccessError.NotFound,
            h.Service.Fetch(Bearer(TokenA), upload.Handle).Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("att/../../secrets")]
    [InlineData("att 1")]
    [InlineData("att:1")]
    public void Fetch_MalformedHandle_IsNotFoundRatherThanBadRequest(string handle)
    {
        var h = Build();

        Assert.Equal(
            ConnectorAttachmentAccessError.NotFound,
            h.Service.Fetch(Bearer(TokenA), handle).Error);
    }

    [Fact]
    public void Fetch_OverlongHandle_IsNotFound()
    {
        var h = Build();

        Assert.Equal(
            ConnectorAttachmentAccessError.NotFound,
            h.Service.Fetch(Bearer(TokenA), new string('a', 200)).Error);
    }

    // ── Rate limiting and quota ──────────────────────────────────────

    [Fact]
    public void Upload_BeyondTheRateLimit_IsRefused()
    {
        var h = Build(c => c.MaxUploadsPerMinute = 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(h.Service.Upload(Bearer(TokenA), Png(), "image/png").Success);
        }

        Assert.Equal(
            ConnectorAttachmentAccessError.RateLimited,
            h.Service.Upload(Bearer(TokenA), Png(), "image/png").Error);
    }

    [Fact]
    public void Upload_RateLimitIsPerConnector()
    {
        var h = Build(c => c.MaxUploadsPerMinute = 2);

        h.Service.Upload(Bearer(TokenA), Png(), "image/png");
        h.Service.Upload(Bearer(TokenA), Png(), "image/png");
        Assert.Equal(
            ConnectorAttachmentAccessError.RateLimited,
            h.Service.Upload(Bearer(TokenA), Png(), "image/png").Error);

        // One connector exhausting its budget must not deny service to another.
        Assert.True(h.Service.Upload(Bearer(TokenB), Png(), "image/png").Success);
    }

    [Fact]
    public void Upload_RateLimitAppliesBeforeContentValidation()
    {
        // Otherwise a connector can spend the Bridge's CPU on sniffing uploads it may not make.
        var h = Build(c => c.MaxUploadsPerMinute = 1);

        h.Service.Upload(Bearer(TokenA), Png(), "image/png");

        var result = h.Service.Upload(Bearer(TokenA), "garbage"u8.ToArray(), "image/png");

        Assert.Equal(ConnectorAttachmentAccessError.RateLimited, result.Error);
    }

    [Fact]
    public void Upload_UnauthorizedRequestsDoNotConsumeRateBudget()
    {
        var h = Build(c => c.MaxUploadsPerMinute = 2);

        for (var i = 0; i < 10; i++)
        {
            h.Service.Upload(Bearer("bogus"), Png(), "image/png");
        }

        Assert.True(h.Service.Upload(Bearer(TokenA), Png(), "image/png").Success);
    }

    [Fact]
    public void Upload_BeyondStorageQuota_ReportsQuotaExceeded()
    {
        var h = Build(c =>
        {
            c.MaxAttachmentBytes = 1024;
            c.MaxStoredBytesPerConnector = 1024;
        });

        Assert.True(h.Service.Upload(Bearer(TokenA), Png(1024), "image/png").Success);

        Assert.Equal(
            ConnectorAttachmentAccessError.QuotaExceeded,
            h.Service.Upload(Bearer(TokenA), Png(1024), "image/png").Error);
    }

    // ── Master switch ────────────────────────────────────────────────

    [Fact]
    public void Upload_WhenMediaDisabled_IsRefusedBeforeAuthentication()
    {
        var h = Build(c => c.Enabled = false);

        Assert.Equal(
            ConnectorAttachmentAccessError.MediaDisabled,
            h.Service.Upload(Bearer(TokenA), Png(), "image/png").Error);
    }

    [Fact]
    public void Fetch_WhenMediaDisabled_IsRefused()
    {
        var h = Build(c => c.Enabled = false);

        Assert.Equal(
            ConnectorAttachmentAccessError.MediaDisabled,
            h.Service.Fetch(Bearer(TokenA), "att_whatever").Error);
    }

    // ── Metadata hygiene ─────────────────────────────────────────────

    [Fact]
    public void Upload_FileNameIsSanitised()
    {
        var h = Build();

        var upload = h.Service.Upload(Bearer(TokenA), Png(), "image/png", "../../etc/passwd.png");
        var fetch = h.Service.Fetch(Bearer(TokenA), upload.Handle);

        Assert.DoesNotContain('/', fetch.Content!.FileName!);
    }
}
