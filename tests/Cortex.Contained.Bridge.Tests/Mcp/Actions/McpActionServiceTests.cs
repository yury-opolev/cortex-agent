using System.Security.Cryptography;
using System.Text.Json;
using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Bridge.Mcp.Actions;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Mcp.Actions;

[Collection(McpActionStoreCollectionDefinition.Name)]
public sealed class McpActionServiceTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";

    private static readonly DateTimeOffset StartTime = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly FakeTimeProvider _timeProvider;
    private readonly IMcpInvocationTarget _target;
    private readonly McpActionDispatchRegistry _registry = new();
    private readonly McpServerConfig _serverConfig;
    private readonly McpConfigStore _configStore;
    private SqliteMcpActionStore _store = null!;
    private McpActionService _service = null!;

    public McpActionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-action-service-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _timeProvider = new FakeTimeProvider(StartTime);
        _target = Substitute.For<IMcpInvocationTarget>();
        _serverConfig = new McpServerConfig
        {
            Key = "github",
            Enabled = true,
            MutationToolAllowList = ["create_issue"],
        };
        var bridgeConfig = new BridgeConfig
        {
            Mcp = new McpSettingsConfig { Enabled = true, Servers = [_serverConfig] },
        };
        _configStore = new McpConfigStore(
            bridgeConfig, Path.Combine(_tempDir, "cortex.yml"), NullLogger<McpConfigStore>.Instance);
    }

    public Task InitializeAsync()
    {
        _store = CreateStore(_tempDir, _timeProvider);
        _service = new McpActionService(
            _store, _target, _configStore, _registry, _timeProvider, NullLogger<McpActionService>.Instance);
        return Task.CompletedTask;
    }

    private static SqliteMcpActionStore CreateStore(string tempDir, FakeTimeProvider timeProvider)
        => new(
            Path.Combine(tempDir, "actions.db"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            timeProvider,
            NullLogger<SqliteMcpActionStore>.Instance);

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
    }

    private static McpToolInvocation Invocation(
        string toolName, string argumentsJson, string? invocationId = null) => new()
        {
            InvocationId = invocationId ?? Guid.CreateVersion7().ToString("N"),
            ServerKey = "github",
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            ConversationId = "conv-1",
            ChannelId = "webchat-default",
            CorrelationId = "corr-1",
        };

    [Fact]
    public async Task InvokeAsync_ReadTool_DispatchesDirectly()
    {
        var invocation = Invocation("list_issues", "{}");
        _target.InvokeAsync(invocation, Arg.Any<CancellationToken>())
            .Returns(McpToolResult.Ok(invocation.InvocationId, "issues"));

        var result = await _service.InvokeAsync(Tenant, invocation, CancellationToken.None);

        Assert.Equal(McpToolOutcome.Succeeded, result.Outcome);
        Assert.Equal(McpToolDisposition.Completed, result.Disposition);
        Assert.Equal("issues", result.Content);
        Assert.Null(result.ActionId);
        await _target.Received(1).InvokeAsync(invocation, Arg.Any<CancellationToken>());
        await _target.DidNotReceive().InvokeApprovedAsync(Arg.Any<McpToolInvocation>(), Arg.Any<CancellationToken>());

        // A read tool never creates an action record.
        var actions = await _store.ListAsync(new McpActionQuery { TenantId = Tenant }, CancellationToken.None);
        Assert.Empty(actions);
    }

    [Fact]
    public async Task InvokeAsync_MutationTool_CreatesProposalWithoutRemoteCall()
    {
        var invocation = Invocation("create_issue", """{"title":"t","body":"b"}""");

        var result = await _service.InvokeAsync(Tenant, invocation, CancellationToken.None);

        // CRITICAL INVARIANT: the mutation NEVER reaches the remote server — no call of any
        // kind was made on the invocation target.
        await _target.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default);
        await _target.DidNotReceiveWithAnyArgs().InvokeApprovedAsync(default!, default);

        // The result is SUCCESSFUL tool content (not a retryable error) awaiting approval.
        Assert.Equal(McpToolOutcome.Succeeded, result.Outcome);
        Assert.False(result.IsError);
        Assert.Equal(McpToolDisposition.AwaitingApproval, result.Disposition);
        Assert.NotNull(result.ActionId);
        Assert.StartsWith("sha256:", result.ArgumentsHash, StringComparison.Ordinal);

        using var content = JsonDocument.Parse(result.Content);
        Assert.Equal(result.ActionId, content.RootElement.GetProperty("actionId").GetString());
        Assert.Equal("proposed", content.RootElement.GetProperty("status").GetString());
        Assert.Equal(result.ArgumentsHash, content.RootElement.GetProperty("argumentsHash").GetString());
        Assert.Equal(
            "Awaiting exact-argument approval. Do not repeat this mutation.",
            content.RootElement.GetProperty("message").GetString());

        // The persisted action holds the CANONICAL arguments (sorted keys, compact).
        var action = await _store.GetAsync(Tenant, result.ActionId!, CancellationToken.None);
        Assert.NotNull(action);
        Assert.Equal(McpActionState.Proposed, action.State);
        Assert.Equal("""{"body":"b","title":"t"}""", action.CanonicalArgumentsJson);
        Assert.Equal(invocation.InvocationId, action.InvocationId);
    }

    [Fact]
    public async Task InvokeAsync_DuplicateMutation_ReturnsExistingAction()
    {
        // Two invocations (distinct invocation ids) with SEMANTICALLY identical arguments —
        // different key order and whitespace — deduplicate to one pending action.
        var first = await _service.InvokeAsync(
            Tenant, Invocation("create_issue", """{"title":"t","body":"b"}"""), CancellationToken.None);
        var second = await _service.InvokeAsync(
            Tenant, Invocation("create_issue", """{ "body": "b", "title": "t" }"""), CancellationToken.None);

        Assert.Equal(McpToolDisposition.AwaitingApproval, second.Disposition);
        Assert.Equal(first.ActionId, second.ActionId);
        Assert.Equal(first.ArgumentsHash, second.ArgumentsHash);
        await _target.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default);

        var actions = await _store.ListAsync(new McpActionQuery { TenantId = Tenant }, CancellationToken.None);
        Assert.Single(actions);
    }

    [Fact]
    public async Task ApproveAsync_StoredArgumentsCannotBeChanged()
    {
        var result = await _service.InvokeAsync(
            Tenant, Invocation("create_issue", """{"title":"t","body":"b"}"""), CancellationToken.None);
        var actionId = result.ActionId!;

        // Approving with a different hash must not mutate anything.
        var stale = await _store.ApproveAsync(
            Tenant, actionId, "sha256:" + new string('0', 64), "user@local", "ok",
            StartTime.AddHours(1), CancellationToken.None);
        Assert.False(stale.Succeeded);
        Assert.Equal("arguments_hash_mismatch", stale.Error);

        var unchanged = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.NotNull(unchanged);
        Assert.Equal(McpActionState.Proposed, unchanged.State);
        Assert.Equal("""{"body":"b","title":"t"}""", unchanged.CanonicalArgumentsJson);
        Assert.Equal(result.ArgumentsHash, unchanged.ArgumentsHash);

        // Approving with the EXACT hash binds the approval to the stored arguments — which
        // remain byte-for-byte identical after approval.
        var approved = await _store.ApproveAsync(
            Tenant, actionId, result.ArgumentsHash!, "user@local", "ok",
            StartTime.AddHours(1), CancellationToken.None);
        Assert.True(approved.Succeeded);
        Assert.Equal("""{"body":"b","title":"t"}""", approved.Action!.CanonicalArgumentsJson);
        Assert.Equal(result.ArgumentsHash, approved.Action.ArgumentsHash);
    }

    [Fact]
    public async Task InvokeAsync_MalformedMutationArguments_FailsValidation_WithoutPersistingOrDispatching()
    {
        var result = await _service.InvokeAsync(
            Tenant, Invocation("create_issue", "not json"), CancellationToken.None);

        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Validation, result.FailureKind);
        await _target.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default);
        var actions = await _store.ListAsync(new McpActionQuery { TenantId = Tenant }, CancellationToken.None);
        Assert.Empty(actions);
    }

    [Fact]
    public async Task InvokeAsync_CanonicalizationFailure_HostLogDoesNotEchoArgumentFragment()
    {
        // M1 REDACTION: a duplicate-key rejection's exception message echoes the offending
        // PROPERTY NAME, which can be a secret-bearing argument key. The host log must carry only
        // the exception TYPE — never that fragment. (The agent-facing return content still explains
        // the reason, by design, so it is deliberately NOT asserted here.)
        var capturingLogger = new CapturingLogger<McpActionService>();
        var service = new McpActionService(
            _store, _target, _configStore, _registry, _timeProvider, capturingLogger);

        const string secretFragment = "x_secret_api_key";
        var invocation = Invocation(
            "create_issue",
            $$"""{"{{secretFragment}}":"v1","{{secretFragment}}":"v2"}""");

        var result = await service.InvokeAsync(Tenant, invocation, CancellationToken.None);

        // Definitive validation failure — nothing persisted or dispatched.
        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Validation, result.FailureKind);
        await _target.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default);

        // The canonicalization-failure log names only the exception TYPE, and NO log line echoes
        // the argument fragment.
        var failureLogs = capturingLogger.Messages
            .Where(m => m.Contains("canonicalization failed", StringComparison.Ordinal))
            .ToList();
        var failureLog = Assert.Single(failureLogs);
        Assert.Contains(nameof(ArgumentException), failureLog, StringComparison.Ordinal);
        Assert.DoesNotContain(secretFragment, failureLog, StringComparison.Ordinal);
        Assert.DoesNotContain(capturingLogger.Messages, m => m.Contains(secretFragment, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetStatusAsync_UnknownAction_ReturnsNotFound()
    {
        var response = await _service.GetStatusAsync(Tenant, "missing", CancellationToken.None);

        Assert.False(response.Found);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public async Task GetStatusAsync_ExistingAction_ReturnsStatusAndHash()
    {
        var result = await _service.InvokeAsync(
            Tenant, Invocation("create_issue", """{"title":"t","body":"b"}"""), CancellationToken.None);

        var response = await _service.GetStatusAsync(Tenant, result.ActionId!, CancellationToken.None);

        Assert.True(response.Found);
        Assert.Equal(result.ActionId, response.ActionId);
        Assert.Equal("proposed", response.Status);
        Assert.Equal(result.ArgumentsHash, response.ArgumentsHash);
        Assert.Equal("github", response.ServerKey);
        Assert.Equal("create_issue", response.ToolName);
    }

    [Fact]
    public async Task CancelAsync_ProposedAction_CancelsImmediately()
    {
        var result = await _service.InvokeAsync(
            Tenant, Invocation("create_issue", """{"title":"t","body":"b"}"""), CancellationToken.None);

        var response = await _service.CancelAsync(
            Tenant, result.ActionId!, result.ArgumentsHash!, "tester", CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal("cancelled", response.Status);
        var action = await _store.GetAsync(Tenant, result.ActionId!, CancellationToken.None);
        Assert.Equal(McpActionState.Cancelled, action!.State);
    }

    [Fact]
    public async Task CancelAsync_StaleHash_DoesNotMutate()
    {
        var result = await _service.InvokeAsync(
            Tenant, Invocation("create_issue", """{"title":"t","body":"b"}"""), CancellationToken.None);

        var response = await _service.CancelAsync(
            Tenant, result.ActionId!, "sha256:" + new string('0', 64), "tester", CancellationToken.None);

        Assert.False(response.Accepted);
        Assert.Equal("arguments_hash_mismatch", response.Error);
        var action = await _store.GetAsync(Tenant, result.ActionId!, CancellationToken.None);
        Assert.Equal(McpActionState.Proposed, action!.State);
    }

    [Fact]
    public async Task CancelAsync_DispatchingAction_SignalsActiveInvocation_AndIsNotReportedCancelled()
    {
        var result = await _service.InvokeAsync(
            Tenant, Invocation("create_issue", """{"title":"t","body":"b"}"""), CancellationToken.None);
        await _store.ApproveAsync(
            Tenant, result.ActionId!, result.ArgumentsHash!, "user@local", "ok",
            StartTime.AddHours(1), CancellationToken.None);
        var lease = await _store.TryClaimNextApprovedAsync(_timeProvider.GetUtcNow(), CancellationToken.None);
        Assert.NotNull(lease);
        using var dispatchCts = new CancellationTokenSource();
        _registry.Register(lease.ActionId, dispatchCts);

        var response = await _service.CancelAsync(
            Tenant, result.ActionId!, result.ArgumentsHash!, "tester", CancellationToken.None);

        // The cancel is accepted (recorded) and the ACTIVE invocation is signalled, but the
        // action is NOT cancelled — the dispatch outcome decides.
        Assert.True(response.Accepted);
        Assert.Equal("dispatching", response.Status);
        Assert.True(dispatchCts.IsCancellationRequested);
        var action = await _store.GetAsync(Tenant, result.ActionId!, CancellationToken.None);
        Assert.Equal(McpActionState.Dispatching, action!.State);
    }

    /// <summary>Captures fully-formatted log messages so redaction assertions can inspect them.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => this.Messages.Add(formatter(state, exception));
    }
}
