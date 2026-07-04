using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.SystemPrompt;

namespace Cortex.Contained.Agent.Host.Tests;

public class SystemPromptValidatorTests
{
    [Fact]
    public void Validate_Defaults_IsValidNoErrors()
    {
        var result = SystemPromptValidator.Validate(SystemPromptDefaults.Create());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_UnknownPlaceholder_IsError()
    {
        var config = SystemPromptDefaults.Create();
        config.MainTemplate = "{{personality}} {{bogus_thing}}";

        var result = SystemPromptValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("bogus_thing", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_OverCap_IsError()
    {
        var config = SystemPromptDefaults.Create();
        config.CodingRelay = new string('x', SystemPromptPlaceholders.SegmentMaxChars + 1);

        var result = SystemPromptValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("CodingRelay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_MissingRecommended_IsWarningNotError()
    {
        var config = SystemPromptDefaults.Create();
        config.MainTemplate = "{{personality}} {{self_notes}} {{skills}}"; // no coding_relay

        var result = SystemPromptValidator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("coding_relay", StringComparison.Ordinal));
    }
}
