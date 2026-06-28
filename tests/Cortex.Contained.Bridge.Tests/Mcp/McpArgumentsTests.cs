using System.Text.Json;
using Cortex.Contained.Bridge.Mcp;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpArgumentsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void Parse_EmptyOrBlank_ReturnsEmpty(string? json)
    {
        Assert.Empty(McpArguments.Parse(json));
    }

    [Fact]
    public void Parse_JsonObject_ExtractsEntries()
    {
        var args = McpArguments.Parse("""{"title":"hi","count":3}""");

        Assert.Equal(2, args.Count);
        Assert.True(args.ContainsKey("title"));
        Assert.True(args.ContainsKey("count"));
        Assert.Equal("hi", ((JsonElement)args["title"]!).GetString());
        Assert.Equal(3, ((JsonElement)args["count"]!).GetInt32());
    }

    [Fact]
    public void Parse_NonObjectRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => McpArguments.Parse("[1,2,3]"));
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => McpArguments.Parse("{not json"));
    }
}
