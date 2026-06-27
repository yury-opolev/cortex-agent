using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Searches for files matching a glob pattern within the sandbox data directory.
/// Returns matching file paths relative to the data root.
/// </summary>
internal sealed class FileFindTool : IAgentTool
{
    private readonly string sandboxRoot;
    private const int MaxResults = 500;

    public FileFindTool(string sandboxRoot)
    {
        this.sandboxRoot = sandboxRoot;
    }

    public string Name => "file_find";

    public string Description =>
        "Search for files matching a glob pattern (e.g., '*.md', 'memos/**/*.txt', 'scripts/*.py'). " +
        "Returns matching file paths relative to the data directory.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "pattern": {
              "type": "string",
              "description": "Glob pattern to match files (e.g., '*.md', 'memos/**/*.txt')"
            },
            "path": {
              "type": "string",
              "description": "Subdirectory to search in (default: root data directory)"
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

            // Determine if we need recursive search
            var isRecursive = pattern.Contains("**") || pattern.Contains('/');

            // Extract the file pattern (last segment after /)
            var searchPattern = pattern;
            var subDir = searchRoot;

            if (pattern.Contains('/') && !pattern.Contains("**"))
            {
                // e.g., "memos/*.md" -> search in "memos" with pattern "*.md"
                var lastSlash = pattern.LastIndexOf('/');
                var dirPart = pattern[..lastSlash];
                searchPattern = pattern[(lastSlash + 1)..];
                subDir = SandboxPathResolver.Resolve(this.sandboxRoot, Path.Combine(userPath, dirPart));

                if (!Directory.Exists(subDir))
                {
                    return Task.FromResult(AgentToolResult.Fail($"Directory not found: {Path.Combine(userPath, dirPart)}"));
                }
            }
            else if (pattern.Contains("**"))
            {
                // For ** patterns, search recursively from the root
                // Strip the ** prefix to get the file pattern
                var starStarIndex = pattern.IndexOf("**", StringComparison.Ordinal);
                if (starStarIndex > 0)
                {
                    var dirPart = pattern[..(starStarIndex - 1)]; // strip trailing /
                    subDir = SandboxPathResolver.Resolve(this.sandboxRoot, Path.Combine(userPath, dirPart));
                }
                searchPattern = pattern[(pattern.LastIndexOf('/') + 1)..];
                isRecursive = true;
            }

            var option = isRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var rootFull = Path.GetFullPath(this.sandboxRoot);

            var sb = new StringBuilder();
            var count = 0;

            foreach (var file in Directory.EnumerateFiles(subDir, searchPattern, option))
            {
                if (count >= MaxResults)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"... truncated ({MaxResults} results shown)");
                    break;
                }

                var relative = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
                var fileInfo = new FileInfo(file);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{relative} ({FormatSize(fileInfo.Length)})");
                count++;
            }

            if (count == 0)
            {
                return Task.FromResult(AgentToolResult.Ok($"No files matching '{pattern}' found."));
            }

            return Task.FromResult(AgentToolResult.Ok($"Found {count} file(s):\n{sb.ToString().TrimEnd()}"));
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

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
    };
}
