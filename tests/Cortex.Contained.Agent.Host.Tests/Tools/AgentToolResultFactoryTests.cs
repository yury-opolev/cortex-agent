using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests.Tools;

public sealed class AgentToolResultFactoryTests
{
    [Fact]
    public void Ok_SetsSuccessAndContent_NoError()
    {
        var r = AgentToolResult.Ok("hello");
        Assert.True(r.Success);
        Assert.Equal("hello", r.Content);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Fail_SetsFailureEmptyContentAndError()
    {
        var r = AgentToolResult.Fail("boom");
        Assert.False(r.Success);
        Assert.Equal(string.Empty, r.Content);
        Assert.Equal("boom", r.Error);
    }
}
