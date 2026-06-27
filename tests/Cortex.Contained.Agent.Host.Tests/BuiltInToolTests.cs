using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;

namespace Cortex.Contained.Agent.Host.Tests;

public class BuiltInToolTests : IDisposable
{
    private static readonly ToolExecutionContext _context = new()
    {
        ConversationId = "conv-test",
        ChannelId = "webchat-default",
    };

    private readonly string _sandbox;

    public BuiltInToolTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "tool_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private static string Args(object obj)
    {
        return JsonSerializer.Serialize(obj);
    }

    // ===================== FileReadTool =====================

    [Fact]
    public async Task FileRead_ExistingFile_ReturnsContent()
    {
        File.WriteAllText(Path.Combine(_sandbox, "hello.txt"), "Hello World");
        var tool = new FileReadTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "hello.txt" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Hello World", result.Content);
    }

    [Fact]
    public async Task FileRead_NonExistentFile_ReturnsError()
    {
        var tool = new FileReadTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "missing.txt" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("File not found", result.Error);
    }

    [Fact]
    public async Task FileRead_MissingPathParam_ReturnsError()
    {
        var tool = new FileReadTool(_sandbox);

        var result = await tool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter", result.Error);
    }

    [Fact]
    public async Task FileRead_WithOffsetAndLimit_ReturnsSlice()
    {
        File.WriteAllText(Path.Combine(_sandbox, "lines.txt"), "line0\nline1\nline2\nline3\nline4");
        var tool = new FileReadTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "lines.txt", offset = 1, limit = 2 }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("line1", result.Content);
        Assert.Contains("line2", result.Content);
        Assert.DoesNotContain("line0", result.Content);
        Assert.DoesNotContain("line3", result.Content);
    }

    [Fact]
    public async Task FileRead_PathEscape_ReturnsError()
    {
        var tool = new FileReadTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "../../../etc/passwd" }), _context, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public void FileRead_HasCorrectName()
    {
        var tool = new FileReadTool(_sandbox);
        Assert.Equal("file_read", tool.Name);
    }

    // ===================== FileWriteTool =====================

    [Fact]
    public async Task FileWrite_CreatesFile()
    {
        var tool = new FileWriteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "new.txt", content = "Hello" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_sandbox, "new.txt")));
        Assert.Equal("Hello", File.ReadAllText(Path.Combine(_sandbox, "new.txt")));
    }

    [Fact]
    public async Task FileWrite_AutoCreatesDirectories()
    {
        var tool = new FileWriteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "sub/dir/file.txt", content = "nested" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_sandbox, "sub", "dir", "file.txt")));
    }

    [Fact]
    public async Task FileWrite_OverwritesExistingFile()
    {
        File.WriteAllText(Path.Combine(_sandbox, "existing.txt"), "old");
        var tool = new FileWriteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "existing.txt", content = "new" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_sandbox, "existing.txt")));
    }

    [Fact]
    public async Task FileWrite_MissingPath_ReturnsError()
    {
        var tool = new FileWriteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { content = "hello" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter: path", result.Error);
    }

    [Fact]
    public async Task FileWrite_MissingContent_ReturnsError()
    {
        var tool = new FileWriteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "file.txt" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter: content", result.Error);
    }

    [Fact]
    public async Task FileWrite_PathEscape_ReturnsError()
    {
        var tool = new FileWriteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "../../escape.txt", content = "bad" }), _context, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public void FileWrite_HasCorrectName()
    {
        var tool = new FileWriteTool(_sandbox);
        Assert.Equal("file_write", tool.Name);
    }

    [Fact]
    public async Task FileWrite_SkillPath_InvalidatesRegistry()
    {
        var registry = new SkillRegistry(Path.Combine(_sandbox, "skills"));
        var tool = new FileWriteTool(_sandbox, registry);

        // Pre-populate cache
        registry.GetSkillsSummary();

        var skillContent = "---\nname: test\ndescription: A test skill\n---\n## Workflow\nDo stuff.";
        await tool.ExecuteAsync(Args(new { path = "skills/test/SKILL.md", content = skillContent }), _context, CancellationToken.None);

        // After invalidation, re-scanning should find the new skill
        var summary = registry.GetSkillsSummary();
        Assert.Single(summary);
        Assert.Equal("test", summary[0].Name);
    }

    [Fact]
    public async Task FileWrite_NonSkillPath_DoesNotInvalidateRegistry()
    {
        var skillsDir = Path.Combine(_sandbox, "skills");
        var skillDir = Path.Combine(skillsDir, "existing");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: existing\ndescription: X\n---\nContent.");

        var registry = new SkillRegistry(skillsDir);
        // Load cache with the existing skill
        var beforeSummary = registry.GetSkillsSummary();
        Assert.Single(beforeSummary);

        // Delete the skill file on disk, then write to a non-skill path
        File.Delete(Path.Combine(skillDir, "SKILL.md"));

        var tool = new FileWriteTool(_sandbox, registry);
        await tool.ExecuteAsync(Args(new { path = "memos/note.md", content = "hello" }), _context, CancellationToken.None);

        // Cache should NOT be invalidated — still shows the deleted skill
        var afterSummary = registry.GetSkillsSummary();
        Assert.Single(afterSummary);
    }

    [Fact]
    public async Task FileWrite_NullRegistry_NoException()
    {
        var tool = new FileWriteTool(_sandbox, null);

        var result = await tool.ExecuteAsync(Args(new { path = "skills/test/SKILL.md", content = "content" }), _context, CancellationToken.None);

        Assert.True(result.Success);
    }

    // ===================== FileEditTool =====================

    [Fact]
    public async Task FileEdit_ReplacesText()
    {
        File.WriteAllText(Path.Combine(_sandbox, "edit.txt"), "Hello World");
        var tool = new FileEditTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "edit.txt", old_text = "World", new_text = "Earth" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Hello Earth", File.ReadAllText(Path.Combine(_sandbox, "edit.txt")));
    }

    [Fact]
    public async Task FileEdit_ReplaceAll_ReplacesAllOccurrences()
    {
        File.WriteAllText(Path.Combine(_sandbox, "multi.txt"), "foo bar foo baz foo");
        var tool = new FileEditTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "multi.txt", old_text = "foo", new_text = "qux", replace_all = true }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("qux bar qux baz qux", File.ReadAllText(Path.Combine(_sandbox, "multi.txt")));
        Assert.Contains("3 occurrence(s)", result.Content);
    }

    [Fact]
    public async Task FileEdit_DefaultReplacesFirstOnly()
    {
        File.WriteAllText(Path.Combine(_sandbox, "first.txt"), "aaa bbb aaa");
        var tool = new FileEditTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "first.txt", old_text = "aaa", new_text = "zzz" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("zzz bbb aaa", File.ReadAllText(Path.Combine(_sandbox, "first.txt")));
    }

    [Fact]
    public async Task FileEdit_TextNotFound_ReturnsError()
    {
        File.WriteAllText(Path.Combine(_sandbox, "nofind.txt"), "Hello");
        var tool = new FileEditTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "nofind.txt", old_text = "missing", new_text = "new" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("old_text not found", result.Error);
    }

    [Fact]
    public async Task FileEdit_FileNotFound_ReturnsError()
    {
        var tool = new FileEditTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "nope.txt", old_text = "a", new_text = "b" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("File not found", result.Error);
    }

    [Fact]
    public async Task FileEdit_EmptyOldText_ReturnsError()
    {
        File.WriteAllText(Path.Combine(_sandbox, "empty.txt"), "content");
        var tool = new FileEditTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "empty.txt", old_text = "", new_text = "x" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("old_text cannot be empty", result.Error);
    }

    [Fact]
    public void FileEdit_HasCorrectName()
    {
        var tool = new FileEditTool(_sandbox);
        Assert.Equal("file_edit", tool.Name);
    }

    // ===================== FileListTool =====================

    [Fact]
    public async Task FileList_EmptyDirectory_ReturnsEmpty()
    {
        var tool = new FileListTool(_sandbox);

        var result = await tool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("empty directory", result.Content);
    }

    [Fact]
    public async Task FileList_DirectoryWithFiles_ListsThem()
    {
        File.WriteAllText(Path.Combine(_sandbox, "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(_sandbox, "b.txt"), "bbb");
        var tool = new FileListTool(_sandbox);

        var result = await tool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("a.txt", result.Content);
        Assert.Contains("b.txt", result.Content);
    }

    [Fact]
    public async Task FileList_SubDirectory_ShowsTrailingSlash()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox, "subdir"));
        var tool = new FileListTool(_sandbox);

        var result = await tool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("subdir/", result.Content);
    }

    [Fact]
    public async Task FileList_Recursive_ShowsNestedFiles()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox, "nested"));
        File.WriteAllText(Path.Combine(_sandbox, "nested", "deep.txt"), "deep");
        var tool = new FileListTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { recursive = true }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("nested/deep.txt", result.Content);
    }

    [Fact]
    public async Task FileList_NonExistentDirectory_ReturnsError()
    {
        var tool = new FileListTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "nope" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Directory not found", result.Error);
    }

    [Fact]
    public void FileList_HasCorrectName()
    {
        var tool = new FileListTool(_sandbox);
        Assert.Equal("file_list", tool.Name);
    }

    // ===================== FileDeleteTool =====================

    [Fact]
    public async Task FileDelete_ExistingFile_DeletesIt()
    {
        var filePath = Path.Combine(_sandbox, "todelete.txt");
        File.WriteAllText(filePath, "bye");
        var tool = new FileDeleteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "todelete.txt" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task FileDelete_EmptyDirectory_DeletesIt()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox, "emptydir"));
        var tool = new FileDeleteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "emptydir" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(Path.Combine(_sandbox, "emptydir")));
    }

    [Fact]
    public async Task FileDelete_NonEmptyDirectory_ReturnsError()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox, "notempty"));
        File.WriteAllText(Path.Combine(_sandbox, "notempty", "file.txt"), "x");
        var tool = new FileDeleteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "notempty" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not empty", result.Error);
    }

    [Fact]
    public async Task FileDelete_NonExistent_ReturnsError()
    {
        var tool = new FileDeleteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "ghost.txt" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Not found", result.Error);
    }

    [Fact]
    public async Task FileDelete_CannotDeleteRoot_ReturnsError()
    {
        var tool = new FileDeleteTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { path = "." }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Cannot delete the root", result.Error);
    }

    [Fact]
    public void FileDelete_HasCorrectName()
    {
        var tool = new FileDeleteTool(_sandbox);
        Assert.Equal("file_delete", tool.Name);
    }

    // ===================== FileFindTool =====================

    [Fact]
    public async Task FileFind_MatchingPattern_ReturnsFiles()
    {
        File.WriteAllText(Path.Combine(_sandbox, "readme.md"), "# readme");
        File.WriteAllText(Path.Combine(_sandbox, "notes.md"), "notes");
        File.WriteAllText(Path.Combine(_sandbox, "script.py"), "print");
        var tool = new FileFindTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { pattern = "*.md" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("readme.md", result.Content);
        Assert.Contains("notes.md", result.Content);
        Assert.DoesNotContain("script.py", result.Content);
    }

    [Fact]
    public async Task FileFind_NoMatches_ReturnsMessage()
    {
        var tool = new FileFindTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { pattern = "*.xyz" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("No files matching", result.Content);
    }

    [Fact]
    public async Task FileFind_MissingPattern_ReturnsError()
    {
        var tool = new FileFindTool(_sandbox);

        var result = await tool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter: pattern", result.Error);
    }

    [Fact]
    public async Task FileFind_EmptyPattern_ReturnsError()
    {
        var tool = new FileFindTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { pattern = "  " }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Pattern cannot be empty", result.Error);
    }

    [Fact]
    public async Task FileFind_RecursiveGlob_FindsNested()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox, "sub"));
        File.WriteAllText(Path.Combine(_sandbox, "sub", "deep.md"), "deep");
        var tool = new FileFindTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { pattern = "**/*.md" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("deep.md", result.Content);
    }

    [Fact]
    public void FileFind_HasCorrectName()
    {
        var tool = new FileFindTool(_sandbox);
        Assert.Equal("file_find", tool.Name);
    }

    // ===================== RunCommandTool =====================

    [Fact]
    public async Task RunCommand_SimpleEcho_ReturnsOutput()
    {
        var tool = new RunCommandTool(_sandbox);
        var command = OperatingSystem.IsWindows() ? "echo hello" : "echo hello";

        var result = await tool.ExecuteAsync(Args(new { command }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hello", result.Content);
        Assert.Contains("exit code: 0", result.Content);
    }

    [Fact]
    public async Task RunCommand_FailingCommand_ReturnsErrorExitCode()
    {
        var tool = new RunCommandTool(_sandbox);
        var command = OperatingSystem.IsWindows() ? "cmd /c exit 1" : "exit 1";

        var result = await tool.ExecuteAsync(Args(new { command }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("exit code: 1", result.Content);
    }

    [Fact]
    public async Task RunCommand_MissingCommand_ReturnsError()
    {
        var tool = new RunCommandTool(_sandbox);

        var result = await tool.ExecuteAsync("{}", _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter: command", result.Error);
    }

    [Fact]
    public async Task RunCommand_EmptyCommand_ReturnsError()
    {
        var tool = new RunCommandTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { command = "  " }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Command cannot be empty", result.Error);
    }

    [Fact]
    public async Task RunCommand_Timeout_ReturnsTimeoutError()
    {
        var tool = new RunCommandTool(_sandbox);
        var command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30";

        var result = await tool.ExecuteAsync(Args(new { command, timeout = 1 }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Error);
    }

    [Fact]
    public async Task RunCommand_InvalidWorkdir_ReturnsError()
    {
        var tool = new RunCommandTool(_sandbox);

        var result = await tool.ExecuteAsync(Args(new { command = "echo hi", workdir = "nonexistent" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Working directory not found", result.Error);
    }

    [Fact]
    public async Task RunCommand_Stderr_CapturedSeparately()
    {
        var tool = new RunCommandTool(_sandbox);
        var command = OperatingSystem.IsWindows()
            ? "echo errormsg 1>&2"
            : "echo errormsg >&2";

        var result = await tool.ExecuteAsync(Args(new { command }), _context, CancellationToken.None);

        // The command itself succeeds (exit 0) even though it wrote to stderr
        Assert.True(result.Success);
        Assert.Contains("stderr", result.Content);
        Assert.Contains("errormsg", result.Content);
    }

    [Fact]
    public void RunCommand_HasCorrectName()
    {
        var tool = new RunCommandTool(_sandbox);
        Assert.Equal("run_command", tool.Name);
    }

    // ===================== All tools have descriptions and schemas =====================

    [Fact]
    public void AllTools_HaveNonEmptyDescriptionAndSchema()
    {
        IAgentTool[] tools =
        [
            new FileReadTool(_sandbox),
            new FileWriteTool(_sandbox),
            new FileEditTool(_sandbox),
            new FileListTool(_sandbox),
            new FileDeleteTool(_sandbox),
            new FileFindTool(_sandbox),
            new RunCommandTool(_sandbox),
            new DateTimeTool(),
        ];

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name), $"Tool name is empty");
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} description is empty");
            Assert.False(string.IsNullOrWhiteSpace(tool.ParametersSchema), $"{tool.Name} schema is empty");

            // Schema should be valid JSON
            var doc = JsonDocument.Parse(tool.ParametersSchema);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
    }

    // ===================== DateTimeTool =====================

    [Fact]
    public void DateTime_HasCorrectName()
    {
        var tool = new DateTimeTool();
        Assert.Equal("date_time", tool.Name);
    }

    [Fact]
    public async Task DateTime_Now_ReturnsCurrentUtcTime()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(Args(new { action = "now" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("UTC:", result.Content);
        // Should contain a valid year
        Assert.Contains(DateTimeOffset.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Content);
    }

    [Fact]
    public async Task DateTime_Now_WithTimezone_ReturnsBothUtcAndLocal()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(Args(new { action = "now", timezone = "UTC" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("UTC:", result.Content);
    }

    [Fact]
    public async Task DateTime_Now_InvalidTimezone_ReturnsError()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(Args(new { action = "now", timezone = "Invalid/Zone" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown timezone", result.Error);
    }

    [Fact]
    public async Task DateTime_Convert_ValidConversion_ReturnsResult()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(
            Args(new { action = "convert", datetime = "2025-06-15T12:00:00Z", timezone = "UTC" }),
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("2025-06-15", result.Content);
        Assert.Contains("12:00:00", result.Content);
    }

    [Fact]
    public async Task DateTime_Convert_MissingDatetime_ReturnsError()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(
            Args(new { action = "convert", timezone = "UTC" }),
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter", result.Error);
        Assert.Contains("datetime", result.Error);
    }

    [Fact]
    public async Task DateTime_Convert_MissingTimezone_ReturnsError()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(
            Args(new { action = "convert", datetime = "2025-06-15T12:00:00Z" }),
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter", result.Error);
        Assert.Contains("timezone", result.Error);
    }

    [Fact]
    public async Task DateTime_Convert_InvalidDatetime_ReturnsError()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(
            Args(new { action = "convert", datetime = "not-a-date", timezone = "UTC" }),
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Invalid datetime format", result.Error);
    }

    [Fact]
    public async Task DateTime_Convert_InvalidTargetTimezone_ReturnsError()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(
            Args(new { action = "convert", datetime = "2025-06-15T12:00:00Z", timezone = "Bad/Zone" }),
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown target timezone", result.Error);
    }

    [Fact]
    public async Task DateTime_Convert_InvalidSourceTimezone_ReturnsError()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(
            Args(new { action = "convert", datetime = "2025-06-15T12:00:00Z", from_timezone = "Bad/Zone", timezone = "UTC" }),
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown source timezone", result.Error);
    }

    [Fact]
    public async Task DateTime_ListTimezones_ReturnsTimezones()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(Args(new { action = "list_timezones" }), _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("UTC", result.Content);
        Assert.Contains("Found", result.Content);
    }

    [Fact]
    public async Task DateTime_ListTimezones_WithSearch_FiltersResults()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(
            Args(new { action = "list_timezones", search = "UTC" }),
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("UTC", result.Content);
        Assert.Contains("matching 'UTC'", result.Content);
    }

    [Fact]
    public async Task DateTime_ListTimezones_NoMatches_ReturnsMessage()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(
            Args(new { action = "list_timezones", search = "ZZZNONEXISTENT" }),
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("No timezones found", result.Content);
    }

    [Fact]
    public async Task DateTime_MissingAction_ReturnsError()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(Args(new { }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter: action", result.Error);
    }

    [Fact]
    public async Task DateTime_UnknownAction_ReturnsError()
    {
        var tool = new DateTimeTool();

        var result = await tool.ExecuteAsync(Args(new { action = "invalid" }), _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown action", result.Error);
    }
}
