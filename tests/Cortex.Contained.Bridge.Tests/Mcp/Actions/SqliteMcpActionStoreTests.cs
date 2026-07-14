using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Bridge.Mcp.Actions;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Mcp.Actions;

[Collection(McpActionStoreCollectionDefinition.Name)]
public sealed class SqliteMcpActionStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset StartTime = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly string _databaseKey;
    private readonly FakeTimeProvider _timeProvider;
    private SqliteMcpActionStore _store = null!;

    public SqliteMcpActionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-action-store-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _databasePath = Path.Combine(_tempDir, "actions.db");
        _databaseKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _timeProvider = new FakeTimeProvider(StartTime);
    }

    public Task InitializeAsync()
    {
        _store = CreateStore();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
    }

    private SqliteMcpActionStore CreateStore()
        => new(_databasePath, _databaseKey, _timeProvider, NullLogger<SqliteMcpActionStore>.Instance);

    private DateTimeOffset Now => _timeProvider.GetUtcNow();

    private static string HashOf(string canonicalJson)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    private McpActionProposal NewProposal(
        string? invocationId = null,
        string tenantId = "tenant-1",
        string serverKey = "github",
        string toolName = "create_issue",
        string canonicalArgumentsJson = """{"body":"b","title":"t"}""")
        => new()
        {
            TenantId = tenantId,
            InvocationId = invocationId ?? Guid.CreateVersion7().ToString("N"),
            CorrelationId = "corr-1",
            ConversationId = "conv-1",
            ChannelId = "webchat-default",
            WorkerId = "worker-1",
            ServerKey = serverKey,
            ToolName = toolName,
            CanonicalArgumentsJson = canonicalArgumentsJson,
            ArgumentsHash = HashOf(canonicalArgumentsJson),
            CreatedAtUtc = Now,
            ProposalExpiresAtUtc = Now.AddMinutes(10),
        };

    private async Task<McpAction> ProposeAndApproveAsync(McpActionProposal? proposal = null)
    {
        proposal ??= NewProposal();
        var action = await _store.ProposeAsync(proposal, CancellationToken.None);
        var result = await _store.ApproveAsync(
            action.TenantId, action.ActionId, action.ArgumentsHash, "user@local", "ok",
            Now.AddHours(1), CancellationToken.None);
        Assert.True(result.Succeeded);
        return result.Action!;
    }

    private async Task<(McpAction Action, McpActionDispatchLease Lease)> ProposeApproveClaimAsync()
    {
        var action = await ProposeAndApproveAsync();
        var lease = await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None);
        Assert.NotNull(lease);
        return (action, lease);
    }

    // ── Propose ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ProposeAsync_PersistsAndSurvivesReopen()
    {
        var proposal = NewProposal();
        var created = await _store.ProposeAsync(proposal, CancellationToken.None);

        Assert.Equal(McpActionState.Proposed, created.State);
        Assert.Equal(proposal.TenantId, created.TenantId);
        Assert.Equal(proposal.InvocationId, created.InvocationId);
        Assert.Equal(proposal.ArgumentsHash, created.ArgumentsHash);

        await _store.DisposeAsync();
        _store = CreateStore();

        var reloaded = await _store.GetAsync(proposal.TenantId, created.ActionId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(created.ActionId, reloaded.ActionId);
        Assert.Equal(proposal.InvocationId, reloaded.InvocationId);
        Assert.Equal(proposal.CorrelationId, reloaded.CorrelationId);
        Assert.Equal(proposal.ConversationId, reloaded.ConversationId);
        Assert.Equal(proposal.ChannelId, reloaded.ChannelId);
        Assert.Equal(proposal.WorkerId, reloaded.WorkerId);
        Assert.Equal(proposal.ServerKey, reloaded.ServerKey);
        Assert.Equal(proposal.ToolName, reloaded.ToolName);
        Assert.Equal(proposal.CanonicalArgumentsJson, reloaded.CanonicalArgumentsJson);
        Assert.Equal(proposal.ArgumentsHash, reloaded.ArgumentsHash);
        Assert.Equal(McpActionState.Proposed, reloaded.State);
        Assert.Equal(proposal.ProposalExpiresAtUtc, reloaded.ProposalExpiresAtUtc);
        Assert.Equal(proposal.CreatedAtUtc, reloaded.CreatedAtUtc);
    }

    [Fact]
    public async Task ProposeAsync_DuplicateInvocation_ReturnsExistingAction()
    {
        var proposal = NewProposal();
        var first = await _store.ProposeAsync(proposal, CancellationToken.None);
        var second = await _store.ProposeAsync(proposal, CancellationToken.None);

        Assert.Equal(first.ActionId, second.ActionId);

        var all = await _store.ListAsync(new McpActionQuery { TenantId = proposal.TenantId }, CancellationToken.None);
        Assert.Single(all);
    }

    [Fact]
    public async Task ProposeAsync_ConcurrentFingerprint_Deduplicates()
    {
        // Two different invocations propose the same (tenant, server, tool, args-hash)
        // while the first is still active: the store must not create a second active action.
        var first = await _store.ProposeAsync(NewProposal(invocationId: "inv-a"), CancellationToken.None);
        var second = await _store.ProposeAsync(NewProposal(invocationId: "inv-b"), CancellationToken.None);

        Assert.Equal(first.ActionId, second.ActionId);

        var all = await _store.ListAsync(new McpActionQuery { TenantId = first.TenantId }, CancellationToken.None);
        Assert.Single(all);
    }

    [Fact]
    public async Task ProposeAsync_DuplicateInvocationWithDifferentHash_ThrowsWithoutAliasing()
    {
        // Re-proposing the SAME invocation with DIFFERENT arguments must not silently return
        // the old action — the caller would believe its new arguments were recorded.
        var original = await _store.ProposeAsync(
            NewProposal(invocationId: "inv-x"), CancellationToken.None);

        var conflicting = NewProposal(
            invocationId: "inv-x",
            canonicalArgumentsJson: """{"body":"DIFFERENT","title":"t"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.ProposeAsync(conflicting, CancellationToken.None));

        // The original action is untouched and no second action was created.
        var reloaded = await _store.GetAsync(original.TenantId, original.ActionId, CancellationToken.None);
        Assert.Equal(original.ArgumentsHash, reloaded!.ArgumentsHash);
        Assert.Equal(original.CanonicalArgumentsJson, reloaded.CanonicalArgumentsJson);
        var all = await _store.ListAsync(new McpActionQuery { TenantId = original.TenantId }, CancellationToken.None);
        Assert.Single(all);
    }

    [Fact]
    public async Task ProposeAsync_SameFingerprintAfterTerminal_CreatesNewAction()
    {
        // The active-fingerprint unique index only covers ACTIVE actions: after the first
        // action reaches a terminal state, the same mutation may be proposed again.
        var first = await _store.ProposeAsync(NewProposal(invocationId: "inv-a"), CancellationToken.None);
        var cancel = await _store.CancelAsync(
            first.TenantId, first.ActionId, first.ArgumentsHash, "user@local", CancellationToken.None);
        Assert.True(cancel.Accepted);

        var second = await _store.ProposeAsync(NewProposal(invocationId: "inv-b"), CancellationToken.None);

        Assert.NotEqual(first.ActionId, second.ActionId);
        Assert.Equal(McpActionState.Proposed, second.State);
    }

    // ── Approve / Reject ─────────────────────────────────────────────────

    [Fact]
    public async Task ApproveAsync_ExactHash_TransitionsToApproved()
    {
        var action = await _store.ProposeAsync(NewProposal(), CancellationToken.None);
        var expiresAt = Now.AddHours(1);

        var result = await _store.ApproveAsync(
            action.TenantId, action.ActionId, action.ArgumentsHash, "user@local", "looks right",
            expiresAt, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.NotNull(result.Action);
        Assert.Equal(McpActionState.Approved, result.Action.State);
        Assert.Equal(expiresAt, result.Action.ApprovalExpiresAtUtc);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Approved, reloaded!.State);
    }

    [Fact]
    public async Task ApproveAsync_StaleHash_DoesNotChangeState()
    {
        var action = await _store.ProposeAsync(NewProposal(), CancellationToken.None);

        var result = await _store.ApproveAsync(
            action.TenantId, action.ActionId, "stale-hash", "user@local", null,
            Now.AddHours(1), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("arguments_hash_mismatch", result.Error);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Proposed, reloaded!.State);
        Assert.Null(reloaded.ApprovalExpiresAtUtc);
        Assert.Equal(action.Version, reloaded.Version);
    }

    [Fact]
    public async Task ApproveAsync_ExpiredProposal_Fails()
    {
        var action = await _store.ProposeAsync(NewProposal(), CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromMinutes(11)); // past the 10-minute proposal window

        var result = await _store.ApproveAsync(
            action.TenantId, action.ActionId, action.ArgumentsHash, "user@local", null,
            Now.AddHours(1), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("proposal_expired", result.Error);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.NotEqual(McpActionState.Approved, reloaded!.State);
    }

    [Fact]
    public async Task ApproveAsync_AlreadyApproved_Fails()
    {
        var action = await ProposeAndApproveAsync();

        var result = await _store.ApproveAsync(
            action.TenantId, action.ActionId, action.ArgumentsHash, "user@local", null,
            Now.AddHours(1), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_state", result.Error);
    }

    [Fact]
    public async Task RejectAsync_Proposed_TransitionsToRejected()
    {
        var action = await _store.ProposeAsync(NewProposal(), CancellationToken.None);

        var result = await _store.RejectAsync(
            action.TenantId, action.ActionId, action.ArgumentsHash, "user@local", "nope", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(McpActionState.Rejected, result.Action!.State);
        Assert.NotNull(result.Action.CompletedAtUtc);
    }

    [Fact]
    public async Task RejectAsync_StaleHash_DoesNotChangeState()
    {
        var action = await _store.ProposeAsync(NewProposal(), CancellationToken.None);

        var result = await _store.RejectAsync(
            action.TenantId, action.ActionId, "stale-hash", "user@local", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("arguments_hash_mismatch", result.Error);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Proposed, reloaded!.State);
    }

    [Fact]
    public async Task RejectAsync_FromApproved_Fails()
    {
        var action = await ProposeAndApproveAsync();

        var result = await _store.RejectAsync(
            action.TenantId, action.ActionId, action.ArgumentsHash, "user@local", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_state", result.Error);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Approved, reloaded!.State);
    }

    // ── Claim / dispatch ─────────────────────────────────────────────────

    [Fact]
    public async Task TryClaimNextApprovedAsync_OnlyOneCallerGetsLease()
    {
        var action = await ProposeAndApproveAsync();

        var claims = await Task.WhenAll(
            _store.TryClaimNextApprovedAsync(Now, CancellationToken.None),
            _store.TryClaimNextApprovedAsync(Now, CancellationToken.None));

        var leases = claims.Where(c => c is not null).ToList();
        Assert.Single(leases);
        Assert.Equal(action.ActionId, leases[0]!.ActionId);
        Assert.Equal(1, leases[0]!.AttemptNumber);
        Assert.Equal(action.InvocationId, leases[0]!.InvocationId);
        Assert.Equal(action.CanonicalArgumentsJson, leases[0]!.CanonicalArgumentsJson);
        Assert.Equal(action.ArgumentsHash, leases[0]!.ArgumentsHash);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Dispatching, reloaded!.State);
    }

    [Fact]
    public async Task TryClaimNextApprovedAsync_NothingApproved_ReturnsNull()
    {
        await _store.ProposeAsync(NewProposal(), CancellationToken.None); // proposed, not approved

        var lease = await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None);

        Assert.Null(lease);
    }

    [Fact]
    public async Task CompleteAttemptAsync_Succeeded_TerminalNeverClaimedAgain()
    {
        var (action, lease) = await ProposeApproveClaimAsync();

        await _store.CompleteAttemptAsync(new McpActionDispatchCompletion
        {
            ActionId = lease.ActionId,
            AttemptNumber = lease.AttemptNumber,
            State = McpActionState.Succeeded,
            FailureKind = McpFailureKind.None,
            ResultContent = """{"issue":42}""",
            RemoteReference = "issue/42",
            CompletedAtUtc = Now,
        }, CancellationToken.None);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Succeeded, reloaded!.State);
        Assert.Equal("""{"issue":42}""", reloaded.ResultContent);
        Assert.Equal("issue/42", reloaded.RemoteReference);
        Assert.NotNull(reloaded.CompletedAtUtc);

        // Terminal states never dispatch again.
        var lease2 = await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None);
        Assert.Null(lease2);
    }

    [Fact]
    public async Task CompleteAttemptAsync_NotStarted_ReturnsToApprovedAndReclaimable()
    {
        var (action, lease) = await ProposeApproveClaimAsync();
        var retryAt = Now.AddMinutes(5);

        await _store.CompleteAttemptAsync(new McpActionDispatchCompletion
        {
            ActionId = lease.ActionId,
            AttemptNumber = lease.AttemptNumber,
            State = McpActionState.Approved, // positively known not to have started
            FailureKind = McpFailureKind.Unavailable,
            Error = "server unreachable before dispatch",
            CompletedAtUtc = Now,
            RetryAtUtc = retryAt,
        }, CancellationToken.None);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Approved, reloaded!.State);
        Assert.Equal(retryAt, reloaded.NextAttemptAtUtc);

        // Not claimable before the retry time.
        Assert.Null(await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None));

        // Claimable at the retry time — as a NEW attempt.
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        var lease2 = await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None);
        Assert.NotNull(lease2);
        Assert.Equal(action.ActionId, lease2.ActionId);
        Assert.Equal(2, lease2.AttemptNumber);
    }

    [Fact]
    public async Task CompleteAttemptAsync_ClosedAttempt_RejectedWithoutStateChange()
    {
        // A late/duplicate completion for an ALREADY-CLOSED attempt must be rejected: it must
        // not re-complete the action with stale evidence while a newer attempt is in flight.
        var (action, lease1) = await ProposeApproveClaimAsync();

        await _store.CompleteAttemptAsync(new McpActionDispatchCompletion
        {
            ActionId = lease1.ActionId,
            AttemptNumber = lease1.AttemptNumber,
            State = McpActionState.Approved, // attempt 1 positively did not start
            FailureKind = McpFailureKind.Unavailable,
            Error = "server unreachable before dispatch",
            CompletedAtUtc = Now,
            RetryAtUtc = Now,
        }, CancellationToken.None);

        var lease2 = await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None);
        Assert.NotNull(lease2);
        Assert.Equal(2, lease2.AttemptNumber); // attempt 2 is genuinely in flight

        // Late duplicate completion of the CLOSED attempt 1 arrives with stale evidence.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CompleteAttemptAsync(
            new McpActionDispatchCompletion
            {
                ActionId = lease1.ActionId,
                AttemptNumber = lease1.AttemptNumber,
                State = McpActionState.Succeeded,
                FailureKind = McpFailureKind.None,
                ResultContent = """{"stale":true}""",
                CompletedAtUtc = Now,
            }, CancellationToken.None));

        // The action is still dispatching on attempt 2, untouched by the stale completion.
        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Dispatching, reloaded!.State);
        Assert.Null(reloaded.ResultContent);

        // Attempt 2 still completes normally.
        await _store.CompleteAttemptAsync(new McpActionDispatchCompletion
        {
            ActionId = lease2.ActionId,
            AttemptNumber = lease2.AttemptNumber,
            State = McpActionState.Succeeded,
            FailureKind = McpFailureKind.None,
            ResultContent = """{"issue":42}""",
            CompletedAtUtc = Now,
        }, CancellationToken.None);
        reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Succeeded, reloaded!.State);
        Assert.Equal("""{"issue":42}""", reloaded.ResultContent);
    }

    [Fact]
    public async Task CompleteAttemptAsync_NonDispatching_Throws()
    {
        var action = await ProposeAndApproveAsync(); // approved, never claimed

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CompleteAttemptAsync(
            new McpActionDispatchCompletion
            {
                ActionId = action.ActionId,
                AttemptNumber = 1,
                State = McpActionState.Succeeded,
                FailureKind = McpFailureKind.None,
                CompletedAtUtc = Now,
            }, CancellationToken.None));

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Approved, reloaded!.State);
    }

    [Fact]
    public async Task CompleteAttemptAsync_DisallowedTargetState_Throws()
    {
        var (_, lease) = await ProposeApproveClaimAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => _store.CompleteAttemptAsync(
            new McpActionDispatchCompletion
            {
                ActionId = lease.ActionId,
                AttemptNumber = lease.AttemptNumber,
                State = McpActionState.Rejected, // not a dispatch outcome
                FailureKind = McpFailureKind.None,
                CompletedAtUtc = Now,
            }, CancellationToken.None));
    }

    // ── Crash recovery ───────────────────────────────────────────────────

    [Fact]
    public async Task RecoverInterruptedDispatches_MarksOutcomeUnknown()
    {
        var (action, _) = await ProposeApproveClaimAsync();

        // Simulate a Bridge crash mid-dispatch: the row is left in 'dispatching'.
        await _store.DisposeAsync();
        _store = CreateStore();

        var recovered = await _store.RecoverInterruptedDispatchesAsync(Now, CancellationToken.None);
        Assert.Equal(1, recovered);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.OutcomeUnknown, reloaded!.State);

        // Never re-dispatched from outcome_unknown.
        Assert.Null(await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None));

        // Idempotent: a second pass finds nothing.
        Assert.Equal(0, await _store.RecoverInterruptedDispatchesAsync(Now, CancellationToken.None));
    }

    // ── Cancel ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_Approved_PreventsClaim()
    {
        var action = await ProposeAndApproveAsync();

        var result = await _store.CancelAsync(
            action.TenantId, action.ActionId, action.ArgumentsHash, "user@local", CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(McpActionState.Cancelled, result.Action!.State);

        var lease = await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None);
        Assert.Null(lease);
    }

    [Fact]
    public async Task CancelAsync_StaleHash_DoesNotChangeState()
    {
        var action = await ProposeAndApproveAsync();

        var result = await _store.CancelAsync(
            action.TenantId, action.ActionId, "stale-hash", "user@local", CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("arguments_hash_mismatch", result.Error);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Approved, reloaded!.State);
    }

    [Fact]
    public async Task CancelAsync_WhileDispatching_AttemptDidNotStart_EndsCancelledNotRedispatched()
    {
        // Cancel accepted mid-attempt: the dispatch outcome decides. When the attempt is then
        // positively known NOT to have started, the pending cancel must be honored — the action
        // must end cancelled instead of silently returning to the outbox and executing later.
        var (action, lease) = await ProposeApproveClaimAsync();

        var cancel = await _store.CancelAsync(
            action.TenantId, action.ActionId, action.ArgumentsHash, "user@local", CancellationToken.None);
        Assert.True(cancel.Accepted);

        // Still dispatching — the in-flight attempt owns the outcome.
        var midFlight = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Dispatching, midFlight!.State);

        await _store.CompleteAttemptAsync(new McpActionDispatchCompletion
        {
            ActionId = lease.ActionId,
            AttemptNumber = lease.AttemptNumber,
            State = McpActionState.Approved, // positively known not to have started
            FailureKind = McpFailureKind.Unavailable,
            Error = "server unreachable before dispatch",
            CompletedAtUtc = Now,
            RetryAtUtc = Now,
        }, CancellationToken.None);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Cancelled, reloaded!.State);
        Assert.NotNull(reloaded.CompletedAtUtc);

        // Never re-dispatched: the cancelled mutation must not execute on attempt 2.
        Assert.Null(await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None));

        // The audit trail records both legal edges of the collapse.
        var events = await ReadEventsAsync(action.ActionId);
        Assert.Equal(("dispatching", "approved"), (events[^2].FromStatus, events[^2].ToStatus));
        Assert.Equal(("approved", "cancelled"), (events[^1].FromStatus, events[^1].ToStatus));
    }

    [Fact]
    public async Task CancelAsync_TerminalState_NotAccepted()
    {
        var action = await _store.ProposeAsync(NewProposal(), CancellationToken.None);
        var rejected = await _store.RejectAsync(
            action.TenantId, action.ActionId, action.ArgumentsHash, "user@local", null, CancellationToken.None);
        Assert.True(rejected.Succeeded);

        var result = await _store.CancelAsync(
            action.TenantId, action.ActionId, action.ArgumentsHash, "user@local", CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("invalid_state", result.Error);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Rejected, reloaded!.State);
    }

    // ── Reconcile ────────────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_OnlyAcceptsOutcomeUnknown()
    {
        // Not accepted from proposed.
        var proposed = await _store.ProposeAsync(NewProposal(), CancellationToken.None);
        var early = await _store.ReconcileAsync(
            proposed.TenantId, proposed.ActionId, proposed.ArgumentsHash, succeeded: true,
            "user@local", "checked remote", null, CancellationToken.None);
        Assert.False(early.Succeeded);
        Assert.Equal("invalid_state", early.Error);

        // Drive the same action to outcome_unknown.
        var approved = await _store.ApproveAsync(
            proposed.TenantId, proposed.ActionId, proposed.ArgumentsHash, "user@local", null,
            Now.AddHours(1), CancellationToken.None);
        Assert.True(approved.Succeeded);
        var lease = await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None);
        Assert.NotNull(lease);
        await _store.CompleteAttemptAsync(new McpActionDispatchCompletion
        {
            ActionId = lease.ActionId,
            AttemptNumber = lease.AttemptNumber,
            State = McpActionState.OutcomeUnknown,
            FailureKind = McpFailureKind.Timeout,
            Error = "timeout after dispatch",
            CompletedAtUtc = Now,
        }, CancellationToken.None);

        var result = await _store.ReconcileAsync(
            proposed.TenantId, proposed.ActionId, proposed.ArgumentsHash, succeeded: true,
            "user@local", "found issue/42 on remote", "issue/42", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(McpActionState.ReconciledSucceeded, result.Action!.State);
        Assert.Equal("issue/42", result.Action.RemoteReference);
        Assert.NotNull(result.Action.CompletedAtUtc);

        // Reconciled is terminal: a second reconcile is not accepted.
        var again = await _store.ReconcileAsync(
            proposed.TenantId, proposed.ActionId, proposed.ArgumentsHash, succeeded: false,
            "user@local", "changed my mind", null, CancellationToken.None);
        Assert.False(again.Succeeded);
        Assert.Equal("invalid_state", again.Error);
    }

    [Fact]
    public async Task ReconcileAsync_StaleHash_DoesNotChangeState()
    {
        var (action, lease) = await ProposeApproveClaimAsync();
        await _store.CompleteAttemptAsync(new McpActionDispatchCompletion
        {
            ActionId = lease.ActionId,
            AttemptNumber = lease.AttemptNumber,
            State = McpActionState.OutcomeUnknown,
            FailureKind = McpFailureKind.Transport,
            CompletedAtUtc = Now,
        }, CancellationToken.None);

        var result = await _store.ReconcileAsync(
            action.TenantId, action.ActionId, "stale-hash", succeeded: true,
            "user@local", "evidence", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("arguments_hash_mismatch", result.Error);

        var reloaded = await _store.GetAsync(action.TenantId, action.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.OutcomeUnknown, reloaded!.State);
    }

    // ── Expiry ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExpireAsync_ExpiresDueProposalsAndApprovals()
    {
        var proposed = await _store.ProposeAsync(NewProposal(), CancellationToken.None);
        var approvedProposal = NewProposal(serverKey: "jira", toolName: "create_ticket");
        var approved = await ProposeAndApproveAsync(approvedProposal); // approval expires in 1h

        _timeProvider.Advance(TimeSpan.FromHours(2)); // past both windows

        var expired = await _store.ExpireAsync(Now, CancellationToken.None);
        Assert.Equal(2, expired);

        var reloadedProposed = await _store.GetAsync(proposed.TenantId, proposed.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Expired, reloadedProposed!.State);
        var reloadedApproved = await _store.GetAsync(approved.TenantId, approved.ActionId, CancellationToken.None);
        Assert.Equal(McpActionState.Expired, reloadedApproved!.State);

        // Expired is terminal: nothing claimable, and a second sweep is a no-op.
        Assert.Null(await _store.TryClaimNextApprovedAsync(Now, CancellationToken.None));
        Assert.Equal(0, await _store.ExpireAsync(Now, CancellationToken.None));
    }

    // ── List ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_FiltersByTenantAndState()
    {
        var mine = await _store.ProposeAsync(NewProposal(tenantId: "tenant-1"), CancellationToken.None);
        await _store.ProposeAsync(
            NewProposal(tenantId: "tenant-2", canonicalArgumentsJson: """{"other":true}"""),
            CancellationToken.None);

        var proposedList = await _store.ListAsync(
            new McpActionQuery { TenantId = "tenant-1", State = McpActionState.Proposed },
            CancellationToken.None);
        Assert.Single(proposedList);
        Assert.Equal(mine.ActionId, proposedList[0].ActionId);

        var approvedList = await _store.ListAsync(
            new McpActionQuery { TenantId = "tenant-1", State = McpActionState.Approved },
            CancellationToken.None);
        Assert.Empty(approvedList);
    }

    // ── Event trail atomicity ────────────────────────────────────────────

    [Fact]
    public async Task StateTransitions_AppendEventForEveryTransition()
    {
        var (action, lease) = await ProposeApproveClaimAsync();
        await _store.CompleteAttemptAsync(new McpActionDispatchCompletion
        {
            ActionId = lease.ActionId,
            AttemptNumber = lease.AttemptNumber,
            State = McpActionState.Succeeded,
            FailureKind = McpFailureKind.None,
            CompletedAtUtc = Now,
        }, CancellationToken.None);

        var events = await ReadEventsAsync(action.ActionId);

        Assert.Equal(4, events.Count);
        Assert.Equal((null, "proposed"), (events[0].FromStatus, events[0].ToStatus));
        Assert.Equal(("proposed", "approved"), (events[1].FromStatus, events[1].ToStatus));
        Assert.Equal(("approved", "dispatching"), (events[2].FromStatus, events[2].ToStatus));
        Assert.Equal(("dispatching", "succeeded"), (events[3].FromStatus, events[3].ToStatus));
    }

    // ── State machine ────────────────────────────────────────────────────

    public static TheoryData<McpActionState, McpActionState, bool> AllTransitionPairs()
    {
        // The spec's complete edge list (McpActionModels.cs). Every other (from, to) pair —
        // including every edge OUT of a terminal state — must be disallowed.
        var allowedEdges = new HashSet<(McpActionState From, McpActionState To)>
        {
            (McpActionState.Proposed, McpActionState.Approved),
            (McpActionState.Proposed, McpActionState.Rejected),
            (McpActionState.Proposed, McpActionState.Cancelled),
            (McpActionState.Proposed, McpActionState.Expired),
            (McpActionState.Approved, McpActionState.Dispatching),
            (McpActionState.Approved, McpActionState.Cancelled),
            (McpActionState.Approved, McpActionState.Expired),
            (McpActionState.Dispatching, McpActionState.Succeeded),
            (McpActionState.Dispatching, McpActionState.Failed),
            (McpActionState.Dispatching, McpActionState.OutcomeUnknown),
            (McpActionState.Dispatching, McpActionState.Approved),
            (McpActionState.OutcomeUnknown, McpActionState.ReconciledSucceeded),
            (McpActionState.OutcomeUnknown, McpActionState.ReconciledFailed),
        };

        var data = new TheoryData<McpActionState, McpActionState, bool>();
        foreach (var from in Enum.GetValues<McpActionState>())
        {
            foreach (var to in Enum.GetValues<McpActionState>())
            {
                data.Add(from, to, allowedEdges.Contains((from, to)));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllTransitionPairs))]
    public void IsTransitionAllowed_MatchesSpecExactly(McpActionState from, McpActionState to, bool expected)
        => Assert.Equal(expected, SqliteMcpActionStore.IsTransitionAllowed(from, to));

    // ── Dispose ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightOperation()
    {
        // Simulate an in-flight operation holding the serialization gate: DisposeAsync must
        // wait for it instead of disposing the gate/connection out from under it (which would
        // make the operation's `finally { gate.Release(); }` throw ObjectDisposedException).
        var gateField = typeof(SqliteMcpActionStore).GetField(
            "gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(gateField);
        var gate = (SemaphoreSlim)gateField.GetValue(_store)!;
        await gate.WaitAsync();

        var disposeTask = _store.DisposeAsync().AsTask();
        var winner = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(disposeTask, winner); // still waiting on the in-flight operation

        gate.Release(); // the in-flight operation finishes and releases without throwing
        await disposeTask;

        // After disposal, new operations fail fast.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _store.GetAsync("tenant-1", "missing", CancellationToken.None));
    }

    // ── Encryption ───────────────────────────────────────────────────────

    [Fact]
    public async Task Database_WithoutCorrectKey_CannotReadActions()
    {
        await _store.ProposeAsync(NewProposal(), CancellationToken.None);
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();

        // The raw file must not carry the plaintext SQLite header.
        var header = new byte[16];
        await using (var fs = File.OpenRead(_databasePath))
        {
            await fs.ReadExactlyAsync(header);
        }
        Assert.NotEqual("SQLite format 3\0"u8.ToArray(), header);

        // Opening without the key must fail to read any table.
        var noKey = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using (var conn = new SqliteConnection(noKey.ToString()))
        {
            var exception = await Record.ExceptionAsync(async () =>
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master";
                await cmd.ExecuteScalarAsync();
            });
            Assert.IsType<SqliteException>(exception);
        }

        // Opening with the correct key works.
        _store = CreateStore();
        var list = await _store.ListAsync(new McpActionQuery { TenantId = "tenant-1" }, CancellationToken.None);
        Assert.Single(list);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed record ActionEvent(string? FromStatus, string ToStatus, string EventType, string Actor);

    private async Task<List<ActionEvent>> ReadEventsAsync(string actionId)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Password = _databaseKey,
        };
        await using var conn = new SqliteConnection(csb.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT from_status, to_status, event_type, actor
            FROM mcp_action_events WHERE action_id = @actionId ORDER BY event_id
            """;
        cmd.Parameters.AddWithValue("@actionId", actionId);

        var events = new List<ActionEvent>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new ActionEvent(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return events;
    }
}
