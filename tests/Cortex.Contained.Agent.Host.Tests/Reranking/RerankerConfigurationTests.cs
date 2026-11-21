using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>Reranker settings, the Bridge grant and the exposure guard (§4.3: authentication always required).</summary>
public sealed class RerankerConfigurationTests
{
    private static readonly string[] Sidecar = ["reranker"];

    [Fact]
    public void Defaults_AreValid_AndUseTheInComposeSidecar()
    {
        var options = new RerankerOptions();

        Assert.Empty(options.Validate());
        Assert.Equal("http://reranker:8080", options.Endpoint);
        Assert.Equal(new RerankBudget(15, 1200), options.History.Budget());
    }

    [Fact]
    public void Validate_BudgetOutOfRange_ReportsTheSection()
    {
        var options = new RerankerOptions();
        options.History.TopN = 0;
        options.Memory.MaxDocChars = 50_000;

        var errors = options.Validate();

        Assert.Contains(errors, e => e.StartsWith("Reranker:History:TopN", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Reranker:Memory:MaxDocChars", StringComparison.Ordinal));
    }

    [Fact]
    public void Access_BeforeAnyGrant_IsOff_AndBlankKeyIsNoKey()
    {
        var access = new RerankerAccess();
        Assert.False(access.Enabled);

        access.Apply(new RerankerGrant { Enabled = true, ApiKey = " " });

        Assert.True(access.Enabled);
        Assert.Null(access.ApiKey);
    }

    [Fact]
    public void Guard_SidecarWithKey_Allowed()
    {
        Assert.Null(RerankerEndpointGuard.Check("http://reranker:8080", "k", Sidecar));
    }

    [Fact]
    public void Guard_SidecarWithoutKey_Refused()
    {
        Assert.Contains("API key", RerankerEndpointGuard.Check("http://reranker:8080", null, Sidecar), StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_RemotePlainHttpWithKey_Refused()
    {
        Assert.Contains("HTTPS", RerankerEndpointGuard.Check("http://10.0.0.5:8080", "k", Sidecar), StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_RemoteHttpsWithKey_Allowed()
    {
        Assert.Null(RerankerEndpointGuard.Check("https://rerank.example.net", "k", Sidecar));
    }
}
