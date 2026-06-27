using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;

namespace Cortex.Contained.Agent.Host.Tests;

public class GrepToolTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly GrepTool _tool;
    private static readonly ToolExecutionContext TestContext = new()
    {
        ConversationId = "test-conv",
        ChannelId = "test-channel",
    };

    public GrepToolTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"grep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _tool = new GrepTool(_sandboxRoot);

        // Create test file structure:
        // root/
        //   hello.txt         — "Hello World\nGoodbye World\nHello Again"
        //   notes/
        //     readme.md       — "# Title\nSome notes about the project\n## API\nEndpoint: /api/users"
        //     todo.md         — "- Fix bug #123\n- Add grep tool\n- Fix bug #456"
        //   src/
        //     app.cs          — "using System;\nclass App {\n  void Run() { Console.WriteLine(\"Hello\"); }\n}"
        //     utils.cs        — "namespace Utils {\n  class Logger {\n    void Log(string msg) { }\n  }\n}"
        //   data/
        //     binary.dll      — (should be skipped)
        //   node_modules/
        //     pkg/index.js    — (should be skipped — directory ignored)

        WriteFile("hello.txt", "Hello World\nGoodbye World\nHello Again");
        WriteFile("notes/readme.md", "# Title\nSome notes about the project\n## API\nEndpoint: /api/users");
        WriteFile("notes/todo.md", "- Fix bug #123\n- Add grep tool\n- Fix bug #456");
        WriteFile("src/app.cs", "using System;\nclass App {\n  void Run() { Console.WriteLine(\"Hello\"); }\n}");
        WriteFile("src/utils.cs", "namespace Utils {\n  class Logger {\n    void Log(string msg) { }\n  }\n}");
        WriteFile("data/binary.dll", "binary content that should not be searched");
        WriteFile("node_modules/pkg/index.js", "module.exports = 'should be skipped';");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandboxRoot))
            {
                Directory.Delete(_sandboxRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_sandboxRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    // ── Basic search ────────────────────────────────────────────────────

    [Fact]
    public async Task Grep_SimplePattern_FindsMatches()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "Hello"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Hello World", result.Content);
        Assert.Contains("Hello Again", result.Content);
    }

    [Fact]
    public async Task Grep_RegexPattern_FindsMatches()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "bug #\\d+"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("bug #123", result.Content);
        Assert.Contains("bug #456", result.Content);
    }

    [Fact]
    public async Task Grep_NoMatches_ReturnsNoMatchesMessage()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "nonexistent_xyz_pattern"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("No matches found", result.Content);
    }

    // ── Include filter ──────────────────────────────────────────────────

    [Fact]
    public async Task Grep_IncludeFilter_OnlySearchesMatchingFiles()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "Hello", "include": "*.cs"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        // Should find "Hello" in app.cs but NOT in hello.txt
        Assert.Contains("app.cs", result.Content);
        Assert.DoesNotContain("hello.txt", result.Content);
    }

    [Fact]
    public async Task Grep_BraceExpansionFilter_MatchesMultipleExtensions()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "#", "include": "*.{md,txt}"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        // Should find matches in .md and .txt files
        Assert.Contains("todo.md", result.Content);
        // Should NOT find anything in .cs files
        Assert.DoesNotContain("app.cs", result.Content);
    }

    // ── Path scoping ────────────────────────────────────────────────────

    [Fact]
    public async Task Grep_PathFilter_OnlySearchesSubdirectory()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "Hello", "path": "src"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        // Should find "Hello" in src/app.cs but NOT in hello.txt (root level)
        Assert.Contains("app.cs", result.Content);
        Assert.DoesNotContain("hello.txt", result.Content);
    }

    [Fact]
    public async Task Grep_NonExistentPath_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "Hello", "path": "nonexistent"}""",
            TestContext,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    // ── Skipped directories and files ───────────────────────────────────

    [Fact]
    public async Task Grep_SkipsNodeModules()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "should be skipped"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("No matches found", result.Content);
    }

    [Fact]
    public async Task Grep_SkipsBinaryExtensions()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "binary content"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("No matches found", result.Content);
    }

    // ── Output format ───────────────────────────────────────────────────

    [Fact]
    public async Task Grep_OutputContainsLineNumbers()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "Goodbye"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        // "Goodbye World" is line 2 of hello.txt
        Assert.Contains("Line 2:", result.Content);
    }

    [Fact]
    public async Task Grep_OutputContainsFileCount()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "Hello"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        // "Hello" appears in hello.txt and src/app.cs
        Assert.Contains("file(s)", result.Content);
    }

    // ── Error handling ──────────────────────────────────────────────────

    [Fact]
    public async Task Grep_MissingPattern_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            """{"path": "src"}""",
            TestContext,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter: pattern", result.Error);
    }

    [Fact]
    public async Task Grep_EmptyPattern_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": ""}""",
            TestContext,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("empty", result.Error);
    }

    [Fact]
    public async Task Grep_InvalidRegex_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            """{"pattern": "[invalid"}""",
            TestContext,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Invalid regex", result.Error);
    }

    // ── Match limit ─────────────────────────────────────────────────────

    [Fact]
    public async Task Grep_ManyMatches_TruncatesAt100()
    {
        // Create a file with 200 matching lines
        var lines = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"match-target line {i}"));
        WriteFile("many-matches.txt", lines);

        var result = await _tool.ExecuteAsync(
            """{"pattern": "match-target"}""",
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("100", result.Content); // Should mention the cap
        Assert.Contains("more specific pattern", result.Content);
    }
}
