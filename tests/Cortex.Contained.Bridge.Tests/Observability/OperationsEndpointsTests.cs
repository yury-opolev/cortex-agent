using System.Security.Cryptography;
using System.Text.Json;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Bridge.Mcp.Actions;
using Cortex.Contained.Bridge.Tests.Mcp.Actions;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Observability;

/// <summary>
/// Proves the generic operational-observability endpoints: both require authorization, the
/// live subagent endpoint 503s when the agent is disconnected, the MCP action history endpoint
/// stays Bridge-local (works while the agent is disconnected) and filters correctly, and NEITHER
/// endpoint ever exposes canonical arguments or result content.
/// </summary>
[Collection(McpActionStoreCollectionDefinition.Name)]
public sealed class OperationsEndpointsTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";

    private static readonly DateTimeOffset StartTime = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly FakeTimeProvider _timeProvider;
    private SqliteMcpActionStore _store = null!;

    public OperationsEndpointsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "operations-endpoints-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _timeProvider = new FakeTimeProvider(StartTime);
    }

    public Task InitializeAsync()
    {
        _store = CreateStore(_tempDir, _timeProvider);
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

    private static int StatusOf(IResult result)
    {
        var statusCodeResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.NotNull(statusCodeResult.StatusCode);
        return statusCodeResult.StatusCode!.Value;
    }

    /// <summary>Extracts the JSON-serialized body of a <c>Results.Json(...)</c> result via its Value property.</summary>
    private static JsonDocument BodyOf(IResult result)
    {
        var valueProp = result.GetType().GetProperty("Value");
        Assert.NotNull(valueProp);
        var value = valueProp!.GetValue(result);
        Assert.NotNull(value);
        var json = JsonSerializer.Serialize(value, value!.GetType());
        return JsonDocument.Parse(json);
    }

    private async Task<McpAction> ProposeAsync(
        string serverKey = "github", string toolName = "create_issue", string? workerId = null)
    {
        var proposal = new McpActionProposal
        {
            TenantId = Tenant,
            InvocationId = Guid.CreateVersion7().ToString("N"),
            ServerKey = serverKey,
            ToolName = toolName,
            WorkerId = workerId,
            CanonicalArgumentsJson = """{"title":"secret title","body":"secret body"}""",
            ArgumentsHash = "sha256:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            CreatedAtUtc = _timeProvider.GetUtcNow(),
            ProposalExpiresAtUtc = _timeProvider.GetUtcNow().AddHours(1),
        };
        return await _store.ProposeAsync(proposal, CancellationToken.None);
    }

    // ── Route mapping: both endpoints require authorization ────────────────

    [Fact]
    public void AllOperationsRoutes_RequireAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapOperationsEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/tenants", StringComparison.Ordinal)
                || endpoint.RoutePattern.RawText!.StartsWith("/api/operations", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, endpoint =>
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
    }

    [Fact]
    public void Subagents_RequiresAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapOperationsEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/tenants/{tenantId}/operations/subagents");

        Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>());
    }

    [Fact]
    public void Actions_RequiresAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapOperationsEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/operations/mcp-actions");

        Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>());
    }

    // ── Subagents: disconnected agent → 503, clamping ───────────────────────

    [Fact]
    public async Task Subagents_DisconnectedAgent_Returns503()
    {
        var client = new HubClient(NullLogger<HubClient>.Instance); // never connected

        var result = await OperationsEndpoints.GetSubagentSnapshotsAsync(
            client, new SubagentSnapshotQuery(), CancellationToken.None);

        Assert.Equal(503, StatusOf(result));
    }

    [Fact]
    public async Task Subagents_NullClient_Returns503()
    {
        var result = await OperationsEndpoints.GetSubagentSnapshotsAsync(
            null, new SubagentSnapshotQuery(), CancellationToken.None);

        Assert.Equal(503, StatusOf(result));
    }

    [Theory]
    [InlineData(0, 1)] // below minimum -> clamped up
    [InlineData(5000, 1000)] // above maximum -> clamped down
    [InlineData(50, 50)] // within range -> unchanged
    public void Subagents_ClampsLimitAndStaleThreshold_Limit(int requested, int expected)
    {
        var query = OperationsEndpoints.BuildSubagentQuery(requested, includeTerminal: true, staleAfterSeconds: 600);

        Assert.Equal(expected, query.Limit);
    }

    [Theory]
    [InlineData(0, 1)] // below minimum -> clamped up to 1
    [InlineData(-100, 1)]
    [InlineData(3600, 3600)] // no upper bound documented -> passes through
    public void Subagents_ClampsLimitAndStaleThreshold_StaleAfterSeconds(int requested, int expected)
    {
        var query = OperationsEndpoints.BuildSubagentQuery(100, includeTerminal: true, staleAfterSeconds: requested);

        Assert.Equal(expected, query.StaleAfterSeconds);
    }

    [Fact]
    public void Subagents_ClampsLimitAndStaleThreshold_MissingParams_UseDocumentedDefaults()
    {
        var query = OperationsEndpoints.BuildSubagentQuery(limit: null, includeTerminal: null, staleAfterSeconds: null);

        Assert.Equal(100, query.Limit);
        Assert.True(query.IncludeTerminal);
        Assert.Equal(600, query.StaleAfterSeconds);
    }

    [Fact]
    public void Subagents_ClampsLimitAndStaleThreshold_IncludeTerminalFalse_IsPreserved()
    {
        var query = OperationsEndpoints.BuildSubagentQuery(null, includeTerminal: false, staleAfterSeconds: null);

        Assert.False(query.IncludeTerminal);
    }

    // ── MCP action history: filters, availability while disconnected, redaction ────

    [Fact]
    public async Task Actions_FiltersByServerOutcomeAndWorker()
    {
        await ProposeAsync(serverKey: "github", toolName: "list_prs", workerId: "sa-9"); // different worker — excluded
        await ProposeAsync(serverKey: "jira", toolName: "create_ticket", workerId: "sa-2"); // different server — excluded
        var matching = await ProposeAsync(serverKey: "github", toolName: "create_issue", workerId: "sa-1");

        var result = await OperationsEndpoints.ListActionsAsync(
            _store, Tenant,
            beforeId: null, limit: null,
            serverKey: "github", toolName: null, outcome: "proposed", workerTaskId: "sa-1",
            CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        var body = BodyOf(result);
        var actions = body.RootElement.GetProperty("actions").EnumerateArray().ToList();

        Assert.Single(actions);
        Assert.Equal(matching.ActionId, actions[0].GetProperty("actionId").GetString());
        Assert.Equal("github", actions[0].GetProperty("serverKey").GetString());
        Assert.Equal("sa-1", actions[0].GetProperty("workerId").GetString());
        Assert.Equal("proposed", actions[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Actions_UnknownOutcome_Returns400()
    {
        var result = await OperationsEndpoints.ListActionsAsync(
            _store, Tenant,
            beforeId: null, limit: null,
            serverKey: null, toolName: null, outcome: "not-a-real-outcome", workerTaskId: null,
            CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task Actions_NoFilters_ReturnsAllForTenant()
    {
        await ProposeAsync(serverKey: "github");
        await ProposeAsync(serverKey: "jira");

        var result = await OperationsEndpoints.ListActionsAsync(
            _store, Tenant,
            beforeId: null, limit: null,
            serverKey: null, toolName: null, outcome: null, workerTaskId: null,
            CancellationToken.None);

        var body = BodyOf(result);
        Assert.Equal(2, body.RootElement.GetProperty("actions").GetArrayLength());
    }

    [Fact]
    public async Task Actions_DoesNotExposeArgumentsOrResultContent()
    {
        await ProposeAsync();

        var result = await OperationsEndpoints.ListActionsAsync(
            _store, Tenant,
            beforeId: null, limit: null,
            serverKey: null, toolName: null, outcome: null, workerTaskId: null,
            CancellationToken.None);

        var body = BodyOf(result);
        var json = body.RootElement.ToString();

        // The proposal's canonical arguments contain "secret title" / "secret body" — if the
        // projection ever leaked CanonicalArgumentsJson, these substrings would appear.
        Assert.DoesNotContain("secret title", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret body", json, StringComparison.Ordinal);

        // Closed-field proof at the key level too.
        var action = body.RootElement.GetProperty("actions").EnumerateArray().Single();
        var propertyNames = action.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("canonicalArgumentsJson", propertyNames);
        Assert.DoesNotContain("argumentsJson", propertyNames);
        Assert.DoesNotContain("resultContent", propertyNames);
        Assert.DoesNotContain("error", propertyNames);

        // But the identifying/operational fields ARE present.
        Assert.Contains("actionId", propertyNames);
        Assert.Contains("argumentsHash", propertyNames);
        Assert.Contains("outcome", propertyNames);
        Assert.Contains("serverKey", propertyNames);
        Assert.Contains("toolName", propertyNames);
        Assert.Contains("workerId", propertyNames);
        Assert.Contains("correlationId", propertyNames);
    }

    [Fact]
    public void ProjectAction_NeverIncludesCanonicalArgumentsOrResultOrError()
    {
        var action = new McpAction
        {
            ActionId = "act-1",
            TenantId = Tenant,
            InvocationId = "inv-1",
            ServerKey = "github",
            ToolName = "create_issue",
            CanonicalArgumentsJson = """{"title":"top secret"}""",
            ArgumentsHash = "sha256:abc",
            State = McpActionState.Succeeded,
            ProposalExpiresAtUtc = StartTime.AddHours(1),
            ResultContent = "confidential result payload",
            Error = "some internal error text",
            CreatedAtUtc = StartTime,
            UpdatedAtUtc = StartTime,
            CompletedAtUtc = StartTime.AddSeconds(5),
        };

        var projected = OperationsEndpoints.ProjectAction(action);
        var json = JsonSerializer.Serialize(projected, projected.GetType());

        Assert.DoesNotContain("top secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("confidential result payload", json, StringComparison.Ordinal);
        Assert.DoesNotContain("internal error text", json, StringComparison.Ordinal);
        Assert.Contains("act-1", json, StringComparison.Ordinal);
        Assert.Contains("5000", json, StringComparison.Ordinal); // durationMs
    }

    [Fact]
    public async Task Actions_WorksWhileAgentDisconnected()
    {
        // No HubClient/TenantRouter is involved at all in ListActionsAsync — proving the
        // MCP action history endpoint is entirely Bridge-local.
        await ProposeAsync();

        var result = await OperationsEndpoints.ListActionsAsync(
            _store, Tenant,
            beforeId: null, limit: null,
            serverKey: null, toolName: null, outcome: null, workerTaskId: null,
            CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
    }
}
