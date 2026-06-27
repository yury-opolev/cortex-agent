using System.Text;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class LlmImageDescriberTests
{
    private static byte[] MakeBytes() => Encoding.UTF8.GetBytes("not-a-real-image");

    private static IModelProvider MakeModelProvider(string memoryModel = "gpt-4o-mini")
    {
        var provider = Substitute.For<IModelProvider>();
        provider.MemoryModel.Returns(memoryModel);
        provider.DefaultModel.Returns("gpt-4o");
        return provider;
    }

    [Fact]
    public async Task DescribeAsync_SuccessfulResponse_ReturnsTrimmedDescription()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = true, Content = "  A red sunset over the ocean.  " });

        var describer = new LlmImageDescriber(llm, MakeModelProvider(), NullLogger<LlmImageDescriber>.Instance);

        var result = await describer.DescribeAsync(MakeBytes(), "image/png", CancellationToken.None);

        Assert.Equal("A red sunset over the ocean.", result);
    }

    [Fact]
    public async Task DescribeAsync_UsesMemoryModelAndSmallTokenLimit()
    {
        LlmCompletionRequest? captured = null;
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Do<LlmCompletionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = true, Content = "ok" });

        var describer = new LlmImageDescriber(llm, MakeModelProvider("claude-haiku"), NullLogger<LlmImageDescriber>.Instance);
        await describer.DescribeAsync(MakeBytes(), "image/png", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("claude-haiku", captured!.Model);
        Assert.Equal(TokenLimits.Small, captured.MaxTokens);
        Assert.Equal(0.2, captured.Temperature, 3);
    }

    [Fact]
    public async Task DescribeAsync_ExceptionFromLlm_ReturnsNull()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<LlmCompletionResult>>(_ => throw new InvalidOperationException("provider exploded"));

        var describer = new LlmImageDescriber(llm, MakeModelProvider(), NullLogger<LlmImageDescriber>.Instance);

        var result = await describer.DescribeAsync(MakeBytes(), "image/png", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DescribeAsync_UnsuccessfulResult_ReturnsNull()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = false, ErrorMessage = "no vision support" });

        var describer = new LlmImageDescriber(llm, MakeModelProvider(), NullLogger<LlmImageDescriber>.Instance);

        var result = await describer.DescribeAsync(MakeBytes(), "image/png", CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task DescribeAsync_EmptyOrWhitespaceResponse_ReturnsNull(string? content)
    {
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = true, Content = content });

        var describer = new LlmImageDescriber(llm, MakeModelProvider(), NullLogger<LlmImageDescriber>.Instance);

        var result = await describer.DescribeAsync(MakeBytes(), "image/png", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DescribeAsync_SendsImageContentBlockInUserMessage()
    {
        LlmCompletionRequest? captured = null;
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Do<LlmCompletionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = true, Content = "ok" });

        var describer = new LlmImageDescriber(llm, MakeModelProvider(), NullLogger<LlmImageDescriber>.Instance);
        await describer.DescribeAsync(MakeBytes(), "image/jpeg", CancellationToken.None);

        Assert.NotNull(captured);
        var user = Assert.Single(captured!.Messages, m => m.Role == "user");
        Assert.NotNull(user.ContentBlocks);
        Assert.Contains(user.ContentBlocks!, b => b.Type == "image" && b.ImageMediaType == "image/jpeg");
    }
}
