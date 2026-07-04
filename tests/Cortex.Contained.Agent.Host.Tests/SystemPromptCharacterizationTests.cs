using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.SystemPrompt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Locks the CURRENT (pre-refactor) system-prompt output of <see cref="PromptAssembler"/>
/// and the Task-7 target output of the subagent template, so the Task 6/7 refactor from
/// hardcoded string concatenation to <see cref="SystemPromptRenderer"/>-based rendering is
/// provably behavior-preserving. Do not weaken these assertions during the refactor — if a
/// change is intentional, update the golden value here in the same commit and explain why.
/// </summary>
public class SystemPromptCharacterizationTests
{
    private static PromptAssembler NewAssembler()
    {
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.ContextWindow.Returns(128_000);
        modelProvider.DefaultModel.Returns("test-model");
        var imageAging = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAging.CurrentValue.Returns(new ImageAgingConfig());

        return new PromptAssembler(
            () => "You are a test.",
            modelProvider,
            imageAging,
            NullLogger<PromptAssembler>.Instance);
    }

    [Fact]
    public async Task MainPrompt_Default_MatchesGolden()
    {
        var session = new AgentSession("conv-1");
        var messages = await NewAssembler().BuildPromptAsync(session, CancellationToken.None);

        var system = messages[0].Content!;

        // With all optional stores null, personality/self-notes/skills-section combine with
        // the always-appended CodingAgentRelayPrompt (now duplicated verbatim as
        // SystemPromptDefaults.CodingRelay by Task 1) and nothing else, since:
        //   - selfNotesStore is null -> selfNotes == ""
        //   - skillRegistry is null -> skillsSection == ""
        //   - channelId is null -> no "## channel" line
        //   - isVoice is false -> no voice-mode block
        //   - subagentStore is null -> no "## Active background tasks"
        //   - todoResolver is null -> no "## Active plans"
        var expected = "You are a test." + "\n\n## Self-notes\n" + SystemPromptDefaults.CodingRelay;

        Assert.Equal(expected, system);
    }

    [Fact]
    public void SubagentPrompt_EmptyValues_MatchesGoldenInstructionsOnly()
    {
        // Locks the Task-7 TARGET output (renderer + defaults), not the live
        // SubAgentStartTool.BuildSubagentSystemPrompt (which uses AppendLine and therefore
        // emits \r\n on Windows but \n in the Linux production container). All segments
        // except {{instructions}} are empty, so the template collapses to just the fixed
        // subagent instructions block.
        var values = new Dictionary<string, string>
        {
            ["personality"] = "",
            ["skill"] = "",
            ["instructions"] = SystemPromptDefaults.SubagentInstructions,
            ["skills"] = "",
            ["bootstrap_context"] = "",
            ["recalled_memories"] = "",
        };

        var result = SystemPromptRenderer.Render(SystemPromptDefaults.SubagentTemplate, values);

        Assert.Equal(SystemPromptDefaults.SubagentInstructions, result);
        Assert.DoesNotContain("\r", result);
    }

    [Fact]
    public void SubagentPrompt_AllValuesPopulated_MatchesGoldenConcatenationInTemplateOrder()
    {
        var values = new Dictionary<string, string>
        {
            ["personality"] = "",
            ["skill"] = "## Skill: demo\n\nbody\n\n",
            ["instructions"] = SystemPromptDefaults.SubagentInstructions,
            ["skills"] = "",
            ["bootstrap_context"] = "\n## User context\nctx",
            ["recalled_memories"] = "\n## Recalled context\nmem",
        };

        var result = SystemPromptRenderer.Render(SystemPromptDefaults.SubagentTemplate, values);

        // Template is "{{personality}}{{skill}}{{instructions}}{{skills}}{{bootstrap_context}}{{recalled_memories}}",
        // so with personality/skills empty the concatenation order is: skill, instructions, bootstrap_context, recalled_memories.
        var expected = values["skill"]
            + values["instructions"]
            + values["bootstrap_context"]
            + values["recalled_memories"];

        Assert.Equal(expected, result);
        Assert.DoesNotContain("\r", result);
    }
}
