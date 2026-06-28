using Cortex.Contained.Bridge.Mcp;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpToolFilterTests
{
    [Fact]
    public void IsAllowed_EmptyAllowList_AllowsEverything()
    {
        Assert.True(McpToolFilter.IsAllowed("anything", []));
    }

    [Fact]
    public void IsAllowed_NameInList_True()
    {
        Assert.True(McpToolFilter.IsAllowed("create_issue", ["create_issue", "list_prs"]));
    }

    [Fact]
    public void IsAllowed_NameNotInList_False()
    {
        Assert.False(McpToolFilter.IsAllowed("delete_repo", ["create_issue", "list_prs"]));
    }

    [Fact]
    public void IsAllowed_CaseSensitive_False()
    {
        Assert.False(McpToolFilter.IsAllowed("Create_Issue", ["create_issue"]));
    }
}
