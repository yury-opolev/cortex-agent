using System.Globalization;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp.Actions;

/// <summary>
/// SQLCipher-encrypted SQLite implementation of <see cref="IMcpActionStore"/>. The database
/// lives at <c>%LOCALAPPDATA%/Cortex/mcp/actions.db</c> and is keyed with
/// <c>SecretManager.GetOrCreateDatabaseKey()</c> using the same connection-string convention
/// as the other encrypted stores (<c>Password</c> on the connection string). Every state
/// transition and its audit-event append happen in one SQLite transaction, so a crash can
/// never leave a state change without its event (or vice versa). A single connection with a
/// serializing semaphore makes claim/complete operations race-free within the process; WAL +
/// <c>synchronous=FULL</c> makes committed transitions crash-durable.
/// </summary>
public sealed partial class SqliteMcpActionStore : IMcpActionStore
{
    private const string ActiveStatusList = "('proposed','approved','dispatching','outcome_unknown')";

    private const string SelectActionSql = """
        SELECT action_id, tenant_id, invocation_id, correlation_id, conversation_id, channel_id,
               worker_id, server_key, tool_name, canonical_arguments_json, arguments_sha256, status,
               proposal_expires_at_utc, approval_expires_at_utc, next_attempt_at_utc, result_content,
               error, remote_reference, created_at_utc, updated_at_utc, completed_at_utc, version
        FROM mcp_actions
        """;

    private readonly SqliteConnection connection;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SqliteMcpActionStore> logger;

    // A single SqliteConnection is shared across all methods and is not safe for concurrent
    // use. This semaphore serializes every operation, which also guarantees that at most one
    // caller can claim any given approved action.
    private readonly SemaphoreSlim gate = new(1, 1);

    private bool disposed;

    public SqliteMcpActionStore(
        string databasePath,
        string databaseKey,
        TimeProvider timeProvider,
        ILogger<SqliteMcpActionStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseKey);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
        this.logger = logger;

        // Initialize the SQLCipher-compatible provider before any SQLite usage
        // (same convention as the encrypted memory store).
        SQLitePCL.Batteries_V2.Init();

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = databaseKey,
        };

        this.connection = new SqliteConnection(connectionString.ToString());
        this.connection.Open();

        this.ExecuteNonQuery("PRAGMA journal_mode=WAL;");
        this.ExecuteNonQuery("PRAGMA synchronous=FULL;");
        this.ExecuteNonQuery("PRAGMA foreign_keys=ON;");
        this.ExecuteNonQuery("PRAGMA busy_timeout=5000;");

        this.InitializeSchema();
        this.LogInitialized(databasePath);
    }

    // ──────────────────────────────────────────────
    //  Propose / read
    // ──────────────────────────────────────────────

    public async Task<McpAction> ProposeAsync(McpActionProposal proposal, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.ServerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.ToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.CanonicalArgumentsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.ArgumentsHash);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = this.connection.BeginTransaction();

            // Idempotent on (tenant, invocation): re-proposing returns the existing action.
            var byInvocation = await this.QuerySingleActionAsync(
                transaction,
                "tenant_id = @tenantId AND invocation_id = @invocationId",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@tenantId", proposal.TenantId);
                    cmd.Parameters.AddWithValue("@invocationId", proposal.InvocationId);
                },
                cancellationToken).ConfigureAwait(false);
            if (byInvocation is not null)
            {
                // True idempotency requires the SAME arguments. Silently returning the old
                // action for different arguments would let the caller believe its new
                // arguments were recorded.
                if (!string.Equals(byInvocation.ArgumentsHash, proposal.ArgumentsHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"MCP invocation '{proposal.InvocationId}' was already proposed with different arguments " +
                        $"(stored hash '{byInvocation.ArgumentsHash}', proposed hash '{proposal.ArgumentsHash}').");
                }

                return byInvocation;
            }

            // Deduplicate against a concurrent ACTIVE action with the same fingerprint
            // (enforced durably by ux_mcp_actions_active_fingerprint).
            var byFingerprint = await this.QuerySingleActionAsync(
                transaction,
                "tenant_id = @tenantId AND server_key = @serverKey AND tool_name = @toolName " +
                "AND arguments_sha256 = @argumentsHash AND status IN " + ActiveStatusList,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@tenantId", proposal.TenantId);
                    cmd.Parameters.AddWithValue("@serverKey", proposal.ServerKey);
                    cmd.Parameters.AddWithValue("@toolName", proposal.ToolName);
                    cmd.Parameters.AddWithValue("@argumentsHash", proposal.ArgumentsHash);
                },
                cancellationToken).ConfigureAwait(false);
            if (byFingerprint is not null)
            {
                return byFingerprint;
            }

            var actionId = Guid.CreateVersion7().ToString("N");
            await using (var cmd = this.CreateCommand(transaction, """
                INSERT INTO mcp_actions (
                    action_id, tenant_id, invocation_id, correlation_id, conversation_id, channel_id,
                    worker_id, server_key, tool_name, canonical_arguments_json, arguments_sha256,
                    status, proposal_expires_at_utc, created_at_utc, updated_at_utc, version)
                VALUES (
                    @actionId, @tenantId, @invocationId, @correlationId, @conversationId, @channelId,
                    @workerId, @serverKey, @toolName, @canonicalArgumentsJson, @argumentsHash,
                    'proposed', @proposalExpiresAtUtc, @createdAtUtc, @createdAtUtc, 0)
                """))
            {
                cmd.Parameters.AddWithValue("@actionId", actionId);
                cmd.Parameters.AddWithValue("@tenantId", proposal.TenantId);
                cmd.Parameters.AddWithValue("@invocationId", proposal.InvocationId);
                cmd.Parameters.AddWithValue("@correlationId", NullableText(proposal.CorrelationId));
                cmd.Parameters.AddWithValue("@conversationId", NullableText(proposal.ConversationId));
                cmd.Parameters.AddWithValue("@channelId", NullableText(proposal.ChannelId));
                cmd.Parameters.AddWithValue("@workerId", NullableText(proposal.WorkerId));
                cmd.Parameters.AddWithValue("@serverKey", proposal.ServerKey);
                cmd.Parameters.AddWithValue("@toolName", proposal.ToolName);
                cmd.Parameters.AddWithValue("@canonicalArgumentsJson", proposal.CanonicalArgumentsJson);
                cmd.Parameters.AddWithValue("@argumentsHash", proposal.ArgumentsHash);
                cmd.Parameters.AddWithValue("@proposalExpiresAtUtc", FormatUtc(proposal.ProposalExpiresAtUtc));
                cmd.Parameters.AddWithValue("@createdAtUtc", FormatUtc(proposal.CreatedAtUtc));
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await this.AppendEventAsync(
                transaction, actionId, fromStatus: null, toStatus: "proposed", eventType: "proposed",
                actor: proposal.WorkerId ?? "agent", detail: null, atUtc: proposal.CreatedAtUtc,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            this.LogProposed(actionId, proposal.ServerKey, proposal.ToolName);

            return new McpAction
            {
                ActionId = actionId,
                TenantId = proposal.TenantId,
                InvocationId = proposal.InvocationId,
                CorrelationId = proposal.CorrelationId,
                ConversationId = proposal.ConversationId,
                ChannelId = proposal.ChannelId,
                WorkerId = proposal.WorkerId,
                ServerKey = proposal.ServerKey,
                ToolName = proposal.ToolName,
                CanonicalArgumentsJson = proposal.CanonicalArgumentsJson,
                ArgumentsHash = proposal.ArgumentsHash,
                State = McpActionState.Proposed,
                ProposalExpiresAtUtc = proposal.ProposalExpiresAtUtc,
                CreatedAtUtc = proposal.CreatedAtUtc,
                UpdatedAtUtc = proposal.CreatedAtUtc,
                Version = 0,
            };
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<McpAction?> GetAsync(string tenantId, string actionId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await this.QuerySingleActionAsync(
                transaction: null,
                "tenant_id = @tenantId AND action_id = @actionId",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@actionId", actionId);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<IReadOnlyList<McpAction>> ListAsync(McpActionQuery query, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.TenantId);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = this.connection.CreateCommand();
            var sql = SelectActionSql + " WHERE tenant_id = @tenantId";
            cmd.Parameters.AddWithValue("@tenantId", query.TenantId);

            if (!string.IsNullOrEmpty(query.BeforeActionId))
            {
                sql += " AND action_id < @beforeActionId";
                cmd.Parameters.AddWithValue("@beforeActionId", query.BeforeActionId);
            }

            if (!string.IsNullOrEmpty(query.ServerKey))
            {
                sql += " AND server_key = @serverKey";
                cmd.Parameters.AddWithValue("@serverKey", query.ServerKey);
            }

            if (!string.IsNullOrEmpty(query.ToolName))
            {
                sql += " AND tool_name = @toolName";
                cmd.Parameters.AddWithValue("@toolName", query.ToolName);
            }

            if (query.State is { } state)
            {
                sql += " AND status = @status";
                cmd.Parameters.AddWithValue("@status", ToDbStatus(state));
            }

            if (!string.IsNullOrEmpty(query.WorkerId))
            {
                sql += " AND worker_id = @workerId";
                cmd.Parameters.AddWithValue("@workerId", query.WorkerId);
            }

            sql += " ORDER BY action_id DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 1000));
            cmd.CommandText = sql;

            var results = new List<McpAction>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(MapAction(reader));
            }

            return results;
        }
        finally
        {
            this.gate.Release();
        }
    }

    // ──────────────────────────────────────────────
    //  Decisions (approve / reject / cancel)
    // ──────────────────────────────────────────────

    public async Task<McpActionDecisionResult> ApproveAsync(string tenantId, string actionId, string expectedArgumentsHash, string actor, string? reason, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedArgumentsHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = this.connection.BeginTransaction();
            var action = await this.LoadActionAsync(transaction, tenantId, actionId, cancellationToken).ConfigureAwait(false);
            if (action is null)
            {
                return new McpActionDecisionResult(false, null, "not_found");
            }

            if (!string.Equals(action.ArgumentsHash, expectedArgumentsHash, StringComparison.Ordinal))
            {
                return new McpActionDecisionResult(false, action, "arguments_hash_mismatch");
            }

            if (!IsTransitionAllowed(action.State, McpActionState.Approved) || action.State != McpActionState.Proposed)
            {
                return new McpActionDecisionResult(false, action, "invalid_state");
            }

            var now = this.timeProvider.GetUtcNow();
            if (action.ProposalExpiresAtUtc <= now)
            {
                return new McpActionDecisionResult(false, action, "proposal_expired");
            }

            await using (var cmd = this.CreateCommand(transaction, """
                UPDATE mcp_actions
                SET status = 'approved', approval_expires_at_utc = @expiresAtUtc,
                    next_attempt_at_utc = NULL, updated_at_utc = @now, version = version + 1
                WHERE action_id = @actionId
                """))
            {
                cmd.Parameters.AddWithValue("@expiresAtUtc", FormatUtc(expiresAtUtc));
                cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                cmd.Parameters.AddWithValue("@actionId", actionId);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await this.InsertDecisionAsync(
                transaction, actionId, decision: "approved", expectedArgumentsHash, actor, reason,
                expiresAtUtc, now, cancellationToken).ConfigureAwait(false);
            await this.AppendEventAsync(
                transaction, actionId, "proposed", "approved", "approved", actor, reason, now,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            this.LogDecision(actionId, "approved", actor);

            var updated = action with
            {
                State = McpActionState.Approved,
                ApprovalExpiresAtUtc = expiresAtUtc,
                NextAttemptAtUtc = null,
                UpdatedAtUtc = now,
                Version = action.Version + 1,
            };
            return new McpActionDecisionResult(true, updated, null);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<McpActionDecisionResult> RejectAsync(string tenantId, string actionId, string expectedArgumentsHash, string actor, string? reason, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedArgumentsHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = this.connection.BeginTransaction();
            var action = await this.LoadActionAsync(transaction, tenantId, actionId, cancellationToken).ConfigureAwait(false);
            if (action is null)
            {
                return new McpActionDecisionResult(false, null, "not_found");
            }

            if (!string.Equals(action.ArgumentsHash, expectedArgumentsHash, StringComparison.Ordinal))
            {
                return new McpActionDecisionResult(false, action, "arguments_hash_mismatch");
            }

            if (!IsTransitionAllowed(action.State, McpActionState.Rejected))
            {
                return new McpActionDecisionResult(false, action, "invalid_state");
            }

            var now = this.timeProvider.GetUtcNow();
            await using (var cmd = this.CreateCommand(transaction, """
                UPDATE mcp_actions
                SET status = 'rejected', completed_at_utc = @now, updated_at_utc = @now,
                    version = version + 1
                WHERE action_id = @actionId
                """))
            {
                cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                cmd.Parameters.AddWithValue("@actionId", actionId);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await this.InsertDecisionAsync(
                transaction, actionId, decision: "rejected", expectedArgumentsHash, actor, reason,
                expiresAtUtc: null, now, cancellationToken).ConfigureAwait(false);
            await this.AppendEventAsync(
                transaction, actionId, "proposed", "rejected", "rejected", actor, reason, now,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            this.LogDecision(actionId, "rejected", actor);

            var updated = action with
            {
                State = McpActionState.Rejected,
                CompletedAtUtc = now,
                UpdatedAtUtc = now,
                Version = action.Version + 1,
            };
            return new McpActionDecisionResult(true, updated, null);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<McpActionCancelResult> CancelAsync(string tenantId, string actionId, string expectedArgumentsHash, string actor, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedArgumentsHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = this.connection.BeginTransaction();
            var action = await this.LoadActionAsync(transaction, tenantId, actionId, cancellationToken).ConfigureAwait(false);
            if (action is null)
            {
                return new McpActionCancelResult(false, null, "not_found");
            }

            if (!string.Equals(action.ArgumentsHash, expectedArgumentsHash, StringComparison.Ordinal))
            {
                return new McpActionCancelResult(false, action, "arguments_hash_mismatch");
            }

            var now = this.timeProvider.GetUtcNow();

            if (action.State is McpActionState.Proposed or McpActionState.Approved)
            {
                await using (var cmd = this.CreateCommand(transaction, """
                    UPDATE mcp_actions
                    SET status = 'cancelled', cancel_requested_at_utc = @now, completed_at_utc = @now,
                        updated_at_utc = @now, version = version + 1
                    WHERE action_id = @actionId
                    """))
                {
                    cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                    cmd.Parameters.AddWithValue("@actionId", actionId);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await this.AppendEventAsync(
                    transaction, actionId, ToDbStatus(action.State), "cancelled", "cancelled", actor,
                    detail: null, now, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                this.LogCancelled(actionId, actor);

                var updated = action with
                {
                    State = McpActionState.Cancelled,
                    CompletedAtUtc = now,
                    UpdatedAtUtc = now,
                    Version = action.Version + 1,
                };
                return new McpActionCancelResult(true, updated, null);
            }

            if (action.State == McpActionState.Dispatching)
            {
                // A dispatch is (possibly) in flight — cancelling now could lose a mutation that
                // already reached the remote server. Record the request; the dispatch outcome decides.
                await using (var cmd = this.CreateCommand(transaction, """
                    UPDATE mcp_actions
                    SET cancel_requested_at_utc = @now, updated_at_utc = @now, version = version + 1
                    WHERE action_id = @actionId
                    """))
                {
                    cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                    cmd.Parameters.AddWithValue("@actionId", actionId);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await this.AppendEventAsync(
                    transaction, actionId, "dispatching", "dispatching", "cancel_requested", actor,
                    detail: "cancel requested while dispatching; outcome decides", now,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                this.LogCancelRequested(actionId, actor);

                var updated = action with { UpdatedAtUtc = now, Version = action.Version + 1 };
                return new McpActionCancelResult(true, updated, null);
            }

            return new McpActionCancelResult(false, action, "invalid_state");
        }
        finally
        {
            this.gate.Release();
        }
    }

    // ──────────────────────────────────────────────
    //  Outbox (claim / complete / recover / expire)
    // ──────────────────────────────────────────────

    public async Task<McpActionDispatchLease?> TryClaimNextApprovedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = this.connection.BeginTransaction();
            var action = await this.QuerySingleActionAsync(
                transaction,
                "status = 'approved' " +
                "AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now) " +
                "AND (approval_expires_at_utc IS NULL OR approval_expires_at_utc > @now) " +
                "ORDER BY created_at_utc, action_id",
                cmd => cmd.Parameters.AddWithValue("@now", FormatUtc(now)),
                cancellationToken).ConfigureAwait(false);
            if (action is null)
            {
                return null;
            }

            int attemptNumber;
            await using (var cmd = this.CreateCommand(transaction, """
                SELECT COALESCE(MAX(attempt_number), 0) + 1 FROM mcp_action_attempts
                WHERE action_id = @actionId
                """))
            {
                cmd.Parameters.AddWithValue("@actionId", action.ActionId);
                attemptNumber = Convert.ToInt32(
                    await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            await using (var cmd = this.CreateCommand(transaction, """
                UPDATE mcp_actions
                SET status = 'dispatching', updated_at_utc = @now, version = version + 1
                WHERE action_id = @actionId AND status = 'approved'
                """))
            {
                cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                cmd.Parameters.AddWithValue("@actionId", action.ActionId);
                var updatedRows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (updatedRows != 1)
                {
                    throw new InvalidOperationException(
                        $"MCP action '{action.ActionId}' changed state while being claimed.");
                }
            }

            await using (var cmd = this.CreateCommand(transaction, """
                INSERT INTO mcp_action_attempts (action_id, attempt_number, outcome, started_at_utc)
                VALUES (@actionId, @attemptNumber, 'started', @now)
                """))
            {
                cmd.Parameters.AddWithValue("@actionId", action.ActionId);
                cmd.Parameters.AddWithValue("@attemptNumber", attemptNumber);
                cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await this.AppendEventAsync(
                transaction, action.ActionId, "approved", "dispatching", "dispatch_claimed", "outbox",
                detail: $"attempt {attemptNumber}", now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            this.LogClaimed(action.ActionId, attemptNumber);

            return new McpActionDispatchLease
            {
                ActionId = action.ActionId,
                AttemptNumber = attemptNumber,
                InvocationId = action.InvocationId,
                TenantId = action.TenantId,
                ServerKey = action.ServerKey,
                ToolName = action.ToolName,
                CanonicalArgumentsJson = action.CanonicalArgumentsJson,
                ArgumentsHash = action.ArgumentsHash,
                StartedAtUtc = now,
            };
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task CompleteAttemptAsync(McpActionDispatchCompletion completion, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentException.ThrowIfNullOrWhiteSpace(completion.ActionId);
        if (completion.State is not (McpActionState.Approved or McpActionState.Succeeded
            or McpActionState.Failed or McpActionState.OutcomeUnknown))
        {
            throw new ArgumentException(
                $"Dispatch completion state must be Approved, Succeeded, Failed, or OutcomeUnknown; got {completion.State}.",
                nameof(completion));
        }

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = this.connection.BeginTransaction();
            var action = await this.QuerySingleActionAsync(
                transaction,
                "action_id = @actionId",
                cmd => cmd.Parameters.AddWithValue("@actionId", completion.ActionId),
                cancellationToken).ConfigureAwait(false);
            if (action is null)
            {
                throw new InvalidOperationException($"MCP action '{completion.ActionId}' not found.");
            }

            if (action.State != McpActionState.Dispatching
                || !IsTransitionAllowed(action.State, completion.State))
            {
                throw new InvalidOperationException(
                    $"Cannot complete a dispatch attempt for MCP action '{completion.ActionId}' in state {action.State}.");
            }

            var attemptOutcome = completion.State switch
            {
                McpActionState.Approved => "not_started",
                McpActionState.Succeeded => "succeeded",
                McpActionState.Failed => "failed",
                _ => "outcome_unknown",
            };

            // completed_at_utc IS NULL binds this completion to the OPEN attempt row: a
            // late/duplicate completion of an already-closed attempt matches zero rows and is
            // rejected below, so it can never re-complete the action with stale evidence
            // while a newer attempt is in flight.
            await using (var cmd = this.CreateCommand(transaction, """
                UPDATE mcp_action_attempts
                SET outcome = @outcome, failure_kind = @failureKind, completed_at_utc = @completedAtUtc,
                    result_content = @resultContent, error = @error, remote_reference = @remoteReference
                WHERE action_id = @actionId AND attempt_number = @attemptNumber
                    AND completed_at_utc IS NULL
                """))
            {
                cmd.Parameters.AddWithValue("@outcome", attemptOutcome);
                cmd.Parameters.AddWithValue("@failureKind", completion.FailureKind == McpFailureKind.None
                    ? DBNull.Value
                    : completion.FailureKind.ToString());
                cmd.Parameters.AddWithValue("@completedAtUtc", FormatUtc(completion.CompletedAtUtc));
                cmd.Parameters.AddWithValue("@resultContent", NullableText(completion.ResultContent));
                cmd.Parameters.AddWithValue("@error", NullableText(completion.Error));
                cmd.Parameters.AddWithValue("@remoteReference", NullableText(completion.RemoteReference));
                cmd.Parameters.AddWithValue("@actionId", completion.ActionId);
                cmd.Parameters.AddWithValue("@attemptNumber", completion.AttemptNumber);
                var updatedRows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (updatedRows != 1)
                {
                    throw new InvalidOperationException(
                        $"Dispatch attempt {completion.AttemptNumber} for MCP action '{completion.ActionId}' is unknown or already completed.");
                }
            }

            if (completion.State == McpActionState.Approved
                && await this.HasPendingCancelAsync(transaction, completion.ActionId, cancellationToken).ConfigureAwait(false))
            {
                // The attempt positively did not start AND a cancel was accepted while it was
                // in flight: honor the cancel now instead of returning the mutation to the
                // outbox (where it would execute on the next attempt). This collapses the two
                // legal edges dispatching → approved → cancelled into one committed transition;
                // the audit trail records both edges.
                await using (var cmd = this.CreateCommand(transaction, """
                    UPDATE mcp_actions
                    SET status = 'cancelled', next_attempt_at_utc = NULL, error = @error,
                        completed_at_utc = @now, updated_at_utc = @now, version = version + 1
                    WHERE action_id = @actionId
                    """))
                {
                    cmd.Parameters.AddWithValue("@actionId", completion.ActionId);
                    cmd.Parameters.AddWithValue("@now", FormatUtc(completion.CompletedAtUtc));
                    cmd.Parameters.AddWithValue("@error", NullableText(completion.Error));
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await this.AppendEventAsync(
                    transaction, completion.ActionId, "dispatching", "approved", "dispatch_not_started",
                    "outbox", detail: completion.Error ?? completion.FailureKind.ToString(),
                    completion.CompletedAtUtc, cancellationToken).ConfigureAwait(false);
                await this.AppendEventAsync(
                    transaction, completion.ActionId, "approved", "cancelled", "cancelled", "system",
                    detail: "pending cancel honored: attempt positively did not start",
                    completion.CompletedAtUtc, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                this.LogAttemptCompleted(completion.ActionId, completion.AttemptNumber, attemptOutcome);
                this.LogPendingCancelHonored(completion.ActionId);
                return;
            }

            var (actionSql, eventType) = completion.State switch
            {
                McpActionState.Approved => ("""
                    UPDATE mcp_actions
                    SET status = 'approved', next_attempt_at_utc = @retryAtUtc, error = @error,
                        updated_at_utc = @now, version = version + 1
                    WHERE action_id = @actionId
                    """, "dispatch_not_started"),
                McpActionState.Succeeded => ("""
                    UPDATE mcp_actions
                    SET status = 'succeeded', result_content = @resultContent,
                        remote_reference = @remoteReference, error = NULL, completed_at_utc = @now,
                        updated_at_utc = @now, version = version + 1
                    WHERE action_id = @actionId
                    """, "dispatch_succeeded"),
                McpActionState.Failed => ("""
                    UPDATE mcp_actions
                    SET status = 'failed', error = @error, remote_reference = @remoteReference,
                        completed_at_utc = @now, updated_at_utc = @now, version = version + 1
                    WHERE action_id = @actionId
                    """, "dispatch_failed"),
                _ => ("""
                    UPDATE mcp_actions
                    SET status = 'outcome_unknown', error = @error, updated_at_utc = @now,
                        version = version + 1
                    WHERE action_id = @actionId
                    """, "dispatch_outcome_unknown"),
            };

            await using (var cmd = this.CreateCommand(transaction, actionSql))
            {
                cmd.Parameters.AddWithValue("@actionId", completion.ActionId);
                cmd.Parameters.AddWithValue("@now", FormatUtc(completion.CompletedAtUtc));
                if (completion.State == McpActionState.Approved)
                {
                    cmd.Parameters.AddWithValue("@retryAtUtc", NullableUtc(completion.RetryAtUtc));
                    cmd.Parameters.AddWithValue("@error", NullableText(completion.Error));
                }
                else if (completion.State == McpActionState.Succeeded)
                {
                    cmd.Parameters.AddWithValue("@resultContent", NullableText(completion.ResultContent));
                    cmd.Parameters.AddWithValue("@remoteReference", NullableText(completion.RemoteReference));
                }
                else if (completion.State == McpActionState.Failed)
                {
                    cmd.Parameters.AddWithValue("@error", NullableText(completion.Error));
                    cmd.Parameters.AddWithValue("@remoteReference", NullableText(completion.RemoteReference));
                }
                else
                {
                    cmd.Parameters.AddWithValue("@error", NullableText(completion.Error));
                }

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await this.AppendEventAsync(
                transaction, completion.ActionId, "dispatching", ToDbStatus(completion.State), eventType,
                "outbox", detail: completion.Error ?? completion.FailureKind.ToString(),
                completion.CompletedAtUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            this.LogAttemptCompleted(completion.ActionId, completion.AttemptNumber, attemptOutcome);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<int> RecoverInterruptedDispatchesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = this.connection.BeginTransaction();

            var interrupted = new List<string>();
            await using (var cmd = this.CreateCommand(transaction,
                "SELECT action_id FROM mcp_actions WHERE status = 'dispatching' ORDER BY action_id"))
            {
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    interrupted.Add(reader.GetString(0));
                }
            }

            foreach (var actionId in interrupted)
            {
                // A dispatch that was in flight when the Bridge died may or may not have reached
                // the remote server — never assume success (or failure); require reconciliation.
                await using (var cmd = this.CreateCommand(transaction, """
                    UPDATE mcp_actions
                    SET status = 'outcome_unknown',
                        error = COALESCE(error, 'dispatch interrupted by Bridge restart'),
                        updated_at_utc = @now, version = version + 1
                    WHERE action_id = @actionId AND status = 'dispatching'
                    """))
                {
                    cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                    cmd.Parameters.AddWithValue("@actionId", actionId);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var cmd = this.CreateCommand(transaction, """
                    UPDATE mcp_action_attempts
                    SET outcome = 'outcome_unknown', completed_at_utc = @now,
                        error = COALESCE(error, 'dispatch interrupted by Bridge restart')
                    WHERE action_id = @actionId AND completed_at_utc IS NULL
                    """))
                {
                    cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                    cmd.Parameters.AddWithValue("@actionId", actionId);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await this.AppendEventAsync(
                    transaction, actionId, "dispatching", "outcome_unknown", "recovered_interrupted",
                    "system", detail: "dispatch was in flight during a Bridge restart", now,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (interrupted.Count > 0)
            {
                this.LogRecovered(interrupted.Count);
            }

            return interrupted.Count;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<int> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = this.connection.BeginTransaction();

            var due = new List<(string ActionId, string FromStatus)>();
            await using (var cmd = this.CreateCommand(transaction, """
                SELECT action_id, status FROM mcp_actions
                WHERE (status = 'proposed' AND proposal_expires_at_utc <= @now)
                   OR (status = 'approved' AND approval_expires_at_utc IS NOT NULL AND approval_expires_at_utc <= @now)
                ORDER BY action_id
                """))
            {
                cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    due.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var (actionId, fromStatus) in due)
            {
                await using (var cmd = this.CreateCommand(transaction, """
                    UPDATE mcp_actions
                    SET status = 'expired', completed_at_utc = @now, updated_at_utc = @now,
                        version = version + 1
                    WHERE action_id = @actionId
                    """))
                {
                    cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                    cmd.Parameters.AddWithValue("@actionId", actionId);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await this.AppendEventAsync(
                    transaction, actionId, fromStatus, "expired", "expired", "system",
                    detail: null, now, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (due.Count > 0)
            {
                this.LogExpired(due.Count);
            }

            return due.Count;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<McpActionDecisionResult> ReconcileAsync(string tenantId, string actionId, string expectedArgumentsHash, bool succeeded, string actor, string evidence, string? remoteReference, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedArgumentsHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = this.connection.BeginTransaction();
            var action = await this.LoadActionAsync(transaction, tenantId, actionId, cancellationToken).ConfigureAwait(false);
            if (action is null)
            {
                return new McpActionDecisionResult(false, null, "not_found");
            }

            if (!string.Equals(action.ArgumentsHash, expectedArgumentsHash, StringComparison.Ordinal))
            {
                return new McpActionDecisionResult(false, action, "arguments_hash_mismatch");
            }

            var targetState = succeeded ? McpActionState.ReconciledSucceeded : McpActionState.ReconciledFailed;
            var targetStatus = ToDbStatus(targetState);
            if (!IsTransitionAllowed(action.State, targetState))
            {
                return new McpActionDecisionResult(false, action, "invalid_state");
            }

            var now = this.timeProvider.GetUtcNow();
            await using (var cmd = this.CreateCommand(transaction, """
                UPDATE mcp_actions
                SET status = @status, remote_reference = COALESCE(@remoteReference, remote_reference),
                    completed_at_utc = @now, updated_at_utc = @now, version = version + 1
                WHERE action_id = @actionId
                """))
            {
                cmd.Parameters.AddWithValue("@status", targetStatus);
                cmd.Parameters.AddWithValue("@remoteReference", NullableText(remoteReference));
                cmd.Parameters.AddWithValue("@now", FormatUtc(now));
                cmd.Parameters.AddWithValue("@actionId", actionId);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await this.AppendEventAsync(
                transaction, actionId, "outcome_unknown", targetStatus, "reconciled", actor,
                detail: evidence, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            this.LogReconciled(actionId, targetStatus, actor);

            var updated = action with
            {
                State = targetState,
                RemoteReference = remoteReference ?? action.RemoteReference,
                CompletedAtUtc = now,
                UpdatedAtUtc = now,
                Version = action.Version + 1,
            };
            return new McpActionDecisionResult(true, updated, null);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        // Fail new operations fast, then wait for any in-flight operation to release the gate
        // before tearing down, so its transaction (and `finally { gate.Release(); }`) never
        // observes a disposed gate or connection.
        this.disposed = true;
        await this.gate.WaitAsync().ConfigureAwait(false);
        this.gate.Dispose();
        SqliteConnection.ClearPool(this.connection);
        await this.connection.DisposeAsync().ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────
    //  State machine
    // ──────────────────────────────────────────────

    /// <summary>
    /// The single source of truth for allowed transitions. Everything else — including
    /// dispatching → approved, which is legal ONLY when the attempt is positively known not to
    /// have started (see <see cref="CompleteAttemptAsync"/>) — is rejected. Terminal states
    /// (succeeded, failed, rejected, reconciled_*, expired, cancelled) allow no transitions.
    /// </summary>
    internal static bool IsTransitionAllowed(McpActionState from, McpActionState to) => (from, to) switch
    {
        (McpActionState.Proposed, McpActionState.Approved) => true,
        (McpActionState.Proposed, McpActionState.Rejected) => true,
        (McpActionState.Proposed, McpActionState.Cancelled) => true,
        (McpActionState.Proposed, McpActionState.Expired) => true,
        (McpActionState.Approved, McpActionState.Dispatching) => true,
        (McpActionState.Approved, McpActionState.Cancelled) => true,
        (McpActionState.Approved, McpActionState.Expired) => true,
        (McpActionState.Dispatching, McpActionState.Succeeded) => true,
        (McpActionState.Dispatching, McpActionState.Failed) => true,
        (McpActionState.Dispatching, McpActionState.OutcomeUnknown) => true,
        (McpActionState.Dispatching, McpActionState.Approved) => true,
        (McpActionState.OutcomeUnknown, McpActionState.ReconciledSucceeded) => true,
        (McpActionState.OutcomeUnknown, McpActionState.ReconciledFailed) => true,
        _ => false,
    };

    private static string ToDbStatus(McpActionState state) => state switch
    {
        McpActionState.Proposed => "proposed",
        McpActionState.Approved => "approved",
        McpActionState.Rejected => "rejected",
        McpActionState.Dispatching => "dispatching",
        McpActionState.Succeeded => "succeeded",
        McpActionState.Failed => "failed",
        McpActionState.OutcomeUnknown => "outcome_unknown",
        McpActionState.ReconciledSucceeded => "reconciled_succeeded",
        McpActionState.ReconciledFailed => "reconciled_failed",
        McpActionState.Expired => "expired",
        McpActionState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown MCP action state."),
    };

    private static McpActionState FromDbStatus(string status) => status switch
    {
        "proposed" => McpActionState.Proposed,
        "approved" => McpActionState.Approved,
        "rejected" => McpActionState.Rejected,
        "dispatching" => McpActionState.Dispatching,
        "succeeded" => McpActionState.Succeeded,
        "failed" => McpActionState.Failed,
        "outcome_unknown" => McpActionState.OutcomeUnknown,
        "reconciled_succeeded" => McpActionState.ReconciledSucceeded,
        "reconciled_failed" => McpActionState.ReconciledFailed,
        "expired" => McpActionState.Expired,
        "cancelled" => McpActionState.Cancelled,
        _ => throw new InvalidOperationException($"Unknown MCP action status '{status}' in database."),
    };

    // ──────────────────────────────────────────────
    //  SQL helpers
    // ──────────────────────────────────────────────

    private SqliteCommand CreateCommand(SqliteTransaction? transaction, string sql)
    {
        var cmd = this.connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private async Task<bool> HasPendingCancelAsync(SqliteTransaction transaction, string actionId, CancellationToken cancellationToken)
    {
        await using var cmd = this.CreateCommand(transaction, """
            SELECT cancel_requested_at_utc FROM mcp_actions WHERE action_id = @actionId
            """);
        cmd.Parameters.AddWithValue("@actionId", actionId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string;
    }

    private Task<McpAction?> LoadActionAsync(SqliteTransaction transaction, string tenantId, string actionId, CancellationToken cancellationToken)
        => this.QuerySingleActionAsync(
            transaction,
            "tenant_id = @tenantId AND action_id = @actionId",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@tenantId", tenantId);
                cmd.Parameters.AddWithValue("@actionId", actionId);
            },
            cancellationToken);

    private async Task<McpAction?> QuerySingleActionAsync(
        SqliteTransaction? transaction,
        string whereAndTail,
        Action<SqliteCommand> bindParameters,
        CancellationToken cancellationToken)
    {
        await using var cmd = this.CreateCommand(transaction, SelectActionSql + " WHERE " + whereAndTail + " LIMIT 1");
        bindParameters(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapAction(reader);
    }

    private async Task InsertDecisionAsync(
        SqliteTransaction transaction,
        string actionId,
        string decision,
        string argumentsHash,
        string actor,
        string? reason,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var cmd = this.CreateCommand(transaction, """
            INSERT INTO mcp_action_decisions (
                decision_id, action_id, decision, arguments_sha256, actor, reason,
                expires_at_utc, decided_at_utc)
            VALUES (@decisionId, @actionId, @decision, @argumentsHash, @actor, @reason,
                @expiresAtUtc, @decidedAtUtc)
            """);
        cmd.Parameters.AddWithValue("@decisionId", Guid.CreateVersion7().ToString("N"));
        cmd.Parameters.AddWithValue("@actionId", actionId);
        cmd.Parameters.AddWithValue("@decision", decision);
        cmd.Parameters.AddWithValue("@argumentsHash", argumentsHash);
        cmd.Parameters.AddWithValue("@actor", actor);
        cmd.Parameters.AddWithValue("@reason", NullableText(reason));
        cmd.Parameters.AddWithValue("@expiresAtUtc", NullableUtc(expiresAtUtc));
        cmd.Parameters.AddWithValue("@decidedAtUtc", FormatUtc(decidedAtUtc));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendEventAsync(
        SqliteTransaction transaction,
        string actionId,
        string? fromStatus,
        string toStatus,
        string eventType,
        string actor,
        string? detail,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        await using var cmd = this.CreateCommand(transaction, """
            INSERT INTO mcp_action_events (
                action_id, from_status, to_status, event_type, actor, detail, created_at_utc)
            VALUES (@actionId, @fromStatus, @toStatus, @eventType, @actor, @detail, @createdAtUtc)
            """);
        cmd.Parameters.AddWithValue("@actionId", actionId);
        cmd.Parameters.AddWithValue("@fromStatus", NullableText(fromStatus));
        cmd.Parameters.AddWithValue("@toStatus", toStatus);
        cmd.Parameters.AddWithValue("@eventType", eventType);
        cmd.Parameters.AddWithValue("@actor", actor);
        cmd.Parameters.AddWithValue("@detail", NullableText(detail));
        cmd.Parameters.AddWithValue("@createdAtUtc", FormatUtc(atUtc));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static McpAction MapAction(SqliteDataReader reader) => new()
    {
        ActionId = reader.GetString(0),
        TenantId = reader.GetString(1),
        InvocationId = reader.GetString(2),
        CorrelationId = reader.IsDBNull(3) ? null : reader.GetString(3),
        ConversationId = reader.IsDBNull(4) ? null : reader.GetString(4),
        ChannelId = reader.IsDBNull(5) ? null : reader.GetString(5),
        WorkerId = reader.IsDBNull(6) ? null : reader.GetString(6),
        ServerKey = reader.GetString(7),
        ToolName = reader.GetString(8),
        CanonicalArgumentsJson = reader.GetString(9),
        ArgumentsHash = reader.GetString(10),
        State = FromDbStatus(reader.GetString(11)),
        ProposalExpiresAtUtc = ParseUtc(reader.GetString(12)),
        ApprovalExpiresAtUtc = reader.IsDBNull(13) ? null : ParseUtc(reader.GetString(13)),
        NextAttemptAtUtc = reader.IsDBNull(14) ? null : ParseUtc(reader.GetString(14)),
        ResultContent = reader.IsDBNull(15) ? null : reader.GetString(15),
        Error = reader.IsDBNull(16) ? null : reader.GetString(16),
        RemoteReference = reader.IsDBNull(17) ? null : reader.GetString(17),
        CreatedAtUtc = ParseUtc(reader.GetString(18)),
        UpdatedAtUtc = ParseUtc(reader.GetString(19)),
        CompletedAtUtc = reader.IsDBNull(20) ? null : ParseUtc(reader.GetString(20)),
        Version = reader.GetInt32(21),
    };

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string text)
        => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static object NullableText(string? value)
        => (object?)value ?? DBNull.Value;

    private static object NullableUtc(DateTimeOffset? value)
        => value is { } utc ? FormatUtc(utc) : DBNull.Value;

    // ──────────────────────────────────────────────
    //  Schema
    // ──────────────────────────────────────────────

    private void InitializeSchema()
    {
        // NEVER drop/recreate this database: it is the durable store of record for
        // approval-gated MCP mutations. Migrations must be additive, gated on user_version.
        var version = this.GetSchemaVersion();
        if (version >= 1)
        {
            return;
        }

        using var transaction = this.connection.BeginTransaction();
        using var cmd = this.connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE mcp_actions (
                action_id                  TEXT PRIMARY KEY,
                tenant_id                  TEXT NOT NULL,
                invocation_id              TEXT NOT NULL,
                correlation_id             TEXT,
                conversation_id            TEXT,
                channel_id                 TEXT,
                worker_id                  TEXT,
                server_key                 TEXT NOT NULL,
                tool_name                  TEXT NOT NULL,
                canonical_arguments_json   TEXT NOT NULL,
                arguments_sha256           TEXT NOT NULL,
                status                     TEXT NOT NULL,
                proposal_expires_at_utc    TEXT NOT NULL,
                approval_expires_at_utc    TEXT,
                next_attempt_at_utc        TEXT,
                cancel_requested_at_utc    TEXT,
                result_content             TEXT,
                error                      TEXT,
                remote_reference           TEXT,
                created_at_utc             TEXT NOT NULL,
                updated_at_utc             TEXT NOT NULL,
                completed_at_utc           TEXT,
                version                    INTEGER NOT NULL DEFAULT 0,
                UNIQUE (tenant_id, invocation_id)
            );
            CREATE UNIQUE INDEX ux_mcp_actions_active_fingerprint ON mcp_actions (tenant_id, server_key, tool_name, arguments_sha256) WHERE status IN ('proposed','approved','dispatching','outcome_unknown');
            CREATE INDEX ix_mcp_actions_outbox ON mcp_actions (status, next_attempt_at_utc, created_at_utc) WHERE status = 'approved';
            CREATE TABLE mcp_action_decisions (
                decision_id       TEXT PRIMARY KEY,
                action_id         TEXT NOT NULL REFERENCES mcp_actions(action_id),
                decision          TEXT NOT NULL CHECK (decision IN ('approved','rejected')),
                arguments_sha256  TEXT NOT NULL,
                actor             TEXT NOT NULL,
                reason            TEXT,
                expires_at_utc    TEXT,
                decided_at_utc    TEXT NOT NULL,
                UNIQUE (action_id)
            );
            CREATE TABLE mcp_action_attempts (
                attempt_id        INTEGER PRIMARY KEY AUTOINCREMENT,
                action_id         TEXT NOT NULL REFERENCES mcp_actions(action_id),
                attempt_number    INTEGER NOT NULL,
                outcome           TEXT NOT NULL,
                failure_kind      TEXT,
                started_at_utc    TEXT NOT NULL,
                completed_at_utc  TEXT,
                result_content    TEXT,
                error             TEXT,
                remote_reference  TEXT,
                UNIQUE (action_id, attempt_number)
            );
            CREATE TABLE mcp_action_events (
                event_id       INTEGER PRIMARY KEY AUTOINCREMENT,
                action_id      TEXT NOT NULL REFERENCES mcp_actions(action_id),
                from_status    TEXT,
                to_status      TEXT NOT NULL,
                event_type     TEXT NOT NULL,
                actor          TEXT NOT NULL,
                detail         TEXT,
                created_at_utc TEXT NOT NULL
            );

            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    private int GetSchemaVersion()
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void ExecuteNonQuery(string sql)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ──────────────────────────────────────────────
    //  LoggerMessage
    // ──────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP action store initialized at {DatabasePath}")]
    private partial void LogInitialized(string databasePath);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP action {ActionId} proposed for {ServerKey}/{ToolName}")]
    private partial void LogProposed(string actionId, string serverKey, string toolName);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP action {ActionId} {Decision} by {Actor}")]
    private partial void LogDecision(string actionId, string decision, string actor);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP action {ActionId} cancelled by {Actor}")]
    private partial void LogCancelled(string actionId, string actor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancel requested for dispatching MCP action {ActionId} by {Actor}")]
    private partial void LogCancelRequested(string actionId, string actor);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP action {ActionId} claimed for dispatch attempt {AttemptNumber}")]
    private partial void LogClaimed(string actionId, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP action {ActionId} attempt {AttemptNumber} completed: {Outcome}")]
    private partial void LogAttemptCompleted(string actionId, int attemptNumber, string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP action {ActionId} cancelled: pending cancel honored after the attempt positively did not start")]
    private partial void LogPendingCancelHonored(string actionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered {Count} interrupted MCP dispatch(es) to outcome_unknown after restart")]
    private partial void LogRecovered(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} MCP action(s)")]
    private partial void LogExpired(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP action {ActionId} reconciled to {TargetStatus} by {Actor}")]
    private partial void LogReconciled(string actionId, string targetStatus, string actor);
}
