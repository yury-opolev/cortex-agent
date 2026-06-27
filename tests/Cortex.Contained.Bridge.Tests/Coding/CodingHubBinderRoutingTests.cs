using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Bridge.Tests.Coding;

// ============================================================================
// CodaSessionManager.GetSessionTenantId — unit tests
// ============================================================================

/// <summary>
/// Tests for <see cref="CodaSessionManager.GetSessionTenantId"/>:
/// <list type="bullet">
///   <item>Returns the tenant for a live registered session.</item>
///   <item>Falls back to <c>knownSessions</c> metadata after the session is ended.</item>
///   <item>Returns <c>null</c> for an entirely unknown session id.</item>
/// </list>
/// </summary>
public sealed class GetSessionTenantIdTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(
        Path.GetTempPath(),
        "cortex-routing-" + Guid.NewGuid().ToString("N"));

    private CodaSessionManager NewManager()
    {
        Directory.CreateDirectory(this.tempRoot);

        var options = Substitute.For<IOptionsMonitor<CodaOptions>>();
        options.CurrentValue.Returns(new CodaOptions());

        var foldersStore = new CodingFoldersStore(
            Path.Combine(this.tempRoot, "coding-folders.json"));
        var modelStore = new CodaModelSettingsStore(
            Path.Combine(this.tempRoot, "coda-model.json"));

        return new CodaSessionManager(
            NullLoggerFactory.Instance,
            options,
            foldersStore,
            modelStore,
            machineHomeDir: Path.Combine(this.tempRoot, "home"));
    }

    private static CodaSession MakeSession(string sessionId, string tenantId)
    {
        var workDir = Directory.CreateTempSubdirectory().FullName;
        return new CodaSession(
            sessionId,
            channelId: "chan-1",
            workingFolder: workDir,
            policy: CodingPolicy.YoloSafe,
            options: new CodaOptions(),
            logger: NullLogger<CodaSession>.Instance)
        {
            TenantId = tenantId,
        };
    }

    [Fact]
    public void GetSessionTenantId_LiveSession_ReturnsTenantId()
    {
        var manager = this.NewManager();
        var session = MakeSession("sess-1", "tenantA");
        manager.RegisterSessionForTesting(session);

        var result = manager.GetSessionTenantId("sess-1");

        Assert.Equal("tenantA", result);
    }

    [Fact]
    public void GetSessionTenantId_UnknownSessionId_ReturnsNull()
    {
        var manager = this.NewManager();

        var result = manager.GetSessionTenantId("no-such-session");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSessionTenantId_AfterEndAsync_ReturnsMetadataTenantId()
    {
        // Arrange: we need a session that was started via StartAsync so that RememberMetadata
        // is called (which persists TenantId in knownSessions).  Since we can't spawn a real
        // coda process, we simulate the metadata path by calling the internal test seam that
        // inserts directly into the live map, then registering through StartAsync-like metadata.
        //
        // Instead, we use a second registration path that exercises RememberMetadata indirectly:
        // we start a session via StartAsync so metadata is populated, then call EndAsync which
        // removes it from the live map but retains the metadata entry.  Because StartAsync spawns
        // a real process we cannot use it here.
        //
        // Solution: register via RegisterSessionForTesting (inserts into live map) then call
        // EndAsync, which removes it from the live map.  EndAsync updates the metadata State to
        // Ended but does NOT create a new metadata entry if none exists.  Therefore, to test the
        // metadata fallback we must use the internal RegisterMetadataForTesting seam added by
        // this change-set.

        var manager = this.NewManager();
        var sessionId = "sess-meta-1";

        // Register both the live session and its metadata (new seam).
        var session = MakeSession(sessionId, "tenantB");
        manager.RegisterSessionForTesting(session);
        manager.RegisterMetadataForTesting(sessionId, "tenantB");

        // End the session — removes from live map, flips metadata State to Ended.
        await manager.EndAsync(sessionId, CancellationToken.None);

        // Live map no longer has it; should fall back to metadata.
        var result = manager.GetSessionTenantId(sessionId);

        Assert.Equal("tenantB", result);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempRoot, recursive: true); } catch { }
    }
}

// ============================================================================
// CodingHubBinder.SelectTargets — pure routing helper
// ============================================================================

/// <summary>
/// Tests for <see cref="CodingHubBinder.SelectTargets"/>:
/// <list type="bullet">
///   <item>Returns only the matching tenant's clients when tenantId is known.</item>
///   <item>Returns ALL clients as a safety fallback when tenantId is <c>null</c>.</item>
///   <item>Returns an empty sequence when no client is mapped to the requested tenant.</item>
///   <item>Ordinal (case-sensitive) tenant comparison.</item>
/// </list>
/// </summary>
public sealed class SelectTargetsTests
{
    private static HubClient NewClient() =>
        new(NullLogger<HubClient>.Instance);

    [Fact]
    public void SelectTargets_KnownTenant_ReturnsOnlyMatchingClients()
    {
        var clientA = NewClient();
        var clientB = NewClient();
        var clients = new Dictionary<HubClient, string>
        {
            [clientA] = "tenantA",
            [clientB] = "tenantB",
        };

        var result = CodingHubBinder.SelectTargets(clients, "tenantA").ToList();

        Assert.Single(result);
        Assert.Same(clientA, result[0]);
    }

    [Fact]
    public void SelectTargets_NullTenant_ReturnsAllClients()
    {
        var clientA = NewClient();
        var clientB = NewClient();
        var clients = new Dictionary<HubClient, string>
        {
            [clientA] = "tenantA",
            [clientB] = "tenantB",
        };

        var result = CodingHubBinder.SelectTargets(clients, tenantId: null).ToList();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SelectTargets_NoMatchingTenant_ReturnsEmpty()
    {
        var clientA = NewClient();
        var clients = new Dictionary<HubClient, string>
        {
            [clientA] = "tenantA",
        };

        var result = CodingHubBinder.SelectTargets(clients, "tenantZ").ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void SelectTargets_TenantComparison_IsCaseSensitive()
    {
        var clientA = NewClient();
        var clients = new Dictionary<HubClient, string>
        {
            [clientA] = "tenantA",
        };

        // "tenanta" (lowercase 'a') must NOT match "tenantA".
        var result = CodingHubBinder.SelectTargets(clients, "tenanta").ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void SelectTargets_MultipleSameTenantClients_ReturnsAll()
    {
        var clientA1 = NewClient();
        var clientA2 = NewClient();
        var clientB = NewClient();
        var clients = new Dictionary<HubClient, string>
        {
            [clientA1] = "tenantA",
            [clientA2] = "tenantA",
            [clientB] = "tenantB",
        };

        var result = CodingHubBinder.SelectTargets(clients, "tenantA").ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(clientA1, result);
        Assert.Contains(clientA2, result);
    }
}
