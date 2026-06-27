using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Lists files and directories within the sandbox data directory.
/// Supports recursive listing with optional depth limit.
/// </summary>
internal sealed class FileListTool : IAgentTool
{
    private readonly string sandboxRoot;
    private const int MaxEntries = 1000;

    public FileListTool(string sandboxRoot)
    {
        this.sandboxRoot = sandboxRoot;
    }

    public string Name => "file_list";

    public string Description =>
        "List files and directories. Returns names with '/' suffix for directories. " +
        "Use 'recursive' to list all nested contents. Defaults to listing the root data directory.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path to list (default: root data directory)"
            },
            "recursive": {
              "type": "boolean",
              "description": "List contents recursively (default: false)"
            }
          }
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            var userPath = ".";
            if (root.TryGetProperty("path", out var pathElement) && pathElement.GetString() is { Length: > 0 } p)
            {
                userPath = p;
            }

            var recursive = false;
            if (root.TryGetProperty("recursive", out var recursiveEl))
            {
                recursive = recursiveEl.GetBoolean();
            }

            var resolved = SandboxPathResolver.Resolve(this.sandboxRoot, userPath);

            if (!Directory.Exists(resolved))
            {
                return Task.FromResult(AgentToolResult.Fail($"Directory not found: {userPath}"));
            }

            var rootFull = Path.GetFullPath(this.sandboxRoot);
            var sb = new StringBuilder();
            var count = 0;

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            // List directories first
            foreach (var dir in Directory.EnumerateDirectories(resolved, "*", option))
            {
                if (count >= MaxEntries)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"... truncated ({MaxEntries} entries shown)");
                    break;
                }

                var relative = Path.GetRelativePath(rootFull, dir).Replace('\\', '/');
                sb.AppendLine(relative + "/");
                count++;
            }

            // Then files
            foreach (var file in Directory.EnumerateFiles(resolved, "*", option))
            {
                if (count >= MaxEntries)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"... truncated ({MaxEntries} entries shown)");
                    break;
                }

                var relative = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
                var fileInfo = new FileInfo(file);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{relative} ({FormatSize(fileInfo.Length)})");
                count++;
            }

            if (count == 0)
            {
                sb.AppendLine("(empty directory)");
            }

            return Task.FromResult(AgentToolResult.Ok(sb.ToString().TrimEnd()));
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
