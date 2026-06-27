using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;
using Cortex.Contained.Contracts.Coding;
using NSubstitute;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public sealed class CodingFoldersListToolTests
{
    [Fact]
    public async Task Lists_allowed_folders_from_agent()
    {
        var agent = Substitute.For<ICodingAgent>();
        agent.ListAllowedFoldersAsync(Arg.Any<CancellationToken>())
            .Returns(new CodingFolderList
            {
                Folders =
                [
                    new CodingFolderInfo { AbsolutePath = "C:\\repos\\cortex", Label = "cortex" },
                ],
            });

        var tool = new CodingFoldersListTool(agent);
        var result = await tool.ExecuteAsync("{}", new ToolExecutionContext { ChannelId = "c1", ConversationId = "c1" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("cortex", result.Content);
    }
}
