using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

public class SetupHelpersTests
{
    // ── GenerateYaml ────────────────────────────────────────────

    [Fact]
    public void GenerateYaml_GitHubCopilotApi_ContainsBaseUrl()
    {
        var request = CreateSetupRequest("github-copilot-api", ["anthropic/claude-sonnet-4"]);

        var yaml = SetupHelpers.GenerateYaml(request);

        Assert.Contains("baseUrl: https://api.githubcopilot.com", yaml);
        Assert.Contains("name: github-copilot-api", yaml);
        Assert.Contains("tokenType: oauth", yaml);
        Assert.Contains("- anthropic/claude-sonnet-4", yaml);
        Assert.DoesNotContain("clientId:", yaml); // default clientId should NOT be emitted
    }

    [Fact]
    public void GenerateYaml_GitHubCopilotApi_WithCustomClientId_EmitsClientId()
    {
        var request = CreateSetupRequest("github-copilot-api", ["gpt-4o"]);
        request.ClientId = "Ov23liCustomAppId1234";

        var yaml = SetupHelpers.GenerateYaml(request);

        Assert.Contains("clientId: Ov23liCustomAppId1234", yaml);
    }

    [Fact]
    public void GenerateYaml_GitHubCopilotApi_WithDefaultClientId_OmitsClientId()
    {
        var request = CreateSetupRequest("github-copilot-api", ["gpt-4o"]);
        request.ClientId = "Ov23li8tweQw6odWQebz"; // the built-in default (opencode)

        var yaml = SetupHelpers.GenerateYaml(request);

        Assert.DoesNotContain("clientId:", yaml);
    }

    [Fact]
    public void GenerateYaml_OpenAi_OmitsBaseUrl()
    {
        var request = CreateSetupRequest("openai", ["gpt-4o", "gpt-4o-mini"]);

        var yaml = SetupHelpers.GenerateYaml(request);

        Assert.DoesNotContain("baseUrl:", yaml);
        Assert.Contains("name: openai", yaml);
        Assert.Contains("- gpt-4o", yaml);
        Assert.Contains("- gpt-4o-mini", yaml);
    }

    [Fact]
    public void GenerateYaml_ContainsStandardSections()
    {
        var request = CreateSetupRequest("openai", ["gpt-4o"]);

        var yaml = SetupHelpers.GenerateYaml(request);

        Assert.Contains("agentHubUrl:", yaml);
        Assert.Contains("webUi:", yaml);
        Assert.Contains("llmProviders:", yaml);
        Assert.Contains("llmProxy:", yaml);
        Assert.Contains("fallbackOrder:", yaml);
    }

    [Fact]
    public void GenerateYaml_FallbackOrderMatchesProvider()
    {
        var request = CreateSetupRequest("anthropic", ["claude-sonnet-4-20250514"]);

        var yaml = SetupHelpers.GenerateYaml(request);

        Assert.Contains("fallbackOrder:", yaml);
        Assert.Contains("- anthropic", yaml);
    }

    // ── ResolveProviderName ─────────────────────────────────────

    [Theory]
    [InlineData("github-copilot-api", "github-copilot-api")]
    [InlineData("openai", "openai")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("OPENAI", "openai")]
    [InlineData("custom-provider", "custom-provider")]
    public void ResolveProviderName_ReturnsExpected(string input, string expected)
    {
        var result = SetupHelpers.ResolveProviderName(input);

        Assert.Equal(expected, result);
    }

    // ── ResolveApi ──────────────────────────────────────────────

    [Theory]
    [InlineData("github-copilot-api", "github-copilot-api")]
    [InlineData("openai", "openai-completions")]
    [InlineData("anthropic", "anthropic-messages")]
    [InlineData("unknown", "openai-completions")]
    public void ResolveApi_ReturnsExpected(string input, string expected)
    {
        var result = SetupHelpers.ResolveApi(input);

        Assert.Equal(expected, result);
    }

    // ── ResolveBaseUrl ──────────────────────────────────────────

    [Fact]
    public void ResolveBaseUrl_GitHubCopilotApi_ReturnsCopilotUrl()
    {
        var result = SetupHelpers.ResolveBaseUrl("github-copilot-api");

        Assert.Equal("https://api.githubcopilot.com", result);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("unknown")]
    public void ResolveBaseUrl_NonCopilot_ReturnsNull(string provider)
    {
        var result = SetupHelpers.ResolveBaseUrl(provider);

        Assert.Null(result);
    }

    // ── ResolveTokenType ────────────────────────────────────────

    [Theory]
    [InlineData("github-copilot-api", "oauth")]
    [InlineData("openai", "bearer")]
    [InlineData("anthropic", "bearer")]
    [InlineData("unknown", "bearer")]
    public void ResolveTokenType_ReturnsExpected(string input, string expected)
    {
        var result = SetupHelpers.ResolveTokenType(input);

        Assert.Equal(expected, result);
    }

    // ── GetProviderTemplates ────────────────────────────────────

    [Fact]
    public void GetProviderTemplates_ReturnsThreeProviders()
    {
        var templates = SetupHelpers.GetProviderTemplates();

        Assert.Equal(3, templates.Count);
    }

    [Fact]
    public void GetProviderTemplates_ContainsGitHubCopilotApi()
    {
        var templates = SetupHelpers.GetProviderTemplates();

        var copilotApi = templates.Find(t => t.Id == "github-copilot-api");
        Assert.NotNull(copilotApi);
        Assert.Equal("GitHub Copilot", copilotApi.Name);
        Assert.Equal("oauth", copilotApi.AuthMethod);
    }

    [Fact]
    public void GetProviderTemplates_AllHaveRequiredFields()
    {
        var templates = SetupHelpers.GetProviderTemplates();

        foreach (var t in templates)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Id));
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.False(string.IsNullOrWhiteSpace(t.ApiKeyLabel));
        }
    }

    // ── IsCopilotChatModel ────────────────────────────────────────

    [Fact]
    public void IsCopilotChatModel_ChatType_ReturnsTrue()
    {
        var entry = CreateCopilotEntry("gpt-4o", type: "chat");

        Assert.True(SetupHelpers.IsCopilotChatModel(entry));
    }

    [Fact]
    public void IsCopilotChatModel_NullCapabilities_ReturnsTrue()
    {
        // When capabilities are null, we default to including the model
        // (only exclude if explicitly non-chat or embedding/image model)
        var entry = new CopilotModelEntry { Id = "gpt-4o", Name = "GPT-4o" };

        Assert.True(SetupHelpers.IsCopilotChatModel(entry));
    }

    [Fact]
    public void IsCopilotChatModel_EmbeddingModel_ReturnsFalse()
    {
        var entry = CreateCopilotEntry("text-embedding-ada-002", type: "chat");

        Assert.False(SetupHelpers.IsCopilotChatModel(entry));
    }

    [Fact]
    public void IsCopilotChatModel_DallEModel_ReturnsFalse()
    {
        var entry = CreateCopilotEntry("dall-e-3", type: "chat");

        Assert.False(SetupHelpers.IsCopilotChatModel(entry));
    }

    [Fact]
    public void IsCopilotChatModel_NonChatType_ReturnsFalse()
    {
        var entry = CreateCopilotEntry("text-embedding-3-large", type: "embeddings");

        Assert.False(SetupHelpers.IsCopilotChatModel(entry));
    }

    [Fact]
    public void IsCopilotChatModel_ClaudeModel_ReturnsTrue()
    {
        var entry = CreateCopilotEntry("claude-sonnet-4", type: "chat");

        Assert.True(SetupHelpers.IsCopilotChatModel(entry));
    }

    // ── FetchAvailableModelsAsync (Anthropic — live API) ──────────

    [Fact]
    public async Task FetchAvailableModelsAsync_Anthropic_WithInvalidKey_Throws()
    {
        using var httpClient = new HttpClient();

        // With a fake key, the Anthropic API should return an error
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            SetupHelpers.FetchAvailableModelsAsync("anthropic", "fake-key", httpClient));
    }

    [Fact]
    public async Task FetchAvailableModelsAsync_UnknownProvider_ReturnsEmpty()
    {
        using var httpClient = new HttpClient();

        var models = await SetupHelpers.FetchAvailableModelsAsync(
            "nonexistent-provider", "fake-key", httpClient);

        Assert.Empty(models);
    }

    // ── DTO Construction ────────────────────────────────────────

    [Fact]
    public void SetupRequest_DefaultValues()
    {
        var request = new SetupRequest();

        Assert.Equal(string.Empty, request.Provider);
        Assert.Equal(string.Empty, request.ApiKey);
        Assert.Null(request.ClientId);
        Assert.Empty(request.Models);
    }

    [Fact]
    public void FetchModelsRequest_DefaultValues()
    {
        var request = new FetchModelsRequest();

        Assert.Equal(string.Empty, request.Provider);
        Assert.Equal(string.Empty, request.ApiKey);
    }

    [Fact]
    public void AvailableModel_DefaultValues()
    {
        var model = new AvailableModel();

        Assert.Equal(string.Empty, model.Id);
        Assert.Equal(string.Empty, model.Name);
        Assert.Equal(string.Empty, model.Publisher);
        Assert.Null(model.Description);
    }

    [Fact]
    public void ProviderTemplate_DefaultValues()
    {
        var template = new ProviderTemplate();

        Assert.Equal(string.Empty, template.Id);
        Assert.Equal(string.Empty, template.Name);
        Assert.Equal(string.Empty, template.Description);
        Assert.Equal("apikey", template.AuthMethod);
        Assert.Equal("API Key", template.ApiKeyLabel);
        Assert.Equal(string.Empty, template.ApiKeyPlaceholder);
    }

    [Fact]
    public void CopilotAuthRequest_DefaultValues()
    {
        var request = new CopilotAuthRequest();

        Assert.Null(request.ClientId);
    }

    [Fact]
    public void CopilotPollRequest_DefaultValues()
    {
        var request = new CopilotPollRequest();

        Assert.Equal(string.Empty, request.DeviceCode);
        Assert.Null(request.ClientId);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static SetupRequest CreateSetupRequest(string provider, List<string> models)
    {
        return new SetupRequest
        {
            Provider = provider,
            ApiKey = "test-key",
            Models = models,
        };
    }

    private static CopilotModelEntry CreateCopilotEntry(string id, string? type = null)
    {
        return new CopilotModelEntry
        {
            Id = id,
            Name = id,
            Capabilities = type is not null
                ? new CopilotModelCapabilities { Type = type }
                : null,
        };
    }

}
