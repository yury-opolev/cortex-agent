using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Searches file contents for lines matching a regex pattern within the sandbox data directory.
/// Returns matching file paths, line numbers, and matching text — sorted by file modification time.
/// </summary>
internal sealed class GrepTool : IAgentTool
{
    private readonly string sandboxRoot;
    private const int MaxMatches = 100;
    private const int MaxLineLength = 2000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Directories to skip during recursive search.</summary>
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "__pycache__", ".venv", "venv",
        "bin", "obj", "dist", "build", ".next", ".nuget",
    };

    /// <summary>File extensions to skip (binary files).</summary>
    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".bin", ".obj", ".o",
        ".zip", ".tar", ".gz", ".bz2", ".7z", ".rar",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svg",
        ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flac",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx",
        ".woff", ".woff2", ".ttf", ".eot",
        ".db", ".sqlite", ".sqlite3",
    };

    public GrepTool(string sandboxRoot)
    {
        this.sandboxRoot = sandboxRoot;
    }

    public string Name => "grep";

    public string Description =>
        "Search file contents for lines matching a regex pattern. " +
        "Searches recursively from the specified directory. " +
        "Returns matching file paths, line numbers, and matching text. " +
        "Use 'include' to filter by file type (e.g., '*.cs', '*.{ts,tsx}'). " +
        "Supports full regex syntax (e.g., 'log.*Error', 'function\\s+\\w+').";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "pattern": {
              "type": "string",
              "description": "Regex pattern to search for in file contents"
            },
            "path": {
              "type": "string",
              "description": "Directory to search in, relative to data root (default: root)"
            },
            "include": {
              "type": "string",
              "description": "File glob filter (e.g., '*.cs', '*.{ts,tsx}', '*.py')"
            }
          },
          "required": ["pattern"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("pattern", out var patternElement))
            {
                return Task.FromResult(AgentToolResult.Fail("Missing required parameter: pattern"));
            }

            var pattern = patternElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return Task.FromResult(AgentToolResult.Fail("Pattern cannot be empty"));
            }

            // Validate the regex
            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.Compiled, RegexTimeout);
            }
            catch (RegexParseException ex)
            {
                return Task.FromResult(AgentToolResult.Fail($"Invalid regex pattern: {ex.Message}"));
            }

            var userPath = ".";
            if (root.TryGetProperty("path", out var pathElement) && pathElement.GetString() is { Length: > 0 } p)
            {
                userPath = p;
            }

            var searchRoot = SandboxPathResolver.Resolve(this.sandboxRoot, userPath);
            if (!Directory.Exists(searchRoot))
            {
                return Task.FromResult(AgentToolResult.Fail($"Directory not found: {userPath}"));
            }

            // Parse include filter into a set of extensions
            var includeExtensions = ParseIncludeFilter(root);

            // Search files, sorted by modification time (most recent first)
            var rootFull = Path.GetFullPath(this.sandboxRoot);
            var files = EnumerateFilesRecursive(searchRoot, includeExtensions, cancellationToken)
                .OrderByDescending(f => f.LastWriteTimeUtc);

            var sb = new StringBuilder();
            var totalMatches = 0;
            var matchedFiles = 0;

            foreach (var fileInfo in files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var fileMatches = SearchFile(fileInfo, regex, rootFull, sb, ref totalMatches, cancellationToken);
                if (fileMatches > 0)
                {
                    matchedFiles++;
                }

                if (totalMatches >= MaxMatches)
                {
                    break;
                }
            }

            if (totalMatches == 0)
            {
                return Task.FromResult(AgentToolResult.Ok($"No matches found for pattern '{pattern}'."));
            }

            var header = $"Found {totalMatches} match(es) across {matchedFiles} file(s)";
            if (totalMatches >= MaxMatches)
            {
                header += $" (showing first {MaxMatches}, use a more specific pattern or 'include' filter to narrow results)";
            }

            return Task.FromResult(AgentToolResult.Ok($"{header}:\n\n{sb.ToString().TrimEnd()}"));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(AgentToolResult.Fail(ex.Message));
        }
        catch (IOException ex)
        {
            return Task.FromResult(AgentToolResult.Fail($"IO error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Searches a single file for regex matches, appending results to the StringBuilder.
    /// Returns the number of matches found in this file.
    /// </summary>
    private static int SearchFile(
        FileInfo fileInfo,
        Regex regex,
        string rootFull,
        StringBuilder sb,
        ref int totalMatches,
        CancellationToken cancellationToken)
    {
        var fileMatchCount = 0;
        var relativePath = Path.GetRelativePath(rootFull, fileInfo.FullName).Replace('\\', '/');
        var wroteHeader = false;

        try
        {
            using var reader = new StreamReader(fileInfo.FullName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lineNumber = 0;

            while (reader.ReadLine() is { } line)
            {
                lineNumber++;

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (totalMatches >= MaxMatches)
                {
                    break;
                }

                try
                {
                    if (!regex.IsMatch(line))
                    {
                        continue;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // Line took too long to match — skip it
                    continue;
                }

                if (!wroteHeader)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{relativePath}:");
                    wroteHeader = true;
                }

                var displayLine = line.Length > MaxLineLength
                    ? string.Concat(line.AsSpan(0, MaxLineLength), "...")
                    : line;

                sb.Append("  Line ");
                sb.Append(lineNumber);
                sb.Append(": ");
                sb.AppendLine(displayLine);

                totalMatches++;
                fileMatchCount++;
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — skip unreadable files silently
        catch (Exception) when (fileMatchCount == 0)
#pragma warning restore CA1031
        {
            // Skip files that can't be read (binary, locked, encoding issues, etc.)
        }

        if (wroteHeader)
        {
            sb.AppendLine(); // Blank line between files
        }

        return fileMatchCount;
    }

    /// <summary>
    /// Recursively enumerates files, skipping binary extensions and known directories.
    /// </summary>
    private static IEnumerable<FileInfo> EnumerateFilesRecursive(
        string directory,
        HashSet<string>? includeExtensions,
        CancellationToken cancellationToken)
    {
        Queue<string> dirs = new();
        dirs.Enqueue(directory);

        while (dirs.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            var dir = dirs.Dequeue();

            // Enqueue subdirectories, skipping known non-content directories
            IEnumerable<string>? subDirs = null;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
#pragma warning disable CA1031 // Do not catch general exception types — skip inaccessible directories
            catch
#pragma warning restore CA1031
            {
                // Permission denied, etc.
            }

            if (subDirs is not null)
            {
                foreach (var subDir in subDirs)
                {
                    var dirName = Path.GetFileName(subDir);
                    if (!SkipDirectories.Contains(dirName))
                    {
                        dirs.Enqueue(subDir);
                    }
                }
            }

            // Enumerate files in this directory
            IEnumerable<string>? files = null;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
#pragma warning disable CA1031 // Do not catch general exception types — skip inaccessible directories
            catch
#pragma warning restore CA1031
            {
                continue;
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);

                // Skip binary file extensions
                if (SkipExtensions.Contains(ext))
                {
                    continue;
                }

                // Apply include filter if specified
                if (includeExtensions is not null && !includeExtensions.Contains(ext))
                {
                    continue;
                }

                FileInfo? fi = null;
                try
                {
                    fi = new FileInfo(file);
                }
#pragma warning disable CA1031 // Do not catch general exception types — skip inaccessible files
                catch
#pragma warning restore CA1031
                {
                    continue;
                }

                // Skip very large files (> 1 MB)
                if (fi.Length > 1024 * 1024)
                {
                    continue;
                }

                yield return fi;
            }
        }
    }

    /// <summary>
    /// Parses the "include" parameter into a set of file extensions.
    /// Supports formats: "*.cs", "*.{ts,tsx}", ".py".
    /// Returns null if no filter specified (match all).
    /// </summary>
    private static HashSet<string>? ParseIncludeFilter(JsonElement root)
    {
        if (!root.TryGetProperty("include", out var includeElement))
        {
            return null;
        }

        var include = includeElement.GetString();
        if (string.IsNullOrWhiteSpace(include))
        {
            return null;
        }

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Handle brace expansion: "*.{ts,tsx}" -> [".ts", ".tsx"]
        if (include.Contains('{') && include.Contains('}'))
        {
            var braceStart = include.IndexOf('{');
            var braceEnd = include.IndexOf('}');
            if (braceEnd > braceStart)
            {
                var parts = include[(braceStart + 1)..braceEnd].Split(',');
                foreach (var part in parts)
                {
                    var ext = part.Trim();
                    if (!ext.StartsWith('.'))
                    {
                        ext = "." + ext;
                    }

                    extensions.Add(ext);
                }

                return extensions;
            }
        }

        // Handle simple patterns: "*.cs", ".py"
        var value = include.TrimStart('*');
        if (!value.StartsWith('.'))
        {
            value = "." + value;
        }

        extensions.Add(value);
        return extensions;
    }
}
