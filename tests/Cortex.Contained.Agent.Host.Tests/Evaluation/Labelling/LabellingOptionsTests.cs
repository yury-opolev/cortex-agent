using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class LabellingOptionsTests
{
    [Theory]
    [InlineData("gpt-6.1-sol", "gpt")]
    [InlineData("claude-opus-5.5", "claude")]
    [InlineData("o4-mini", "gpt")]
    [InlineData("vendor/Gemini-3-pro", "gemini")]
    [InlineData("acme-large-2", "acme")]
    public void Of_ModelId_ReturnsFamily(string model, string family) => Assert.Equal(family, ModelFamily.Of(model));

    [Fact]
    public void Validate_Defaults_AreUsable()
    {
        var options = new LabellingOptions();

        Assert.Empty(options.Validate());
        Assert.Equal("gpt-6.1-sol", options.QueryModel);
        Assert.Equal("claude-opus-5.5", options.JudgeModel);
        Assert.Equal(75, options.KappaSample);
    }

    [Fact]
    public void Validate_SameFamily_Refused()
    {
        var options = new LabellingOptions { QueryModel = "claude-sonnet-5", JudgeModel = "claude-opus-5.5" };

        Assert.Contains(options.Validate(), e => e.Contains("claude family", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderFor_ListedModel_NamesItsProviderAndUnknownIsNull()
    {
        using var client = new DirectLlmClient(Substitute.For<IHttpClientFactory>(), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(new LlmCredentials
        {
            Providers =
            [
                new LlmProviderCredential
                {
                    Name = "copilot", Api = "github-copilot-api", BaseUrl = "https://api.example.test", Kind = CredentialKind.ApiKey,
                    ApiKey = "test-key", Models = ["gpt-6.1-sol", "claude-opus-5.5"], DefaultModel = "gpt-6.1-sol",
                },
            ],
        });

        Assert.Equal("copilot", client.ProviderFor("claude-opus-5.5"));
        Assert.Null(client.ProviderFor("gemini-3-pro"));
    }
}
