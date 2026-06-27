using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class SkillRegistryTests : IDisposable
{
    private readonly string tempDir;

    public SkillRegistryTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"skill-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetSkillsSummary_NoSkillsDirectory_ReturnsEmpty()
    {
        var nonExistentDir = Path.Combine(this.tempDir, "nope");
        var sut = new SkillRegistry(nonExistentDir);

        var summary = sut.GetSkillsSummary();

        Assert.Empty(summary);
    }

    [Fact]
    public void GetSkillsSummary_EmptyDirectory_ReturnsEmpty()
    {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        Directory.CreateDirectory(skillsDir);
        var sut = new SkillRegistry(skillsDir);

        var summary = sut.GetSkillsSummary();

        Assert.Empty(summary);
    }

    [Fact]
    public void GetSkillsSummary_SingleSkill_ReturnsNameAndDescription()
    {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        var researchDir = Path.Combine(skillsDir, "research");
        Directory.CreateDirectory(researchDir);
        File.WriteAllText(Path.Combine(researchDir, "SKILL.md"), """
            ---
            name: research
            description: Systematic multi-source research on any topic
            ---

            ## Workflow
            1. Break the question into sub-questions
            """);
        var sut = new SkillRegistry(skillsDir);

        var summary = sut.GetSkillsSummary();

        Assert.Single(summary);
        Assert.Equal("research", summary[0].Name);
        Assert.Equal("Systematic multi-source research on any topic", summary[0].Description);
        Assert.Contains("research/SKILL.md", summary[0].RelativePath);
    }

    [Fact]
    public void GetSkillsSummary_MultipleSkills_ReturnsAllSorted()
    {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        CreateSkill(skillsDir, "b-second", "Second skill");
        CreateSkill(skillsDir, "a-first", "First skill");
        var sut = new SkillRegistry(skillsDir);

        var summary = sut.GetSkillsSummary();

        Assert.Equal(2, summary.Count);
        Assert.Equal("a-first", summary[0].Name);
        Assert.Equal("b-second", summary[1].Name);
    }

    [Fact]
    public void GetSkillsSummary_MalformedFrontmatter_SkipsSkill()
    {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        var badDir = Path.Combine(skillsDir, "bad-skill");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "SKILL.md"), "No frontmatter here");
        var sut = new SkillRegistry(skillsDir);

        var summary = sut.GetSkillsSummary();

        Assert.Empty(summary);
    }

    [Fact]
    public void GetSkillsSummary_CachesResults_RefreshesAfterInvalidate()
    {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        CreateSkill(skillsDir, "initial", "Initial skill");
        var sut = new SkillRegistry(skillsDir);

        var first = sut.GetSkillsSummary();
        Assert.Single(first);

        CreateSkill(skillsDir, "added", "Added skill");
        var second = sut.GetSkillsSummary();
        Assert.Single(second); // still cached

        sut.Invalidate();
        var third = sut.GetSkillsSummary();
        Assert.Equal(2, third.Count);
    }

    [Fact]
    public void FormatForSystemPrompt_NoSkills_ReturnsCreationInstructions()
    {
        var sut = new SkillRegistry(Path.Combine(this.tempDir, "empty"));

        var prompt = sut.FormatForSystemPrompt();

        Assert.Contains("## Skills", prompt);
        Assert.Contains("SKILL.md", prompt);
        Assert.Contains("No skills exist yet", prompt);
    }

    [Fact]
    public void FormatForSystemPrompt_WithSkills_ReturnsFormattedSection()
    {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        CreateSkill(skillsDir, "research", "Systematic research on any topic");
        var sut = new SkillRegistry(skillsDir);

        var prompt = sut.FormatForSystemPrompt();

        Assert.Contains("## Skills", prompt);
        Assert.Contains("research", prompt);
        Assert.Contains("Systematic research on any topic", prompt);
        Assert.Contains("SKILL.md", prompt);
    }

    [Fact]
    public void ReadSkillContent_ExistingSkill_ReturnsFullContent()
    {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        var content = """
            ---
            name: research
            description: Research skill
            ---

            ## Workflow
            Full content here.
            """;
        var researchDir = Path.Combine(skillsDir, "research");
        Directory.CreateDirectory(researchDir);
        File.WriteAllText(Path.Combine(researchDir, "SKILL.md"), content);
        var sut = new SkillRegistry(skillsDir);

        var result = sut.ReadSkillContent("research");

        Assert.NotNull(result);
        Assert.Contains("Full content here", result);
    }

    [Fact]
    public void ReadSkillContent_NonExistentSkill_ReturnsNull()
    {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        Directory.CreateDirectory(skillsDir);
        var sut = new SkillRegistry(skillsDir);

        var result = sut.ReadSkillContent("nope");

        Assert.Null(result);
    }

    // ── FindMalformedSkills ──────────────────────────────────────────────

    [Fact]
    public void FindMalformedSkills_NoDirectory_ReturnsEmpty()
    {
        var sut = new SkillRegistry(Path.Combine(this.tempDir, "nope"));

        var result = sut.FindMalformedSkills();

        Assert.Empty(result);
    }

    [Fact]
    public void FindMalformedSkills_AllValid_ReturnsEmpty()
    {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        CreateSkill(skillsDir, "valid-one", "A valid skill");
        CreateSkill(skillsDir, "valid-two", "Another valid skill");
        var sut = new SkillRegistry(skillsDir);

        var result = sut.FindMalformedSkills();

        Assert.Empty(result);
    }

    [Fact]
    public void FindMalformedSkills_MissingFrontmatter_ReturnsPath()
    {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        CreateSkill(skillsDir, "good", "A valid skill");

        // Create a malformed skill (markdown header instead of YAML frontmatter)
        var badDir = Path.Combine(skillsDir, "broken");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "SKILL.md"), """
            # Broken Skill
            This has no YAML frontmatter.
            """);

        var sut = new SkillRegistry(skillsDir);

        var result = sut.FindMalformedSkills();

        Assert.Single(result);
        Assert.Equal("skills/broken/SKILL.md", result[0]);
    }

    private static void CreateSkill(string skillsDir, string name, string description)
    {
        var dir = Path.Combine(skillsDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            name: {name}
            description: {description}
            ---

            ## Workflow
            Steps go here.
            """);
    }
}
