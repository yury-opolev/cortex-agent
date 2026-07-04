using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

public class SystemPromptRendererTests
{
    [Fact]
    public void Render_SubstitutesKnownPlaceholders()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["personality"] = "You are Cortex.",
            ["self_notes"] = "notes",
        };

        var result = SystemPromptRenderer.Render("{{personality}}\n\n## Self-notes\n{{self_notes}}", values);

        Assert.Equal("You are Cortex.\n\n## Self-notes\nnotes", result);
    }

    [Fact]
    public void Render_EmptyValue_ProducesNoGap()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "X",
            ["channel"] = "",
            ["b"] = "Y",
        };

        var result = SystemPromptRenderer.Render("{{a}}{{channel}}{{b}}", values);

        Assert.Equal("XY", result);
    }

    [Fact]
    public void Render_RepeatedPlaceholder_ReplacesAllOccurrences()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["x"] = "1" };

        Assert.Equal("1-1", SystemPromptRenderer.Render("{{x}}-{{x}}", values));
    }

    [Fact]
    public void Render_UnknownPlaceholder_LeftUntouched()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["x"] = "1" };

        Assert.Equal("1-{{y}}", SystemPromptRenderer.Render("{{x}}-{{y}}", values));
    }
}
