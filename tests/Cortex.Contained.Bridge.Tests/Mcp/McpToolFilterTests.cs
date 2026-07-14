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

    [Fact]
    public void IsMutation_EmptyList_ClassifiesNothing()
    {
        // Opposite default to IsAllowed: an EMPTY mutation list means NO tool is a mutation.
        // Classification is explicit admin policy — never inferred from a scary-sounding name.
        Assert.False(McpToolFilter.IsMutation("delete_repo", []));
    }

    [Fact]
    public void IsMutation_NameInList_True()
    {
        Assert.True(McpToolFilter.IsMutation("create_issue", ["create_issue", "merge_pr"]));
    }

    [Fact]
    public void IsMutation_NameNotInList_False()
    {
        Assert.False(McpToolFilter.IsMutation("list_prs", ["create_issue", "merge_pr"]));
    }

    [Fact]
    public void IsMutation_CaseSensitive_False()
    {
        Assert.False(McpToolFilter.IsMutation("Create_Issue", ["create_issue"]));
    }
}
