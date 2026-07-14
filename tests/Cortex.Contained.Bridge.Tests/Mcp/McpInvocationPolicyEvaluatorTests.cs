using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpInvocationPolicyEvaluatorTests
{
    private static McpKustoReadBoundsConfig ValidBounds() => new()
    {
        AllowedCluster = "https://help.kusto.windows.net",
        AllowedDatabase = "IncidentDb",
        MaxLookbackHours = 24,
        MaxRowLimit = 1000,
    };

    [Fact]
    public void EvaluateKustoRead_AllFieldsWithinBounds_ReturnsNull()
    {
        var error = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(),
            cluster: "https://help.kusto.windows.net",
            database: "IncidentDb",
            lookbackHours: 12,
            rowLimit: 500,
            query: "IncidentTable | take 10");

        Assert.Null(error);
    }

    [Fact]
    public void Policy_MissingStructuredBound_RejectsBeforeDispatch()
    {
        // Any missing structured field (here: no query) must be rejected BEFORE the invocation is
        // ever dispatched to the wrapper MCP — a raw-KQL-only tool that can't supply these fields
        // is exactly the case this seam refuses to enable.
        var error = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(),
            cluster: "https://help.kusto.windows.net",
            database: "IncidentDb",
            lookbackHours: 12,
            rowLimit: 500,
            query: null);

        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null, "IncidentDb")]
    [InlineData("https://help.kusto.windows.net", null)]
    [InlineData(null, null)]
    public void Policy_MissingClusterOrDatabase_RejectsBeforeDispatch(string? cluster, string? database)
    {
        var error = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(), cluster, database, lookbackHours: 12, rowLimit: 500, query: "T | take 1");

        Assert.NotNull(error);
    }

    [Fact]
    public void Policy_MissingLookbackOrRowLimit_RejectsBeforeDispatch()
    {
        var missingLookback = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(), "https://help.kusto.windows.net", "IncidentDb", lookbackHours: null, rowLimit: 500, query: "T | take 1");
        var missingRowLimit = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(), "https://help.kusto.windows.net", "IncidentDb", lookbackHours: 12, rowLimit: null, query: "T | take 1");

        Assert.NotNull(missingLookback);
        Assert.NotNull(missingRowLimit);
    }

    [Fact]
    public void Policy_ClusterOrDatabaseOutsideAllowList_Rejects()
    {
        var wrongCluster = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(), "https://attacker.kusto.windows.net", "IncidentDb", 12, 500, "T | take 1");
        var wrongDatabase = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(), "https://help.kusto.windows.net", "OtherDb", 12, 500, "T | take 1");

        Assert.NotNull(wrongCluster);
        Assert.NotNull(wrongDatabase);
    }

    [Fact]
    public void Policy_ExcessiveRowsOrLookback_Rejects()
    {
        var excessiveLookback = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(), "https://help.kusto.windows.net", "IncidentDb", lookbackHours: 999, rowLimit: 500, query: "T | take 1");
        var excessiveRows = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(), "https://help.kusto.windows.net", "IncidentDb", lookbackHours: 12, rowLimit: 999_999, query: "T | take 1");

        Assert.NotNull(excessiveLookback);
        Assert.NotNull(excessiveRows);
    }

    [Fact]
    public void Policy_NonPositiveRowsOrLookback_Rejects()
    {
        var zeroLookback = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(), "https://help.kusto.windows.net", "IncidentDb", lookbackHours: 0, rowLimit: 500, query: "T | take 1");
        var negativeRows = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            ValidBounds(), "https://help.kusto.windows.net", "IncidentDb", lookbackHours: 12, rowLimit: -5, query: "T | take 1");

        Assert.NotNull(zeroLookback);
        Assert.NotNull(negativeRows);
    }

    [Fact]
    public void Policy_MisconfiguredBounds_FailsClosedEvenForAnOtherwiseValidRequest()
    {
        // An unconfigured (empty allow-list) policy must REJECT every request — never be treated
        // as "no restriction configured".
        var unconfigured = new McpKustoReadBoundsConfig();

        var error = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            unconfigured, "any-cluster", "any-db", lookbackHours: 1, rowLimit: 1, query: "T | take 1");

        Assert.NotNull(error);
    }

    [Fact]
    public void Policy_MisconfiguredBoundsWithNonPositiveMax_FailsClosed()
    {
        var badBounds = new McpKustoReadBoundsConfig
        {
            AllowedCluster = "https://help.kusto.windows.net",
            AllowedDatabase = "IncidentDb",
            MaxLookbackHours = 0,
            MaxRowLimit = 0,
        };

        var error = McpInvocationPolicyEvaluator.EvaluateKustoRead(
            badBounds, "https://help.kusto.windows.net", "IncidentDb", lookbackHours: 1, rowLimit: 1, query: "T | take 1");

        Assert.NotNull(error);
    }
}
