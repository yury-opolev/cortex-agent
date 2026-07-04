using System.Text.Json;
using Cortex.Contained.Contracts.SystemPrompt;

namespace Cortex.Contained.Contracts.Tests;

public class SystemPromptConfigTests
{
    [Fact]
    public void Defaults_Create_PopulatesAllFieldsFromDefaults()
    {
        var config = SystemPromptDefaults.Create();

        Assert.Equal(SystemPromptDefaults.MainTemplate, config.MainTemplate);
        Assert.Equal(SystemPromptDefaults.SubagentTemplate, config.SubagentTemplate);
        Assert.Equal(SystemPromptDefaults.VoiceMode, config.VoiceMode);
        Assert.Equal(SystemPromptDefaults.CodingRelay, config.CodingRelay);
        Assert.Equal(SystemPromptDefaults.SubagentInstructions, config.SubagentInstructions);
    }

    [Fact]
    public void MainTemplate_ContainsAllMainPlaceholders()
    {
        foreach (var name in SystemPromptPlaceholders.Main)
        {
            Assert.Contains("{{" + name + "}}", SystemPromptDefaults.MainTemplate, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SubagentTemplate_ContainsInstructionsPlaceholder()
    {
        Assert.Contains("{{instructions}}", SystemPromptDefaults.SubagentTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialization_UsesCamelCaseNamedProperties_NoItem1()
    {
        var config = SystemPromptDefaults.Create();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var json = JsonSerializer.Serialize(config, options);

        Assert.Contains("\"mainTemplate\"", json, StringComparison.Ordinal);
        Assert.Contains("\"subagentTemplate\"", json, StringComparison.Ordinal);
        Assert.Contains("\"voiceMode\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Item1", json, StringComparison.Ordinal);

        var roundTrip = JsonSerializer.Deserialize<SystemPromptConfig>(json, options)!;
        Assert.Equal(config.CodingRelay, roundTrip.CodingRelay);
    }
}
