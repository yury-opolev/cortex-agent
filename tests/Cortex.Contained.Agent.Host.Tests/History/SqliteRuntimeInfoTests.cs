using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class SqliteRuntimeInfoTests
{
    [Fact]
    public void Capabilities_BundledEngine_SupportsHistoryIndex()
    {
        var capabilities = SqliteRuntimeInfo.Capabilities;

        Assert.True(capabilities.SupportsHistoryIndex, $"SQLite {capabilities.Version}: {capabilities.Error}");
        Assert.True(Version.Parse(capabilities.Version) >= SqliteRuntimeInfo.MinimumHistoryIndexVersion);
    }
}
