using System.Globalization;
using System.Text;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Truncates large tool output to prevent context window overflow.
/// When output exceeds <see cref="MaxBytes"/> or <see cref="MaxLines"/>,
/// the full content is saved to a temp file and the LLM receives a preview
/// with a hint to use Read/Grep to access the rest.
/// <para>
/// Modeled after OpenCode's truncation approach
/// (<c>opencode/packages/opencode/src/tool/truncation.ts</c>).
/// </para>
/// </summary>
internal static class ToolOutputTruncator
{
    /// <summary>Maximum bytes of tool output before truncation (50 KB).</summary>
    internal const int MaxBytes = 50 * 1024;

    /// <summary>Maximum number of lines before truncation.</summary>
    internal const int MaxLines = 2000;

    /// <summary>Directory name (under data root) where full outputs are saved.</summary>
    private const string OutputDirName = "tool-output";

    /// <summary>Retention period for saved tool output files.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static string? outputDir;

    /// <summary>
    /// Initializes the output directory. Must be called once at startup.
    /// Also runs cleanup of stale files.
    /// </summary>
    internal static void Initialize(string dataRoot)
    {
        outputDir = Path.Combine(dataRoot, OutputDirName);
        Directory.CreateDirectory(outputDir);
        CleanupStaleFiles();
    }

    /// <summary>
    /// Truncates tool output if it exceeds size/line limits.
    /// If truncated, saves full output to disk and returns a preview with a hint.
    /// </summary>
    /// <returns>
    /// The (possibly truncated) content string. If no truncation was needed,
    /// returns the original string unchanged.
    /// </returns>
    internal static string Truncate(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return output;
        }

        var byteCount = Encoding.UTF8.GetByteCount(output);
        var lines = output.Split('\n');

        if (lines.Length <= MaxLines && byteCount <= MaxBytes)
        {
            return output;
        }

        // Build the preview: take lines from the head until we hit both limits.
        var preview = new StringBuilder();
        var currentBytes = 0;
        var lineCount = 0;

        for (var i = 0; i < lines.Length && lineCount < MaxLines; i++)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + (i > 0 ? 1 : 0); // +1 for newline
            if (currentBytes + lineBytes > MaxBytes)
            {
                break;
            }

            if (i > 0)
            {
                preview.Append('\n');
            }

            preview.Append(lines[i]);
            currentBytes += lineBytes;
            lineCount++;
        }

        // Save full output to disk
        var filePath = SaveFullOutput(output);

        // Build the truncated message
        var removedBytes = byteCount - currentBytes;
        var removedLines = lines.Length - lineCount;

        preview.Append("\n\n...");
        if (removedBytes > removedLines)
        {
            preview.Append(removedBytes.ToString("N0", CultureInfo.InvariantCulture));
            preview.Append(" bytes truncated...");
        }
        else
        {
            preview.Append(removedLines);
            preview.Append(" lines truncated...");
        }

        if (filePath is not null)
        {
            preview.Append("\n\nFull output saved to: ");
            preview.Append(filePath);
            preview.Append("\nUse file_read with offset/limit to view specific sections, or grep to search the full content.");
        }

        return preview.ToString();
    }

    /// <summary>
    /// Saves full output to a timestamped file in the output directory.
    /// Returns the file path, or null if the directory is not initialized.
    /// </summary>
    private static string? SaveFullOutput(string output)
    {
        if (outputDir is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(outputDir);
            var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.txt";
            var filePath = Path.Combine(outputDir, fileName);
            File.WriteAllText(filePath, output, Encoding.UTF8);
            return filePath;
        }
#pragma warning disable CA1031 // Do not catch general exception types — best-effort save
        catch
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes tool output files older than <see cref="Retention"/>.
    /// Called once at startup. Errors are silently ignored.
    /// </summary>
    internal static void CleanupStaleFiles()
    {
        if (outputDir is null || !Directory.Exists(outputDir))
        {
            return;
        }

        try
        {
            var cutoff = DateTime.UtcNow - Retention;
            foreach (var file in Directory.EnumerateFiles(outputDir, "*.txt"))
            {
                try
                {
                    if (File.GetCreationTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
#pragma warning disable CA1031 // Do not catch general exception types — best-effort cleanup
                catch
#pragma warning restore CA1031
                {
                    // Skip files that can't be deleted
                }
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — best-effort cleanup
        catch
#pragma warning restore CA1031
        {
            // Directory enumeration failed — skip cleanup
        }
    }
}
