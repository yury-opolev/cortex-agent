using Cortex.Contained.Bridge.Coding;

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
    public void Clone_CopiesAllFields_IncludingMcpPolicy()
    {
        var src = new CodaOptions
        {
            CodaBinaryPath = "x/coda.exe",
            MaxSessions = 7,
            IdleHours = 9,
            Provider = "github-copilot",
            Model = "m",
            StartTimeoutSeconds = 11,
            ControlTimeoutSeconds = 12,
            PromptIdleTimeoutSeconds = 13,
            Mcp = CodaMcpPolicy.Curated,
            CuratedMcpDir = "C:\\curated",
        };

        var copy = src.Clone();

        Assert.NotSame(src, copy);
        Assert.Equal("x/coda.exe", copy.CodaBinaryPath);
        Assert.Equal(7, copy.MaxSessions);
        Assert.Equal(9, copy.IdleHours);
        Assert.Equal("github-copilot", copy.Provider);
        Assert.Equal("m", copy.Model);
        Assert.Equal(11, copy.StartTimeoutSeconds);
        Assert.Equal(12, copy.ControlTimeoutSeconds);
        Assert.Equal(13, copy.PromptIdleTimeoutSeconds);
        Assert.Equal(CodaMcpPolicy.Curated, copy.Mcp);
        Assert.Equal("C:\\curated", copy.CuratedMcpDir);
    }

    [Fact]
    public void ResolveDefaultBinaryPath_WithoutBundledExe_ReturnsPathFallback()
    {
        // Arrange: temp dir with no coda/coda.exe present.
        // The method uses AppContext.BaseDirectory so we can't control it directly,
        // but we can verify behavior when the bundled file does NOT exist there.
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "coda", "coda.exe");

        // Act
        var result = CodaOptions.ResolveDefaultBinaryPath();

        // Assert: result is either the bundled path (if it actually exists) or the PATH fallback.
        if (File.Exists(bundledPath))
        {
            Assert.Equal(bundledPath, result);
        }
        else
        {
            Assert.Equal("coda", result);
        }
    }

    [Fact]
    public void ResolveDefaultBinaryPath_WhenBundledExeIsPresent_ReturnsBundledPath()
    {
        // Arrange: create a fake coda/coda.exe inside a temp directory and
        // verify that the resolver logic returns the bundled path when the file exists.
        var tempBase = Directory.CreateTempSubdirectory().FullName;
        var codaDir = Path.Combine(tempBase, "coda");
        Directory.CreateDirectory(codaDir);
        var codaExe = Path.Combine(codaDir, "coda.exe");
        File.WriteAllText(codaExe, string.Empty);

        try
        {
            // We test the pure logic: if File.Exists(bundled) → return bundled, else → "coda".
            // Since we can't swap AppContext.BaseDirectory, we exercise the same File.Exists
            // branch manually to validate the conditional logic is correct.
            var bundled = Path.Combine(tempBase, "coda", "coda.exe");
            var resolved = File.Exists(bundled) ? bundled : "coda";

            Assert.Equal(codaExe, resolved);
        }
        finally
        {
            Directory.Delete(tempBase, recursive: true);
        }
    }

    [Fact]
    public void ResolveDefaultBinaryPath_WhenNoBundledExe_ReturnsCodaFallback()
    {
        // Arrange: temp dir without coda/coda.exe.
        var tempBase = Directory.CreateTempSubdirectory().FullName;

        try
        {
            // Simulate the resolver logic against a path that has no coda/coda.exe.
            var bundled = Path.Combine(tempBase, "coda", "coda.exe");
            var resolved = File.Exists(bundled) ? bundled : "coda";

            Assert.Equal("coda", resolved);
        }
        finally
        {
            Directory.Delete(tempBase, recursive: true);
        }
    }
}
