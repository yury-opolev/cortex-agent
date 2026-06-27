using Cortex.Contained.Bridge.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class AllowAlwaysCacheTests
{
    [Fact]
    public void IsAllowed_after_add_returns_true_for_same_pair()
    {
        var cache = new AllowAlwaysCache();

        cache.Add("session-1", "bash");

        Assert.True(cache.IsAllowed("session-1", "bash"));
    }

    [Fact]
    public void IsAllowed_returns_false_for_different_tool()
    {
        var cache = new AllowAlwaysCache();

        cache.Add("session-1", "bash");

        Assert.False(cache.IsAllowed("session-1", "read_file"));
    }

    [Fact]
    public void IsAllowed_returns_false_for_different_session()
    {
        var cache = new AllowAlwaysCache();

        cache.Add("session-1", "bash");

        Assert.False(cache.IsAllowed("session-2", "bash"));
    }

    [Fact]
    public void IsAllowed_returns_false_before_any_entry()
    {
        var cache = new AllowAlwaysCache();

        Assert.False(cache.IsAllowed("session-1", "bash"));
    }

    [Fact]
    public void ClearSession_removes_all_entries_for_session()
    {
        var cache = new AllowAlwaysCache();

        cache.Add("session-1", "bash");
        cache.Add("session-1", "read_file");
        cache.Add("session-2", "bash");

        cache.ClearSession("session-1");

        Assert.False(cache.IsAllowed("session-1", "bash"));
        Assert.False(cache.IsAllowed("session-1", "read_file"));
        Assert.True(cache.IsAllowed("session-2", "bash"));
    }

    [Fact]
    public void Add_multiple_tools_for_same_session_all_allowed()
    {
        var cache = new AllowAlwaysCache();

        cache.Add("session-1", "bash");
        cache.Add("session-1", "write_file");
        cache.Add("session-1", "read_file");

        Assert.True(cache.IsAllowed("session-1", "bash"));
        Assert.True(cache.IsAllowed("session-1", "write_file"));
        Assert.True(cache.IsAllowed("session-1", "read_file"));
    }
}
