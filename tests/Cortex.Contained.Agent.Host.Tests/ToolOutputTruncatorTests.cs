using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

public class ToolOutputTruncatorTests : IDisposable
{
    private readonly string _tempDir;

    public ToolOutputTruncatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"truncator-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        ToolOutputTruncator.Initialize(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    // ── Small output: no truncation ─────────────────────────────────────

    [Fact]
    public void Truncate_SmallOutput_ReturnsUnchanged()
    {
        var output = "Hello, world!\nLine 2\nLine 3";

        var result = ToolOutputTruncator.Truncate(output);

        Assert.Same(output, result); // Reference equality — no copy
    }

    [Fact]
    public void Truncate_EmptyString_ReturnsEmpty()
    {
        var result = ToolOutputTruncator.Truncate(string.Empty);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Truncate_NullString_ReturnsNull()
    {
        var result = ToolOutputTruncator.Truncate(null!);

        Assert.Null(result);
    }

    [Fact]
    public void Truncate_ExactlyAtLimit_ReturnsUnchanged()
    {
        // 2000 lines, each short enough to be under 50KB total
        var lines = Enumerable.Range(1, ToolOutputTruncator.MaxLines)
            .Select(i => $"Line {i}");
        var output = string.Join('\n', lines);

        // Only truncate if BOTH conditions exceeded — check bytes too
        if (System.Text.Encoding.UTF8.GetByteCount(output) <= ToolOutputTruncator.MaxBytes)
        {
            var result = ToolOutputTruncator.Truncate(output);
            Assert.Same(output, result);
        }
    }

    // ── Large output: byte limit ────────────────────────────────────────

    [Fact]
    public void Truncate_ExceedsMaxBytes_TruncatesAndSavesFile()
    {
        // Create output that exceeds 50KB but has few lines
        var bigLine = new string('x', ToolOutputTruncator.MaxBytes + 10_000);
        var output = $"Header line\n{bigLine}\nFooter line";

        var result = ToolOutputTruncator.Truncate(output);

        // Result should be shorter than original
        Assert.True(result.Length < output.Length,
            $"Truncated result ({result.Length}) should be shorter than original ({output.Length})");

        // Should contain truncation notice
        Assert.Contains("truncated", result);

        // Should contain hint to use file_read
        Assert.Contains("Full output saved to:", result);
        Assert.Contains("file_read", result);

        // File should have been saved
        var savedFiles = Directory.GetFiles(Path.Combine(_tempDir, "tool-output"), "*.txt");
        Assert.Single(savedFiles);

        // Saved file should contain the full original content
        var savedContent = File.ReadAllText(savedFiles[0]);
        Assert.Equal(output, savedContent);
    }

    // ── Large output: line limit ────────────────────────────────────────

    [Fact]
    public void Truncate_ExceedsMaxLines_TruncatesAndSavesFile()
    {
        // 3000 short lines — under byte limit but over line limit
        var lines = Enumerable.Range(1, 3000).Select(i => $"Line {i}");
        var output = string.Join('\n', lines);

        var result = ToolOutputTruncator.Truncate(output);

        Assert.True(result.Length < output.Length);
        Assert.Contains("truncated", result);
        Assert.Contains("Full output saved to:", result);

        // The preview should contain the first lines
        Assert.StartsWith("Line 1\n", result);

        // File saved
        var savedFiles = Directory.GetFiles(Path.Combine(_tempDir, "tool-output"), "*.txt");
        Assert.Single(savedFiles);
    }

    // ── Preview content ─────────────────────────────────────────────────

    [Fact]
    public void Truncate_PreviewContainsFirstLines()
    {
        var lines = Enumerable.Range(1, 3000).Select(i => $"Short line {i}");
        var output = string.Join('\n', lines);

        var result = ToolOutputTruncator.Truncate(output);

        // First line should be in the preview
        Assert.Contains("Short line 1", result);
        // Line 2000 should be in the preview (it's at the limit)
        Assert.Contains("Short line 100", result);
    }

    [Fact]
    public void Truncate_PreviewDoesNotContainTruncatedLines()
    {
        var lines = Enumerable.Range(1, 3000).Select(i => $"Short line {i}");
        var output = string.Join('\n', lines);

        var result = ToolOutputTruncator.Truncate(output);

        // Line 3000 should NOT be in the preview (it was truncated)
        Assert.DoesNotContain("Short line 3000", result);
    }

    // ── Hint format ─────────────────────────────────────────────────────

    [Fact]
    public void Truncate_HintContainsAbsoluteFilePath()
    {
        var output = new string('x', ToolOutputTruncator.MaxBytes + 1000);

        var result = ToolOutputTruncator.Truncate(output);

        // Extract the file path from the hint
        var pathPrefix = "Full output saved to: ";
        var pathStart = result.IndexOf(pathPrefix, StringComparison.Ordinal) + pathPrefix.Length;
        var pathEnd = result.IndexOf('\n', pathStart);
        var filePath = result[pathStart..pathEnd];

        // Path should be absolute
        Assert.True(Path.IsPathRooted(filePath), $"File path should be absolute: {filePath}");

        // File should exist
        Assert.True(File.Exists(filePath), $"Saved file should exist: {filePath}");
    }

    // ── Cleanup ─────────────────────────────────────────────────────────

    [Fact]
    public void CleanupStaleFiles_DeletesOldFiles()
    {
        var outputDir = Path.Combine(_tempDir, "tool-output");
        Directory.CreateDirectory(outputDir);

        // Create a "stale" file with old creation time
        var staleFile = Path.Combine(outputDir, "old-file.txt");
        File.WriteAllText(staleFile, "stale content");
        File.SetCreationTimeUtc(staleFile, DateTime.UtcNow - ToolOutputTruncator.Retention - TimeSpan.FromHours(1));

        // Create a "fresh" file
        var freshFile = Path.Combine(outputDir, "new-file.txt");
        File.WriteAllText(freshFile, "fresh content");

        ToolOutputTruncator.CleanupStaleFiles();

        Assert.False(File.Exists(staleFile), "Stale file should be deleted");
        Assert.True(File.Exists(freshFile), "Fresh file should be preserved");
    }

    [Fact]
    public void CleanupStaleFiles_PreservesRecentFiles()
    {
        var outputDir = Path.Combine(_tempDir, "tool-output");
        Directory.CreateDirectory(outputDir);

        // Create several recent files
        for (var i = 0; i < 5; i++)
        {
            var file = Path.Combine(outputDir, $"recent-{i}.txt");
            File.WriteAllText(file, $"content {i}");
        }

        ToolOutputTruncator.CleanupStaleFiles();

        var remaining = Directory.GetFiles(outputDir, "*.txt");
        Assert.Equal(5, remaining.Length);
    }

    // ── Integration: 30 messages scenario ───────────────────────────────

    [Fact]
    public void Truncate_120KB_ToolResult_TruncatedTo50KB()
    {
        // Simulate the production scenario: a web_fetch returning 120KB
        var bigWebPage = string.Join('\n',
            Enumerable.Range(1, 5000).Select(i => $"<p>Paragraph {i} with some content about the API documentation for method {i}.</p>"));

        var result = ToolOutputTruncator.Truncate(bigWebPage);

        // Result should be much smaller than original
        var resultBytes = System.Text.Encoding.UTF8.GetByteCount(result);
        // Allow overhead for the truncation notice and hint (up to ~500 bytes extra)
        Assert.True(resultBytes < ToolOutputTruncator.MaxBytes + 500,
            $"Truncated result ({resultBytes} bytes) should be close to MaxBytes ({ToolOutputTruncator.MaxBytes})");

        // Original content should be fully saved
        var savedFiles = Directory.GetFiles(Path.Combine(_tempDir, "tool-output"), "*.txt");
        Assert.Single(savedFiles);
        var savedContent = File.ReadAllText(savedFiles[0]);
        Assert.Equal(bigWebPage, savedContent);
    }

    [Fact]
    public void Truncate_512KB_CommandOutput_TruncatedWithPreview()
    {
        // Simulate RunCommandTool's max output (512KB)
        var lines = Enumerable.Range(1, 10000)
            .Select(i => $"[2025-01-15 10:30:{i % 60:D2}] INFO Processing batch item {i} of 10000 — status=OK bytes={i * 100}");
        var output = $"--- stdout ---\n{string.Join('\n', lines)}\n--- exit code: 0 ---";

        var result = ToolOutputTruncator.Truncate(output);

        Assert.Contains("--- stdout ---", result); // Header preserved in preview
        Assert.Contains("truncated", result);
        Assert.Contains("Full output saved to:", result);
        Assert.DoesNotContain("exit code: 0", result); // Footer was truncated away
    }
}
