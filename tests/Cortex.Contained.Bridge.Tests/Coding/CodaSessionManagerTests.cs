using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Bridge.Tests.Coding;

/// <summary>
/// Tests for <see cref="CodaSessionManager.EffectiveOptions"/>: the UI MCP-policy store
/// (coda-mcp.json) overrides the cortex.yml <c>Coding:Coda:Mcp</c> value when set, and falls
/// back to it when unset. coda is single-provider and self-resolves its provider, so the
/// Bridge no longer resolves/pins one — this class covers only the MCP-policy override that
/// survives that removal.
/// </summary>
public sealed class CodaSessionManagerEffectiveOptionsTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "cortex-b1-" + Guid.NewGuid().ToString("N"));

    private string McpFilePath => Path.Combine(this.tempRoot, "coda-mcp.json");

    private CodaSessionManager NewManager(CodaMcpPolicy yamlMcp = CodaMcpPolicy.Host)
    {
        Directory.CreateDirectory(this.tempRoot);

        var options = Substitute.For<IOptionsMonitor<CodaOptions>>();
        options.CurrentValue.Returns(new CodaOptions { Mcp = yamlMcp });

        var foldersStore = new CodingFoldersStore(Path.Combine(this.tempRoot, "coding-folders.json"));
        var mcpStore = new CodaMcpSettingsStore(this.McpFilePath);

        return new CodaSessionManager(
            NullLoggerFactory.Instance,
            options,
            foldersStore,
            mcpStore);
    }

    private void WriteMcpUiStore(CodaMcpPolicy? mcp, string? curatedDir)
    {
        new CodaMcpSettingsStore(this.McpFilePath).Set(mcp, curatedDir);
    }

    [Fact]
    public void EffectiveOptions_UiMcpStore_OverridesYamlPolicy()
    {
        var manager = this.NewManager(yamlMcp: CodaMcpPolicy.Host);
        this.WriteMcpUiStore(CodaMcpPolicy.Curated, "C:\\curated");

        var effective = manager.EffectiveOptions();

        Assert.Equal(CodaMcpPolicy.Curated, effective.Mcp);
        Assert.Equal("C:\\curated", effective.CuratedMcpDir);
    }

    [Fact]
    public void EffectiveOptions_FallsBackToYamlPolicy_WhenUiUnset()
    {
        var manager = this.NewManager(yamlMcp: CodaMcpPolicy.Off);

        var effective = manager.EffectiveOptions();

        Assert.Equal(CodaMcpPolicy.Off, effective.Mcp); // no UI override → cortex.yml value
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempRoot, recursive: true); } catch { }
    }
}

/// <summary>
/// Tests for the pure policy-resolution helper extracted from <see cref="CodaSessionManager"/>.
/// The manager itself spawns real processes so its lifecycle tests live in the FakeCoda
/// integration suite (Task 6).  Here we test the testable seam directly.
/// </summary>
public sealed class CodaSessionPolicyTests
{
    // -----------------------------------------------------------------------
    // Resolve — new signature: (CodingPolicy? requested, CodingPolicy ceiling)
    // -----------------------------------------------------------------------

    [Fact]
    public void Resolve_null_request_with_yolo_safe_ceiling_returns_yolo_safe()
    {
        // Key fix: folder policy is the default when nothing is requested.
        var (policy, error) = CodaSessionPolicy.Resolve(requested: null, ceiling: CodingPolicy.YoloSafe);

        Assert.Null(error);
        Assert.Equal(CodingPolicy.YoloSafe, policy);
    }

    [Fact]
    public void Resolve_null_request_with_yolo_ceiling_returns_yolo()
    {
        var (policy, error) = CodaSessionPolicy.Resolve(requested: null, ceiling: CodingPolicy.Yolo);

        Assert.Null(error);
        Assert.Equal(CodingPolicy.Yolo, policy);
    }

    [Fact]
    public void Resolve_null_request_with_prompt_ceiling_returns_prompt()
    {
        var (policy, error) = CodaSessionPolicy.Resolve(requested: null, ceiling: CodingPolicy.Prompt);

        Assert.Null(error);
        Assert.Equal(CodingPolicy.Prompt, policy);
    }

    [Fact]
    public void Resolve_prompt_request_with_yolo_safe_ceiling_returns_prompt()
    {
        // Explicit stricter policy honored.
        var (policy, error) = CodaSessionPolicy.Resolve(requested: CodingPolicy.Prompt, ceiling: CodingPolicy.YoloSafe);

        Assert.Null(error);
        Assert.Equal(CodingPolicy.Prompt, policy);
    }

    [Fact]
    public void Resolve_yolo_safe_request_with_yolo_safe_ceiling_returns_yolo_safe()
    {
        var (policy, error) = CodaSessionPolicy.Resolve(requested: CodingPolicy.YoloSafe, ceiling: CodingPolicy.YoloSafe);

        Assert.Null(error);
        Assert.Equal(CodingPolicy.YoloSafe, policy);
    }

    [Fact]
    public void Resolve_yolo_request_with_yolo_safe_ceiling_returns_error()
    {
        // Exceeds ceiling — error.
        var (policy, error) = CodaSessionPolicy.Resolve(requested: CodingPolicy.Yolo, ceiling: CodingPolicy.YoloSafe);

        Assert.NotNull(error);
        Assert.Equal(CodingAgentErrorCodes.FolderNotAllowed, error.Value.ErrorCode);
        Assert.Equal(default, policy);
    }

    [Fact]
    public void Resolve_yolo_request_with_prompt_ceiling_returns_error()
    {
        var (policy, error) = CodaSessionPolicy.Resolve(requested: CodingPolicy.Yolo, ceiling: CodingPolicy.Prompt);

        Assert.NotNull(error);
        Assert.Equal(CodingAgentErrorCodes.FolderNotAllowed, error.Value.ErrorCode);
    }

    [Fact]
    public void Resolve_yolo_request_with_yolo_ceiling_returns_yolo()
    {
        var (policy, error) = CodaSessionPolicy.Resolve(requested: CodingPolicy.Yolo, ceiling: CodingPolicy.Yolo);

        Assert.Null(error);
        Assert.Equal(CodingPolicy.Yolo, policy);
    }

    // -----------------------------------------------------------------------
    // IsWithinCeiling — the underlying extension is exercised here too
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(CodingPolicy.Prompt, CodingPolicy.Prompt, true)]
    [InlineData(CodingPolicy.Prompt, CodingPolicy.YoloSafe, true)]
    [InlineData(CodingPolicy.Prompt, CodingPolicy.Yolo, true)]
    [InlineData(CodingPolicy.YoloSafe, CodingPolicy.YoloSafe, true)]
    [InlineData(CodingPolicy.YoloSafe, CodingPolicy.Yolo, true)]
    [InlineData(CodingPolicy.Yolo, CodingPolicy.Yolo, true)]
    [InlineData(CodingPolicy.YoloSafe, CodingPolicy.Prompt, false)]
    [InlineData(CodingPolicy.Yolo, CodingPolicy.Prompt, false)]
    [InlineData(CodingPolicy.Yolo, CodingPolicy.YoloSafe, false)]
    public void IsWithinCeiling_matches_expected(CodingPolicy requested, CodingPolicy ceiling, bool expected)
    {
        Assert.Equal(expected, requested.IsWithinCeiling(ceiling));
    }
}

/// <summary>
/// Tests that verify the <see cref="CodaSessionManager"/> ceiling wiring:
/// ceiling is derived from <see cref="CodingFoldersStore.GetCeiling"/> and enforced
/// via <see cref="CodaSessionPolicy.Resolve"/>. Also tests the hard allowlist gate
/// and child-folder inheritance.
/// </summary>
public sealed class CodaSessionManagerCeilingTests
{
    private static CodingFoldersStore NewStore()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        return new CodingFoldersStore(Path.Combine(dir, "coding-folders.json"));
    }

    // -----------------------------------------------------------------------
    // Hard allowlist gate
    // -----------------------------------------------------------------------

    /// <summary>
    /// A path NOT in any store entry is rejected by the allowlist gate.
    /// </summary>
    [Fact]
    public void IsAllowed_returns_false_for_unlisted_folder()
    {
        var store = NewStore();

        Assert.False(store.IsAllowed(@"C:\definitely\not\in\the\store"));
    }

    /// <summary>
    /// A CHILD folder of a listed root is allowed by <see cref="CodingFoldersStore.IsAllowed"/>.
    /// </summary>
    [Fact]
    public void IsAllowed_returns_true_for_child_of_listed_folder()
    {
        var store = NewStore();
        var root = Directory.CreateTempSubdirectory().FullName;
        store.Add(root, null, CodingPolicy.YoloSafe);

        var child = Path.Combine(root, "sub", "deep");

        Assert.True(store.IsAllowed(child));
    }

    // -----------------------------------------------------------------------
    // GetCeiling inherits policy to child folders
    // -----------------------------------------------------------------------

    /// <summary>
    /// <see cref="CodingFoldersStore.GetCeiling"/> for a CHILD path returns the parent entry's
    /// <see cref="CodingFolderEntry.DefaultPolicy"/>.
    /// </summary>
    [Fact]
    public void GetCeiling_returns_parent_policy_for_child_path()
    {
        var store = NewStore();
        var root = Directory.CreateTempSubdirectory().FullName;
        store.Add(root, null, CodingPolicy.YoloSafe);

        var child = Path.Combine(root, "nested", "project");

        Assert.Equal(CodingPolicy.YoloSafe, store.GetCeiling(child));
    }

    // -----------------------------------------------------------------------
    // Policy ceiling enforcement (with new Resolve signature)
    // -----------------------------------------------------------------------

    /// <summary>
    /// A folder configured with <see cref="CodingPolicy.Prompt"/> ceiling rejects a Yolo request.
    /// </summary>
    [Fact]
    public void PromptCeiling_folder_rejects_yolo_request()
    {
        var store = NewStore();
        var folder = Directory.CreateTempSubdirectory().FullName;
        store.Add(folder, label: "test", CodingPolicy.Prompt);

        var ceiling = store.GetCeiling(folder);
        var (_, error) = CodaSessionPolicy.Resolve(requested: CodingPolicy.Yolo, ceiling: ceiling);

        Assert.Equal(CodingPolicy.Prompt, ceiling);
        Assert.NotNull(error);
        Assert.Equal(CodingAgentErrorCodes.FolderNotAllowed, error.Value.ErrorCode);
    }

    /// <summary>
    /// A folder configured with <see cref="CodingPolicy.Yolo"/> ceiling accepts a Yolo request.
    /// </summary>
    [Fact]
    public void YoloCeiling_folder_accepts_yolo_request()
    {
        var store = NewStore();
        var folder = Directory.CreateTempSubdirectory().FullName;
        store.Add(folder, label: "test", CodingPolicy.Yolo);

        var ceiling = store.GetCeiling(folder);
        var (policy, error) = CodaSessionPolicy.Resolve(requested: CodingPolicy.Yolo, ceiling: ceiling);

        Assert.Equal(CodingPolicy.Yolo, ceiling);
        Assert.Null(error);
        Assert.Equal(CodingPolicy.Yolo, policy);
    }

    /// <summary>
    /// A folder configured with <see cref="CodingPolicy.YoloSafe"/> ceiling: default (null) resolves
    /// to YoloSafe; explicit Yolo is rejected.
    /// </summary>
    [Fact]
    public void YoloSafeCeiling_folder_default_resolves_to_yolo_safe_and_rejects_yolo()
    {
        var store = NewStore();
        var folder = Directory.CreateTempSubdirectory().FullName;
        store.Add(folder, label: "safe", CodingPolicy.YoloSafe);

        var ceiling = store.GetCeiling(folder);
        Assert.Equal(CodingPolicy.YoloSafe, ceiling);

        // Default (null) → folder's own policy is the effective policy.
        var (defaultPolicy, defaultError) = CodaSessionPolicy.Resolve(requested: null, ceiling: ceiling);
        Assert.Null(defaultError);
        Assert.Equal(CodingPolicy.YoloSafe, defaultPolicy);

        // Explicit stricter policy honored.
        var (strictPolicy, strictError) = CodaSessionPolicy.Resolve(requested: CodingPolicy.Prompt, ceiling: ceiling);
        Assert.Null(strictError);
        Assert.Equal(CodingPolicy.Prompt, strictPolicy);

        // Full Yolo request rejected by YoloSafe ceiling.
        var (_, yoloError) = CodaSessionPolicy.Resolve(requested: CodingPolicy.Yolo, ceiling: ceiling);
        Assert.NotNull(yoloError);
        Assert.Equal(CodingAgentErrorCodes.FolderNotAllowed, yoloError.Value.ErrorCode);
    }

    /// <summary>
    /// A path NOT in any store entry yields the most-restrictive <see cref="CodingPolicy.Prompt"/> ceiling.
    /// </summary>
    [Fact]
    public void Unlisted_folder_ceiling_is_most_restrictive_prompt()
    {
        var store = NewStore();

        var ceiling = store.GetCeiling(@"C:\definitely\not\in\the\store");

        Assert.Equal(CodingPolicy.Prompt, ceiling);
    }

    /// <summary>
    /// <see cref="CodingFoldersStore.IsAllowed"/> returns true for a registered folder
    /// and false for an unregistered one.
    /// </summary>
    [Fact]
    public void IsAllowed_reflects_store_contents()
    {
        var store = NewStore();
        var folder = Directory.CreateTempSubdirectory().FullName;
        store.Add(folder, null, CodingPolicy.YoloSafe);

        Assert.True(store.IsAllowed(Path.Combine(folder, "sub")));
        Assert.False(store.IsAllowed(@"C:\unrelated\path\xyz"));
    }

    /// <summary>
    /// <see cref="CodaSessionManager.ListFolders"/> returns the entries from the store.
    /// </summary>
    [Fact]
    public void ListFolders_returns_entries_with_label_and_policy()
    {
        var store = NewStore();
        var folder = Directory.CreateTempSubdirectory().FullName;
        store.Add(folder, label: "my-project", CodingPolicy.Yolo);

        var entries = store.Get();

        Assert.Single(entries);
        Assert.Equal("my-project", entries[0].Label);
        Assert.Equal(CodingPolicy.Yolo, entries[0].DefaultPolicy);
    }
}

/// <summary>
/// Verifies that a coda process that cannot even spawn (bogus binary path) fails fast with a
/// definitive <c>coda_start_failed</c> and leaves no phantom session behind.
/// </summary>
public sealed class CodaSessionManagerStartFailureTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "cortex-startfail-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.tempRoot, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task StartAsync_SpawnFails_ThrowsStartFailed_AndLeavesNoPhantom()
    {
        Directory.CreateDirectory(this.tempRoot);
        var workingFolder = Path.Combine(this.tempRoot, "repo");
        Directory.CreateDirectory(workingFolder);

        var foldersStore = new CodingFoldersStore(Path.Combine(this.tempRoot, "coding-folders.json"));
        foldersStore.Add(workingFolder, label: "repo", CodingPolicy.Yolo);

        var bogusBinary = Path.Combine(this.tempRoot, "does-not-exist-coda.exe");
        var options = Substitute.For<IOptionsMonitor<CodaOptions>>();
        options.CurrentValue.Returns(new CodaOptions
        {
            CodaBinaryPath = bogusBinary,
            StartTimeoutSeconds = 5,
        });

        var manager = new CodaSessionManager(
            NullLoggerFactory.Instance, options, foldersStore);

        var request = new CodingStartRequest { ChannelId = "c1", WorkingFolder = workingFolder, RequestedPolicy = CodingPolicy.Yolo };

        var ex = await Assert.ThrowsAsync<CodingAgentException>(
            () => manager.StartAsync("tenant-1", request, CancellationToken.None));

        Assert.Equal(CodingAgentErrorCodes.StartFailed, ex.ErrorCode);
        Assert.Contains("No session is running", ex.Message);
        Assert.DoesNotContain(
            manager.ListSessions(),
            s => s.State is not (CodingSessionState.Crashed or CodingSessionState.Ended));
    }

    /// <summary>
    /// Proves the <see cref="CodaSessionAdmission.CheckTenantCeiling"/> cap wiring inside
    /// <see cref="CodaSessionManager.StartAsync"/> is enforced PER TENANT, not globally: tenantA
    /// filled to its ceiling is rejected with <c>MaxSessionsReached</c> before any spawn is
    /// attempted, while tenantB (an empty budget) passes the cap gate for the exact same request
    /// and only then fails at the spawn step (bogus binary → <c>StartFailed</c>) — the same
    /// no-real-process pattern as <see cref="StartAsync_SpawnFails_ThrowsStartFailed_AndLeavesNoPhantom"/>.
    /// </summary>
    [Fact]
    public async Task StartAsync_Cap_IsEnforcedPerTenant_NotGlobally()
    {
        Directory.CreateDirectory(this.tempRoot);
        var workingFolder = Path.Combine(this.tempRoot, "repo");
        Directory.CreateDirectory(workingFolder);

        var foldersStore = new CodingFoldersStore(Path.Combine(this.tempRoot, "coding-folders.json"));
        foldersStore.Add(workingFolder, label: "repo", CodingPolicy.Yolo);

        var bogusBinary = Path.Combine(this.tempRoot, "does-not-exist-coda.exe");
        var options = Substitute.For<IOptionsMonitor<CodaOptions>>();
        options.CurrentValue.Returns(new CodaOptions
        {
            CodaBinaryPath = bogusBinary,
            StartTimeoutSeconds = 5,
            MaxSessions = 1,
        });

        var manager = new CodaSessionManager(
            NullLoggerFactory.Instance, options, foldersStore);

        // Fill tenantA's per-tenant budget (MaxSessions = 1) with a pre-built, non-started session.
        var filler = new CodaSession(
            Guid.NewGuid().ToString("D"),
            channelId: "chan-filler",
            workingFolder: workingFolder,
            policy: CodingPolicy.Yolo,
            options: new CodaOptions(),
            logger: NullLogger<CodaSession>.Instance)
        {
            TenantId = "tenantA",
        };
        manager.RegisterSessionForTesting(filler);

        var request = new CodingStartRequest
        {
            ChannelId = "chan-new",
            WorkingFolder = workingFolder,
            RequestedPolicy = CodingPolicy.Yolo,
        };

        // tenantA is at its per-tenant ceiling → MaxSessionsReached (gate hit before any spawn).
        var exA = await Assert.ThrowsAsync<CodingAgentException>(
            () => manager.StartAsync("tenantA", request, CancellationToken.None));
        Assert.Equal(CodingAgentErrorCodes.MaxSessionsReached, exA.ErrorCode);

        // tenantB has its own (empty) budget → passes the cap gate, then fails at the spawn step
        // because of the bogus binary. Proves the cap is per-tenant, not global.
        var exB = await Assert.ThrowsAsync<CodingAgentException>(
            () => manager.StartAsync("tenantB", request, CancellationToken.None));
        Assert.Equal(CodingAgentErrorCodes.StartFailed, exB.ErrorCode);
    }
}

/// <summary>
/// Verifies that <see cref="CodaSessionManager.SendMessageAsync"/> does not pretend to deliver a
/// message to a dead session — it reports <c>session_not_ready</c> rather than a false success or
/// a misleading "busy".
/// </summary>
public sealed class CodaSessionManagerSendReadinessTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "cortex-sendready-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.tempRoot, recursive: true); } catch { /* ignore */ }
    }

    private CodaSessionManager NewManager()
    {
        Directory.CreateDirectory(this.tempRoot);
        var options = Substitute.For<IOptionsMonitor<CodaOptions>>();
        options.CurrentValue.Returns(new CodaOptions());
        return new CodaSessionManager(
            NullLoggerFactory.Instance,
            options,
            new CodingFoldersStore(Path.Combine(this.tempRoot, "coding-folders.json")));
    }

    [Fact]
    public async Task SendMessage_ToCrashedSession_ThrowsSessionNotReady()
    {
        var (server, clientStream) = FakeCoda.FakeCodaServer.Create();
        await using var _ = server;
        var connection = new CodaJsonRpcConnection(clientStream, clientStream);
        var session = new CodaSession(
            "dead-session", "c1", Path.Combine(this.tempRoot, "wf"), CodingPolicy.Prompt, connection, NullLogger<CodaSession>.Instance);

        var errorSignal = new TaskCompletionSource<CodaErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Error += evt => errorSignal.TrySetResult(evt);

        server.Scenario = FakeCoda.FakeCodaScenario.Crash;
        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await session.WriteUserMessageAsync("go", CancellationToken.None);
        await errorSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CodingSessionState.Crashed, session.State);

        var manager = this.NewManager();
        manager.RegisterSessionForTesting(session);

        var ex = await Assert.ThrowsAsync<CodingAgentException>(() =>
            manager.SendMessageAsync(new CodingSendRequest { SessionId = "dead-session", Message = "hello" }, CancellationToken.None));

        Assert.Equal(CodingAgentErrorCodes.SessionNotReady, ex.ErrorCode);
        Assert.Contains("No message was delivered", ex.Message);
    }

    [Fact]
    public async Task Crash_ReapsOwnedCodaProcess()
    {
        var (server, clientStream) = FakeCoda.FakeCodaServer.Create();
        await using var _ = server;
        var connection = new CodaJsonRpcConnection(clientStream, clientStream);
        var session = new CodaSession(
            "reap-session", "c1", Path.Combine(this.tempRoot, "wf"), CodingPolicy.Prompt, connection, NullLogger<CodaSession>.Instance);

        // A real, long-running stand-in for the coda process: cmd.exe blocks reading redirected stdin.
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        session.AttachProcessForTesting(proc);

        var errorSignal = new TaskCompletionSource<CodaErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Error += evt => errorSignal.TrySetResult(evt);

        server.Scenario = FakeCoda.FakeCodaScenario.Crash;
        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await session.WriteUserMessageAsync("go", CancellationToken.None);
        await errorSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var exited = proc.WaitForExit(5000);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }

        Assert.True(exited, "a crash must reap (kill) the owned coda process so it does not orphan");
    }
}

/// <summary>
/// Verifies <see cref="CodaSessionManager.GetHistoryAsync"/> pass-through to a live session
/// (full transcript and incremental-since-cursor) and the <c>no_active_session</c> guard when
/// the session is not live. Uses a <see cref="FakeCoda.FakeCodaServer"/> over an in-memory
/// stream so no real coda process is spawned.
/// </summary>
public sealed class CodaSessionManagerHistoryTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "cortex-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.tempRoot, recursive: true); } catch { /* ignore */ }
    }

    private CodaSessionManager NewManager()
    {
        Directory.CreateDirectory(this.tempRoot);
        var options = Substitute.For<IOptionsMonitor<CodaOptions>>();
        options.CurrentValue.Returns(new CodaOptions());
        return new CodaSessionManager(
            NullLoggerFactory.Instance,
            options,
            new CodingFoldersStore(Path.Combine(this.tempRoot, "coding-folders.json")));
    }

    private async Task<(CodaSessionManager Manager, FakeCoda.FakeCodaServer Server, CodaSession Session)> NewLiveSessionAsync(string sessionId)
    {
        var (server, clientStream) = FakeCoda.FakeCodaServer.Create();
        var connection = new CodaJsonRpcConnection(clientStream, clientStream);
        var session = new CodaSession(
            sessionId, "c1", Path.Combine(this.tempRoot, "wf"), CodingPolicy.Prompt, connection, NullLogger<CodaSession>.Instance);
        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var manager = this.NewManager();
        manager.RegisterSessionForTesting(session);
        return (manager, server, session);
    }

    [Fact]
    public async Task GetHistoryAsync_UnknownSession_ThrowsNoActiveSession()
    {
        var manager = this.NewManager();

        var ex = await Assert.ThrowsAsync<CodingAgentException>(() =>
            manager.GetHistoryAsync("no-such-session", sinceIndex: null, CancellationToken.None));

        Assert.Equal(CodingAgentErrorCodes.NoActiveSession, ex.ErrorCode);
    }

    [Fact]
    public async Task GetHistoryAsync_NoSinceIndex_ReturnsFullTranscriptAndNullNextIndex()
    {
        var (manager, server, session) = await this.NewLiveSessionAsync("hist-1");
        await using var _ = server;
        await using var __ = session;
        server.HistoryMessages =
        [
            ("user", "hello"),
            ("assistant", "hi there"),
        ];

        var history = await manager.GetHistoryAsync("hist-1", sinceIndex: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(history.NextIndex);
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal("user", history.Messages[0].Role);
        Assert.Equal("hello", history.Messages[0].Content);
        Assert.Equal("assistant", history.Messages[1].Role);
        Assert.Equal("hi there", history.Messages[1].Content);
    }

    [Fact]
    public async Task SendMessageAsync_WhileWorking_SteersTheRunningTurn()
    {
        var (manager, server, session) = await this.NewLiveSessionAsync("steer-1");
        await using var _ = server;
        await using var __ = session;

        // A turn that never completes keeps the session Working, so the next send lands mid-turn.
        server.Scenario = FakeCoda.FakeCodaScenario.Stall;
        await session.WriteUserMessageAsync("start work", CancellationToken.None);
        Assert.Equal(CodingSessionState.Working, session.State);

        var response = await manager.SendMessageAsync(
            new CodingSendRequest { SessionId = "steer-1", Message = "actually, prioritize the failing test" },
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // The mid-turn message was delivered as a steering comment — not rejected as "busy".
        Assert.True(response.Steered);
        Assert.Equal(CodingSessionState.Working, response.State);
        Assert.Contains("actually, prioritize the failing test", server.SteerComments);
    }

    [Fact]
    public async Task LimitReached_thenTurnComplete_ReturnsToIdle_AndAcceptsANewTurn()
    {
        var (manager, server, session) = await this.NewLiveSessionAsync("limit-1");
        await using var _ = server;
        await using var __ = session;

        server.Scenario = FakeCoda.FakeCodaScenario.LimitReached;
        await session.WriteUserMessageAsync("do work", CancellationToken.None);

        // The turn hit a limit then completed → the session is recoverable: back to Idle, NOT Crashed.
        await WaitForStateAsync(session, CodingSessionState.Idle, TimeSpan.FromSeconds(5));
        Assert.Equal(CodingSessionState.Idle, session.State);

        // A follow-up message is accepted as a real NEW turn (Steered=false), proving the fitbit-sync
        // regression (a limit-hit session refusing further messages) is fixed end-to-end.
        server.Scenario = FakeCoda.FakeCodaScenario.Happy;
        var response = await manager.SendMessageAsync(
            new CodingSendRequest { SessionId = "limit-1", Message = "continue" }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(response.Steered);
    }

    private static async Task WaitForStateAsync(CodaSession session, CodingSessionState target, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (session.State != target && sw.Elapsed < timeout)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task GetHistoryAsync_WithSinceIndex_ReturnsSliceAndNextIndex()
    {
        var (manager, server, session) = await this.NewLiveSessionAsync("hist-2");
        await using var _ = server;
        await using var __ = session;
        server.HistoryMessages =
        [
            ("user", "m0"),
            ("assistant", "m1"),
            ("user", "m2"),
            ("assistant", "m3"),
        ];

        var history = await manager.GetHistoryAsync("hist-2", sinceIndex: 2, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, history.NextIndex);
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal("m2", history.Messages[0].Content);
        Assert.Equal("m3", history.Messages[1].Content);
    }
}

/// <summary>
/// Verifies that <see cref="CodaSessionManager.ListSessions"/> and
/// <see cref="CodaSessionManager.GetStatus"/> report true liveness: a crashed coda process is
/// never surfaced as an active (<see cref="CodingSessionState.Idle"/>) session, neither while it
/// is still in the live map nor after it has been removed (the metadata-fallback path). Also
/// confirms the canonical stop-by-id path (<see cref="CodaSessionManager.EndAsync"/>).
/// Uses a <see cref="FakeCoda.FakeCodaServer"/> over an in-memory stream so no real coda
/// process is spawned.
/// </summary>
public sealed class CodaSessionManagerLivenessTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "cortex-liveness-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.tempRoot, recursive: true); } catch { /* ignore */ }
    }

    private CodaSessionManager NewManager()
    {
        Directory.CreateDirectory(this.tempRoot);
        var options = Substitute.For<IOptionsMonitor<CodaOptions>>();
        options.CurrentValue.Returns(new CodaOptions());
        return new CodaSessionManager(
            NullLoggerFactory.Instance,
            options,
            new CodingFoldersStore(Path.Combine(this.tempRoot, "coding-folders.json")));
    }

    /// <summary>
    /// Drives a manager-registered session to <see cref="CodingSessionState.Crashed"/> via the
    /// FakeCoda <see cref="FakeCoda.FakeCodaScenario.Crash"/> scenario. The returned task completes
    /// only after the manager's own <c>Error</c> handler has fired — which, by construction, runs
    /// after the metadata <c>State</c> has been updated, so callers can assert on the fallback path
    /// without racing the crash.
    /// </summary>
    private async Task<(CodaSessionManager Manager, FakeCoda.FakeCodaServer Server, CodaSession Session)> NewCrashedSessionAsync(string sessionId)
    {
        var (server, clientStream) = FakeCoda.FakeCodaServer.Create();
        var connection = new CodaJsonRpcConnection(clientStream, clientStream);
        var session = new CodaSession(
            sessionId, "c1", Path.Combine(this.tempRoot, "wf"), CodingPolicy.Prompt, connection, NullLogger<CodaSession>.Instance);

        var manager = this.NewManager();

        // The manager re-raises Error AFTER updating the metadata State, so awaiting the
        // manager-level event guarantees the fallback metadata is already truthful.
        var managerErrorSignal = new TaskCompletionSource<CodaErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Error += evt => managerErrorSignal.TrySetResult(evt);

        manager.RegisterStartedSessionForTesting(session, channelId: "c1", workingFolder: Path.Combine(this.tempRoot, "wf"), policy: CodingPolicy.Prompt, tenantId: "tenant-1");

        server.Scenario = FakeCoda.FakeCodaScenario.Crash;
        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await session.WriteUserMessageAsync("go", CancellationToken.None);
        await managerErrorSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CodingSessionState.Crashed, session.State);

        return (manager, server, session);
    }

    [Fact]
    public async Task ListSessions_CrashedLiveSession_ReportsCrashed_NotIdle()
    {
        var (manager, server, session) = await this.NewCrashedSessionAsync("crash-live-1");
        await using var _ = server;
        await using var __ = session;

        var status = Assert.Single(manager.ListSessions());

        Assert.Equal("crash-live-1", status.SessionId);
        Assert.Equal(CodingSessionState.Crashed, status.State);
        Assert.NotEqual(CodingSessionState.Idle, status.State);
    }

    [Fact]
    public async Task GetStatus_CrashedLiveSession_ReportsCrashed_NotIdle()
    {
        var (manager, server, session) = await this.NewCrashedSessionAsync("crash-live-2");
        await using var _ = server;
        await using var __ = session;

        var status = manager.GetStatus("crash-live-2");

        Assert.NotNull(status);
        Assert.Equal(CodingSessionState.Crashed, status.State);
    }

    [Fact]
    public async Task GetStatus_CrashedSessionRemovedFromLiveMap_FallbackReportsCrashed_NotIdle()
    {
        var (manager, server, session) = await this.NewCrashedSessionAsync("crash-gone-1");
        await using var _ = server;
        await using var __ = session;

        // Simulate a cleanup/teardown that drops the session from the live map WITHOUT going
        // through EndAsync — the metadata fallback must still report the true (Crashed) state.
        manager.RemoveLiveSessionForTesting("crash-gone-1");

        var status = manager.GetStatus("crash-gone-1");

        Assert.NotNull(status);
        Assert.Equal(CodingSessionState.Crashed, status.State);
        Assert.NotEqual(CodingSessionState.Idle, status.State);
    }

    [Fact]
    public async Task ListSessions_CrashedSessionRemovedFromLiveMap_FallbackReportsCrashed_NotIdle()
    {
        var (manager, server, session) = await this.NewCrashedSessionAsync("crash-gone-2");
        await using var _ = server;
        await using var __ = session;

        manager.RemoveLiveSessionForTesting("crash-gone-2");

        var status = Assert.Single(manager.ListSessions());

        Assert.Equal("crash-gone-2", status.SessionId);
        Assert.Equal(CodingSessionState.Crashed, status.State);
        Assert.NotEqual(CodingSessionState.Idle, status.State);
    }

    [Fact]
    public async Task GetStatus_AfterStreamPulse_ReportsStreamingContext()
    {
        var (server, clientStream) = FakeCoda.FakeCodaServer.Create();
        await using var _ = server;
        server.Scenario = FakeCoda.FakeCodaScenario.SlowStream;
        var connection = new CodaJsonRpcConnection(clientStream, clientStream);
        var session = new CodaSession(
            "stream-1", "c1", Path.Combine(this.tempRoot, "wf"), CodingPolicy.Yolo, connection,
            NullLogger<CodaSession>.Instance, new CodaOptions { PromptIdleTimeoutSeconds = 30 });
        await using var __ = session;

        var manager = this.NewManager();
        manager.RegisterStartedSessionForTesting(session, channelId: "c1", workingFolder: Path.Combine(this.tempRoot, "wf"), policy: CodingPolicy.Yolo, tenantId: "tenant-1");
        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FinalResult += _ => done.TrySetResult();
        await session.WriteUserMessageAsync("go", CancellationToken.None);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var status = manager.GetStatus("stream-1");
        Assert.NotNull(status);
        Assert.NotNull(status!.LastStreamActivityAt);
        Assert.True((status.StreamedChars ?? 0) > 0);
    }

    [Fact]
    public async Task ManagerForwardsStalledEvent()
    {
        var (server, clientStream) = FakeCoda.FakeCodaServer.Create();
        await using var _ = server;
        server.Scenario = FakeCoda.FakeCodaScenario.Stall;
        var connection = new CodaJsonRpcConnection(clientStream, clientStream);
        var session = new CodaSession(
            "stall-1", "c1", Path.Combine(this.tempRoot, "wf"), CodingPolicy.Yolo, connection,
            NullLogger<CodaSession>.Instance, new CodaOptions { PromptIdleTimeoutSeconds = 1 });
        await using var __ = session;

        var manager = this.NewManager();
        manager.RegisterStartedSessionForTesting(session, channelId: "c1", workingFolder: Path.Combine(this.tempRoot, "wf"), policy: CodingPolicy.Yolo, tenantId: "tenant-1");
        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var stalled = new TaskCompletionSource<CodaStalledEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Stalled += e => stalled.TrySetResult(e);

        await session.WriteUserMessageAsync("work", CancellationToken.None);

        var evt = await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("stall-1", evt.SessionId);
        Assert.Equal(CodingSessionState.Crashed, manager.GetStatus("stall-1")!.State);
    }

    [Fact]
    public async Task SetGoalAsync_LiveSession_ReturnsEchoedGoal()
    {
        var (server, clientStream) = FakeCoda.FakeCodaServer.Create();
        await using var _ = server;
        var connection = new CodaJsonRpcConnection(clientStream, clientStream);
        var session = new CodaSession(
            "goal-1", "c1", Path.Combine(this.tempRoot, "wf"), CodingPolicy.Yolo, connection,
            NullLogger<CodaSession>.Instance, new CodaOptions());
        await using var __ = session;

        var manager = this.NewManager();
        manager.RegisterStartedSessionForTesting(session, channelId: "c1", workingFolder: Path.Combine(this.tempRoot, "wf"), policy: CodingPolicy.Yolo, tenantId: "tenant-1");
        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var response = await manager.SetGoalAsync(
            new CodingSetGoalRequest { SessionId = "goal-1", Goal = "ship it", MaxDuration = "1h", MaxContinuations = 100 },
            CancellationToken.None);

        Assert.Equal("goal-1", response.SessionId);
        Assert.Equal("ship it", response.Goal);
        Assert.Equal("1h", response.MaxDuration);
        Assert.Equal(100, response.MaxContinuations);
    }

    [Fact]
    public async Task SetGoalAsync_UnknownSession_Throws()
    {
        var manager = this.NewManager();
        await Assert.ThrowsAsync<CodingAgentException>(() =>
            manager.SetGoalAsync(new CodingSetGoalRequest { SessionId = "nope", Goal = "x" }, CancellationToken.None));
    }

    [Fact]
    public async Task EndAsync_ReturnsEnded_RemovesLiveSession_AndSubsequentStatusIsEnded()
    {
        var (server, clientStream) = FakeCoda.FakeCodaServer.Create();
        await using var _ = server;
        var connection = new CodaJsonRpcConnection(clientStream, clientStream);
        var session = new CodaSession(
            "end-1", "c1", Path.Combine(this.tempRoot, "wf"), CodingPolicy.Prompt, connection, NullLogger<CodaSession>.Instance);

        var manager = this.NewManager();
        manager.RegisterStartedSessionForTesting(session, channelId: "c1", workingFolder: Path.Combine(this.tempRoot, "wf"), policy: CodingPolicy.Prompt, tenantId: "tenant-1");
        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Sanity: a started session is live and Idle before we stop it.
        Assert.Equal(CodingSessionState.Idle, manager.GetStatus("end-1")!.State);

        var response = await manager.EndAsync("end-1", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("end-1", response.SessionId);
        Assert.Equal(CodingSessionState.Ended, response.State);

        // The live session is gone; a subsequent status comes from the metadata fallback and
        // must read Ended — never a live/Idle phantom.
        var status = manager.GetStatus("end-1");
        Assert.NotNull(status);
        Assert.Equal(CodingSessionState.Ended, status.State);

        var listed = Assert.Single(manager.ListSessions());
        Assert.Equal(CodingSessionState.Ended, listed.State);
    }
}
