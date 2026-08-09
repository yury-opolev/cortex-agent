using Cortex.Contained.Bridge.Connectors.Media;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Connectors.Media;

/// <summary>
/// Tests <see cref="ConnectorAttachmentStore"/>. A handle is a bearer capability handed to an
/// untrusted local process, so the security properties — unguessable, channel-scoped, single-use,
/// expiring, quota-bounded — are asserted individually and from the attacker's side.
/// </summary>
public sealed class ConnectorAttachmentStoreTests
{
    private const string ChannelA = "plugin:terminal:default";
    private const string ChannelB = "plugin:other:default";
    private const int OneMebibyte = 1_048_576;

    private static (ConnectorAttachmentStore Store, FakeTimeProvider Time) Build(
        Action<ConnectorMediaConfig>? configure = null)
    {
        var config = new ConnectorMediaConfig();
        configure?.Invoke(config);

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-09T12:00:00Z", null));
        var store = new ConnectorAttachmentStore(
            ConnectorMediaPolicy.From(config, OneMebibyte),
            time,
            NullLogger<ConnectorAttachmentStore>.Instance);

        return (store, time);
    }

    private static ConnectorAttachmentContent PngContent(int totalBytes = 64, string? fileName = "shot.png")
    {
        var data = new byte[totalBytes];
        ImageContentSnifferTests.Png.AsSpan(0, Math.Min(8, totalBytes)).CopyTo(data);

        return new ConnectorAttachmentContent
        {
            MimeType = "image/png",
            Data = data,
            FileName = fileName,
        };
    }

    // ── Issue / resolve round trip ───────────────────────────────────

    [Fact]
    public void Issue_ThenResolve_ReturnsTheStoredContent()
    {
        var (store, _) = Build();
        var content = PngContent();

        var handle = store.Issue(ChannelA, content);

        Assert.NotNull(handle);
        var resolved = store.Resolve(handle, ChannelA);
        Assert.NotNull(resolved);
        Assert.Equal(content.Data, resolved.Data);
        Assert.Equal("image/png", resolved.MimeType);
        Assert.Equal("shot.png", resolved.FileName);
    }

    [Fact]
    public void Issue_ProducesAnUnguessablePrefixedHandle()
    {
        var (store, _) = Build();

        var handle = store.Issue(ChannelA, PngContent())!;

        Assert.StartsWith("att_", handle, StringComparison.Ordinal);

        // 128 bits base64-url encodes to 22 characters, plus the 4-character prefix.
        Assert.Equal(26, handle.Length);
        Assert.True(
            ConnectorAttachmentValidator.IsWellFormedHandle(handle),
            "an issued handle must pass the validator's own well-formedness check");
    }

    [Fact]
    public void Issue_HandlesAreUnique()
    {
        var (store, _) = Build();

        var handles = Enumerable.Range(0, 200).Select(_ => store.Issue(ChannelA, PngContent(8))).ToList();

        Assert.All(handles, h => Assert.NotNull(h));
        Assert.Equal(handles.Count, handles.Distinct(StringComparer.Ordinal).Count());
    }

    // ── Single use ───────────────────────────────────────────────────

    [Fact]
    public void Resolve_IsSingleUse()
    {
        var (store, _) = Build();
        var handle = store.Issue(ChannelA, PngContent())!;

        Assert.NotNull(store.Resolve(handle, ChannelA));

        // A leaked handle must not be replayable.
        Assert.Null(store.Resolve(handle, ChannelA));
        Assert.Equal(0, store.Count);
    }

    // ── Channel scoping ──────────────────────────────────────────────

    [Fact]
    public void Resolve_FromAnotherChannel_ReturnsNull()
    {
        var (store, _) = Build();
        var handle = store.Issue(ChannelA, PngContent())!;

        Assert.Null(store.Resolve(handle, ChannelB));
    }

    [Fact]
    public void Resolve_FromAnotherChannel_DoesNotConsumeTheEntry()
    {
        // Otherwise one connector could destroy another's attachments simply by guessing at
        // handles — a denial of service that needs no read access at all.
        var (store, _) = Build();
        var handle = store.Issue(ChannelA, PngContent())!;

        Assert.Null(store.Resolve(handle, ChannelB));
        Assert.NotNull(store.Resolve(handle, ChannelA));
    }

    [Fact]
    public void Resolve_UnknownHandle_ReturnsNull()
    {
        var (store, _) = Build();

        Assert.Null(store.Resolve("att_doesnotexist", ChannelA));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_BlankHandle_ReturnsNull(string? handle)
    {
        var (store, _) = Build();

        Assert.Null(store.Resolve(handle!, ChannelA));
    }

    // ── Expiry ───────────────────────────────────────────────────────

    [Fact]
    public void Resolve_AfterTtl_ReturnsNull()
    {
        var (store, time) = Build(c => c.HandleTtl = TimeSpan.FromMinutes(10));
        var handle = store.Issue(ChannelA, PngContent())!;

        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.Null(store.Resolve(handle, ChannelA));
    }

    [Fact]
    public void Resolve_JustBeforeTtl_StillWorks()
    {
        var (store, time) = Build(c => c.HandleTtl = TimeSpan.FromMinutes(10));
        var handle = store.Issue(ChannelA, PngContent())!;

        time.Advance(TimeSpan.FromMinutes(9));

        Assert.NotNull(store.Resolve(handle, ChannelA));
    }

    [Fact]
    public void ExpiredEntries_AreSweptWithoutBeingResolved()
    {
        // Unreferenced uploads must not accumulate just because nobody asks for them.
        var (store, time) = Build(c => c.HandleTtl = TimeSpan.FromMinutes(10));
        store.Issue(ChannelA, PngContent());
        store.Issue(ChannelA, PngContent());
        Assert.Equal(2, store.Count);

        time.Advance(TimeSpan.FromMinutes(11));
        time.Advance(ConnectorAttachmentStore.SweepInterval);

        Assert.Equal(0, store.Count);
    }

    // ── Quota ────────────────────────────────────────────────────────

    [Fact]
    public void Issue_BeyondChannelQuota_ReturnsNull()
    {
        var (store, _) = Build(c =>
        {
            c.MaxAttachmentBytes = 1024;
            c.MaxStoredBytesPerConnector = 2048;
        });

        Assert.NotNull(store.Issue(ChannelA, PngContent(1024)));
        Assert.NotNull(store.Issue(ChannelA, PngContent(1024)));

        // Third upload would exceed the quota.
        Assert.Null(store.Issue(ChannelA, PngContent(1024)));
    }

    [Fact]
    public void Issue_QuotaIsPerChannelNotGlobal()
    {
        var (store, _) = Build(c =>
        {
            c.MaxAttachmentBytes = 1024;
            c.MaxStoredBytesPerConnector = 1024;
        });

        Assert.NotNull(store.Issue(ChannelA, PngContent(1024)));
        Assert.Null(store.Issue(ChannelA, PngContent(1024)));

        // One connector exhausting its quota must not deny service to another.
        Assert.NotNull(store.Issue(ChannelB, PngContent(1024)));
    }

    [Fact]
    public void Issue_QuotaIsReclaimedOnConsume()
    {
        var (store, _) = Build(c =>
        {
            c.MaxAttachmentBytes = 1024;
            c.MaxStoredBytesPerConnector = 1024;
        });

        var handle = store.Issue(ChannelA, PngContent(1024))!;
        Assert.Null(store.Issue(ChannelA, PngContent(1024)));

        store.Resolve(handle, ChannelA);

        Assert.NotNull(store.Issue(ChannelA, PngContent(1024)));
    }

    [Fact]
    public void Issue_QuotaIsReclaimedOnExpiry()
    {
        var (store, time) = Build(c =>
        {
            c.MaxAttachmentBytes = 1024;
            c.MaxStoredBytesPerConnector = 1024;
            c.HandleTtl = TimeSpan.FromMinutes(10);
        });

        store.Issue(ChannelA, PngContent(1024));
        Assert.Null(store.Issue(ChannelA, PngContent(1024)));

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.NotNull(store.Issue(ChannelA, PngContent(1024)));
    }

    [Fact]
    public void LiveBytesForChannel_TracksStoredContent()
    {
        var (store, _) = Build();

        Assert.Equal(0, store.LiveBytesForChannel(ChannelA));

        store.Issue(ChannelA, PngContent(64));
        store.Issue(ChannelA, PngContent(32));

        Assert.Equal(96, store.LiveBytesForChannel(ChannelA));
        Assert.Equal(0, store.LiveBytesForChannel(ChannelB));
    }

    // ── Content validation on the way in ─────────────────────────────

    [Fact]
    public void Issue_ContentNotMatchingItsDeclaredType_IsRefused()
    {
        // The store must not be a way around the allow-list that the wire path enforces.
        var (store, _) = Build();
        var content = new ConnectorAttachmentContent
        {
            MimeType = "image/png",
            Data = "<html>not an image</html>"u8.ToArray(),
        };

        Assert.Null(store.Issue(ChannelA, content));
    }

    [Fact]
    public void Issue_DisallowedMimeType_IsRefused()
    {
        var (store, _) = Build(c => c.AllowedMimeTypes = ["image/jpeg"]);

        Assert.Null(store.Issue(ChannelA, PngContent()));
    }

    [Fact]
    public void Issue_OversizeContent_IsRefused()
    {
        var (store, _) = Build(c => c.MaxAttachmentBytes = 1024);

        Assert.Null(store.Issue(ChannelA, PngContent(4096)));
    }

    [Fact]
    public void Issue_EmptyContent_IsRefused()
    {
        var (store, _) = Build();
        var empty = new ConnectorAttachmentContent { MimeType = "image/png", Data = [] };

        Assert.Null(store.Issue(ChannelA, empty));
    }

    [Fact]
    public void Issue_WhenMediaDisabled_IsRefused()
    {
        var (store, _) = Build(c => c.Enabled = false);

        Assert.Null(store.Issue(ChannelA, PngContent()));
    }

    // ── Eviction ─────────────────────────────────────────────────────

    [Fact]
    public void EvictChannel_DropsOnlyThatChannelsEntries()
    {
        var (store, _) = Build();
        var handleA = store.Issue(ChannelA, PngContent())!;
        var handleB = store.Issue(ChannelB, PngContent())!;

        store.EvictChannel(ChannelA);

        Assert.Null(store.Resolve(handleA, ChannelA));
        Assert.NotNull(store.Resolve(handleB, ChannelB));
    }

    // ── Disposal ─────────────────────────────────────────────────────

    [Fact]
    public void Dispose_ClearsStoredContentAndIsIdempotent()
    {
        var (store, _) = Build();
        var handle = store.Issue(ChannelA, PngContent())!;

        store.Dispose();
        store.Dispose();

        Assert.Equal(0, store.Count);
        Assert.Null(store.Resolve(handle, ChannelA));
    }

    // ── Concurrency ──────────────────────────────────────────────────

    [Fact]
    public void Consume_UnderConcurrency_SucceedsExactlyOnce()
    {
        var (store, _) = Build();
        var handle = store.Issue(ChannelA, PngContent())!;

        var successes = 0;
        Parallel.For(0, 64, _ =>
        {
            if (store.Resolve(handle, ChannelA) is not null)
            {
                Interlocked.Increment(ref successes);
            }
        });

        Assert.Equal(1, successes);
    }

    [Fact]
    public void Issue_UnderConcurrency_NeverExceedsTheQuota()
    {
        // Without an atomic check-and-insert, concurrent uploads each observe headroom that only
        // one of them can actually have, and the quota silently overruns.
        var (store, _) = Build(c =>
        {
            c.MaxAttachmentBytes = 1024;
            c.MaxStoredBytesPerConnector = 8 * 1024;
        });

        Parallel.For(0, 64, _ => store.Issue(ChannelA, PngContent(1024)));

        Assert.True(
            store.LiveBytesForChannel(ChannelA) <= 8 * 1024,
            $"quota overrun: {store.LiveBytesForChannel(ChannelA)} bytes held");
    }
}
