using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class RevertTransferToolTests
{
    private static readonly ToolExecutionContext Context = new()
    {
        ConversationId = "voice-default",
        ChannelId = "voice-default",
    };

    private readonly IAgentRuntime agentRuntime;
    private readonly RevertTransferTool tool;

    public RevertTransferToolTests()
    {
        this.agentRuntime = Substitute.For<IAgentRuntime>();
        this.tool = new RevertTransferTool(
            () => this.agentRuntime,
            NullLogger<RevertTransferTool>.Instance);
    }

    [Fact]
    public void Name_IsRevertTransfer()
    {
        Assert.Equal("revert_transfer", this.tool.Name);
    }

    [Fact]
    public void ParametersSchema_IsValidJson()
    {
        var doc = System.Text.Json.JsonDocument.Parse(this.tool.ParametersSchema);
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        // 'channel' is optional — no required array entry needed.
    }

    [Fact]
    public async Task ExecuteAsync_NoChannelArg_UsesContextChannel()
    {
        this.agentRuntime
            .RevertTransferAsync("voice-default", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await this.tool.ExecuteAsync("""{}""", Context, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        await this.agentRuntime.Received(1).RevertTransferAsync("voice-default", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_FriendlyChannelName_Resolves()
    {
        this.agentRuntime
            .RevertTransferAsync("webchat-default", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await this.tool.ExecuteAsync(
            """{"channel":"webchat"}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        await this.agentRuntime.Received(1).RevertTransferAsync("webchat-default", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeReturnsFalse_NoSnapshotError()
    {
        this.agentRuntime
            .RevertTransferAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await this.tool.ExecuteAsync("""{}""", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No recent transfer snapshot", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsError()
    {
        var result = await this.tool.ExecuteAsync("not json", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Invalid JSON", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Success_ContentMentionsChannel()
    {
        this.agentRuntime
            .RevertTransferAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await this.tool.ExecuteAsync(
            """{"channel":"voice-default"}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("voice-default", result.Content);
        Assert.Contains("pre-transfer", result.Content);
    }
}
