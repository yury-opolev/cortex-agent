using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Setup;

/// <summary>
/// Regression coverage for the 2026-05-20 incident where the LLM-provider save
/// endpoint regenerated cortex.yml from scratch via SetupHelpers.GenerateYaml
/// and silently dropped every other section (tenants, channels, voice, memory).
/// CortexConfigMutator.UpdateLlmProviders MUST preserve everything it doesn't
/// own.
/// </summary>
public class CortexConfigMutatorTests
{
    private const string MultiSectionYaml = """
# Cortex Configuration

agentHubUrl: http://localhost:5100/hub/agent

webUi:
  enabled: true
  port: 5080

llmProviders:
  - name: github-copilot-api
    api: github-copilot-api
    defaultModel: claude-opus-4.6
    models:
      - claude-opus-4.6

llmProxy:
  fallbackOrder:
    - github-copilot-api

channels:
  discord:
    enabled: true
    settings:
      EnableBargeIn: true
      DmVoiceReplyMode: text
  voice:
    enabled: true
    settings:
      PushToTalk: true
      WakeWord: hey Emma

tenants:
  default:
    endpoint: http://localhost:5100/hub/agent
    enabled: true
    discordUserId: "806798098839765047"
    discordGuildId: "1477637110159638568"
    discordVoiceChannelId: "1477637110885384321"

memory:
  duplicateThreshold: 0.9

maxSubagentRounds: 600
""";

    private static LlmProviderConfig NewProvider(string name = "openai") => new()
    {
        Name = name,
        Api = "openai-completions",
        Models = ["gpt-5.5", "gpt-5.4"],
        DefaultModel = "gpt-5.5",
    };

    [Fact]
    public void UpdateLlmProviders_PreservesTenantsSection()
    {
        var updated = CortexConfigMutator.UpdateLlmProviders(
            MultiSectionYaml,
            [NewProvider()],
            ["openai"]);

        Assert.Contains("tenants:", updated, StringComparison.Ordinal);
        Assert.Contains("default:", updated, StringComparison.Ordinal);
        Assert.Contains("1477637110159638568", updated, StringComparison.Ordinal); // discordGuildId
        Assert.Contains("1477637110885384321", updated, StringComparison.Ordinal); // discordVoiceChannelId
        Assert.Contains("806798098839765047", updated, StringComparison.Ordinal);   // discordUserId
    }

    [Fact]
    public void UpdateLlmProviders_PreservesChannelsSection()
    {
        var updated = CortexConfigMutator.UpdateLlmProviders(
            MultiSectionYaml,
            [NewProvider()],
            ["openai"]);

        Assert.Contains("channels:", updated, StringComparison.Ordinal);
        Assert.Contains("discord:", updated, StringComparison.Ordinal);
        Assert.Contains("EnableBargeIn", updated, StringComparison.Ordinal);
        Assert.Contains("DmVoiceReplyMode", updated, StringComparison.Ordinal);
        Assert.Contains("voice:", updated, StringComparison.Ordinal);
        Assert.Contains("PushToTalk", updated, StringComparison.Ordinal);
        Assert.Contains("hey Emma", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateLlmProviders_PreservesMemoryAndMisc()
    {
        var updated = CortexConfigMutator.UpdateLlmProviders(
            MultiSectionYaml,
            [NewProvider()],
            ["openai"]);

        Assert.Contains("memory:", updated, StringComparison.Ordinal);
        Assert.Contains("duplicateThreshold", updated, StringComparison.Ordinal);
        Assert.Contains("maxSubagentRounds:", updated, StringComparison.Ordinal);
        Assert.Contains("600", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateLlmProviders_ReplacesProvidersList()
    {
        var updated = CortexConfigMutator.UpdateLlmProviders(
            MultiSectionYaml,
            [NewProvider("openai"), NewProvider("anthropic")],
            ["openai", "anthropic"]);

        // Old provider gone, new ones present.
        Assert.DoesNotContain("github-copilot-api", updated, StringComparison.Ordinal);
        Assert.Contains("name: openai", updated, StringComparison.Ordinal);
        Assert.Contains("name: anthropic", updated, StringComparison.Ordinal);
        Assert.Contains("gpt-5.5", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateLlmProviders_ReplacesFallbackOrder()
    {
        var updated = CortexConfigMutator.UpdateLlmProviders(
            MultiSectionYaml,
            [NewProvider("openai"), NewProvider("anthropic")],
            ["anthropic", "openai"]);

        var fallbackIdx = updated.IndexOf("fallbackOrder:", StringComparison.Ordinal);
        Assert.True(fallbackIdx > 0);
        var after = updated[fallbackIdx..];
        var antIdx = after.IndexOf("anthropic", StringComparison.Ordinal);
        var oaIdx = after.IndexOf("openai", StringComparison.Ordinal);
        Assert.True(antIdx > 0);
        Assert.True(oaIdx > antIdx, "anthropic should come before openai in fallbackOrder");
    }

    [Fact]
    public void UpdateLlmProviders_RoundTripStaysValidYaml()
    {
        var updated = CortexConfigMutator.UpdateLlmProviders(
            MultiSectionYaml,
            [NewProvider()],
            ["openai"]);

        // Parse again to make sure we didn't produce broken YAML.
        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        using var reader = new StringReader(updated);
        stream.Load(reader);
        Assert.Single(stream.Documents);
    }

    [Fact]
    public void UpdateLlmProviders_OmitsClientIdWhenItEqualsDefaultCopilot()
    {
        // SetupHelpers.GenerateYaml skips emitting clientId when it equals
        // the default GitHub Copilot OAuth client ID (SetupHelpers.cs:341-345).
        // The mutator MUST match that policy so a round-trip after a save
        // doesn't add a clientId line that wasn't there before.
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ClientId = SetupHelpers.DefaultCopilotOAuthClientId,
            Models = ["claude-opus-4.6"],
            DefaultModel = "claude-opus-4.6",
        };

        var updated = CortexConfigMutator.UpdateLlmProviders(
            MultiSectionYaml,
            [provider],
            ["github-copilot-api"]);

        Assert.DoesNotContain("clientId:", updated, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupHelpers.DefaultCopilotOAuthClientId, updated, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateLlmProviders_EmitsClientIdWhenItDiffersFromDefaultCopilot()
    {
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ClientId = "CustomOAuthAppId12345",
            Models = ["claude-opus-4.6"],
            DefaultModel = "claude-opus-4.6",
        };

        var updated = CortexConfigMutator.UpdateLlmProviders(
            MultiSectionYaml,
            [provider],
            ["github-copilot-api"]);

        Assert.Contains("clientId: CustomOAuthAppId12345", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateLlmProviders_NoLlmProxyInInput_CreatesIt()
    {
        const string yamlWithoutProxy = """
agentHubUrl: x
llmProviders:
  - name: old
    api: openai-completions
""";

        var updated = CortexConfigMutator.UpdateLlmProviders(
            yamlWithoutProxy,
            [NewProvider()],
            ["openai"]);

        Assert.Contains("llmProxy:", updated, StringComparison.Ordinal);
        Assert.Contains("fallbackOrder:", updated, StringComparison.Ordinal);
        Assert.Contains("openai", updated, StringComparison.Ordinal);
    }
}
