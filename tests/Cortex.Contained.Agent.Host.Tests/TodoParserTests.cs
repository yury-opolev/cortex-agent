using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

public class TodoParserTests
{
    [Fact]
    public void Parse_StandardCheckboxes_ParsesAllStatuses()
    {
        var markdown = """
            - [ ] Pending item
            - [-] In progress item
            - [x] Completed item
            - [~] Skipped item
            """;

        var items = TodoParser.Parse(markdown);

        Assert.Equal(4, items.Count);
        Assert.Equal(TodoStatus.Pending, items[0].Status);
        Assert.Equal("Pending item", items[0].Description);
        Assert.Equal(TodoStatus.InProgress, items[1].Status);
        Assert.Equal(TodoStatus.Completed, items[2].Status);
        Assert.Equal(TodoStatus.Skipped, items[3].Status);
    }

    [Fact]
    public void Parse_UppercaseX_ParsesAsCompleted()
    {
        var items = TodoParser.Parse("- [X] Done task");

        Assert.Single(items);
        Assert.Equal(TodoStatus.Completed, items[0].Status);
    }

    [Fact]
    public void Parse_MixedContent_IgnoresNonCheckboxLines()
    {
        var markdown = """
            Some preamble text
            - [ ] First task
            This is a comment
            - [x] Second task

            Another paragraph
            """;

        var items = TodoParser.Parse(markdown);

        Assert.Equal(2, items.Count);
        Assert.Equal("First task", items[0].Description);
        Assert.Equal("Second task", items[1].Description);
    }

    [Fact]
    public void Parse_AsteriskAndPlusMarkers_Supported()
    {
        var markdown = """
            * [ ] Asterisk item
            + [x] Plus item
            - [-] Dash item
            """;

        var items = TodoParser.Parse(markdown);

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void Parse_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(TodoParser.Parse(""));
        Assert.Empty(TodoParser.Parse(null!));
        Assert.Empty(TodoParser.Parse("   "));
    }

    [Fact]
    public void Parse_NoCheckboxes_ReturnsEmpty()
    {
        var items = TodoParser.Parse("Just a plain text paragraph.");

        Assert.Empty(items);
    }

    [Fact]
    public void Parse_LeadingWhitespace_Handled()
    {
        var items = TodoParser.Parse("    - [ ] Indented task");

        Assert.Single(items);
        Assert.Equal("Indented task", items[0].Description);
    }

    [Fact]
    public void Parse_DescriptionWithSpecialChars_Preserved()
    {
        var items = TodoParser.Parse("- [ ] Research → build tool (sa-7f3a)");

        Assert.Single(items);
        Assert.Equal("Research → build tool (sa-7f3a)", items[0].Description);
    }

    [Fact]
    public void Summarize_MixedStatuses_CountsCorrectly()
    {
        var items = new List<TodoItem>
        {
            new() { Description = "A", Status = TodoStatus.Completed },
            new() { Description = "B", Status = TodoStatus.InProgress },
            new() { Description = "C", Status = TodoStatus.Pending },
            new() { Description = "D", Status = TodoStatus.Skipped },
            new() { Description = "E", Status = TodoStatus.Completed },
        };

        var summary = TodoParser.Summarize("plan", items);

        Assert.Equal("plan", summary.Name);
        Assert.Equal(5, summary.TotalCount);
        Assert.Equal(3, summary.DoneCount); // 2 completed + 1 skipped
    }

    [Fact]
    public void FormatSummary_ProducesExpectedString()
    {
        var summary = new TodoSummary { Name = "API migration", TotalCount = 3, DoneCount = 1 };

        var formatted = TodoParser.FormatSummary(summary);

        Assert.Equal("- \"API migration\" (1/3 done)", formatted);
    }
}
