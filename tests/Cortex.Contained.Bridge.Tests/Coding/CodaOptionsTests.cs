using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodaOptionsTests
{
    [Fact]
    public void Defaults_AreModest_AndStartTimeoutBelowAgentInvokeCeiling()
    {
        var coda = new CodaOptions();

        Assert.Equal(30, coda.StartTimeoutSeconds);
        Assert.Equal(15, coda.ControlTimeoutSeconds);
        Assert.Equal(300, coda.PromptIdleTimeoutSeconds);

        // Invariant: the Bridge's start timeout must stay strictly below the Agent→Bridge invoke
        // ceiling (45s, asserted in CodingAgentOptionsTests) so the Bridge's specific failure is
        // delivered before the agent's blunt timeout.
        const int agentInvokeCeiling = 45;
        Assert.True(coda.StartTimeoutSeconds < agentInvokeCeiling,
            $"StartTimeoutSeconds ({coda.StartTimeoutSeconds}) must be < BridgeInvokeTimeoutSeconds ({agentInvokeCeiling}).");
    }

    [Fact]
    public void Defaults_Mcp_IsHost_AndNoCuratedDir()
    {
        var coda = new CodaOptions();

        // Default = parity with today: coda serve uses the host's ~/.coda/.mcp.json.
        Assert.Equal(CodaMcpPolicy.Host, coda.Mcp);
        Assert.Null(coda.CuratedMcpDir);
    }

    [Fact]
    public void Defaults_Source_IsAuto()
    {
        // Default = parity with today's implicit bundled-if-present-else-host resolution.
        Assert.Equal(CodaSource.Auto, new CodaOptions().Source);
    }

    [Fact]
    public void Clone_CopiesAllFields_IncludingMcpPolicyAndSource()
    {
        var src = new CodaOptions
        {
            CodaBinaryPath = "x/coda.exe",
            MaxSessions = 7,
            IdleHours = 9,
            StartTimeoutSeconds = 11,
            ControlTimeoutSeconds = 12,
            PromptIdleTimeoutSeconds = 13,
            Mcp = CodaMcpPolicy.Curated,
            CuratedMcpDir = "C:\\curated",
            Source = CodaSource.Bundled,
        };

        var copy = src.Clone();

        Assert.NotSame(src, copy);
        Assert.Equal("x/coda.exe", copy.CodaBinaryPath);
        Assert.Equal(7, copy.MaxSessions);
        Assert.Equal(9, copy.IdleHours);
        Assert.Equal(11, copy.StartTimeoutSeconds);
        Assert.Equal(12, copy.ControlTimeoutSeconds);
        Assert.Equal(13, copy.PromptIdleTimeoutSeconds);
        Assert.Equal(CodaMcpPolicy.Curated, copy.Mcp);
        Assert.Equal("C:\\curated", copy.CuratedMcpDir);
        Assert.Equal(CodaSource.Bundled, copy.Source);
    }
}
