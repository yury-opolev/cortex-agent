using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Reads a file from the sandbox data directory.
/// Returns the file content as text, with optional offset and limit for large files.
/// </summary>
internal sealed class FileReadTool : IAgentTool
{
    private readonly string sandboxRoot;
    private const int DefaultMaxBytes = 1024 * 1024; // 1 MB default max read

    public FileReadTool(string sandboxRoot)
    {
        this.sandboxRoot = sandboxRoot;
    }

    public string Name => "file_read";

    public string Description =>
        "Read the contents of a file. Returns text content. " +
        "Use 'offset' (0-based line number) and 'limit' (number of lines) for large files.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path to the file within the data directory"
            },
            "offset": {
              "type": "integer",
              "description": "Starting line number (0-based). Default: 0"
            },
            "limit": {
              "type": "integer",
              "description": "Maximum number of lines to return. Default: all lines"
            }
          },
          "required": ["path"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("path", out var pathElement))
            {
                return Task.FromResult(AgentToolResult.Fail("Missing required parameter: path"));
            }

            var userPath = pathElement.GetString() ?? string.Empty;
            var resolved = SandboxPathResolver.ResolveAndVerify(this.sandboxRoot, userPath);

            if (!File.Exists(resolved))
            {
                return Task.FromResult(AgentToolResult.Fail($"File not found: {userPath}"));
            }

            // Check file size
            var fileInfo = new FileInfo(resolved);
            if (fileInfo.Length > DefaultMaxBytes * 10) // 10MB hard limit
            {
                return Task.FromResult(AgentToolResult.Fail($"File too large ({fileInfo.Length:N0} bytes). Maximum: {DefaultMaxBytes * 10:N0} bytes."));
            }

            var offset = 0;
            var limit = int.MaxValue;

            if (root.TryGetProperty("offset", out var offsetEl))
            {
                offset = offsetEl.GetInt32();
            }

            if (root.TryGetProperty("limit", out var limitEl))
            {
                limit = limitEl.GetInt32();
            }

            var allLines = File.ReadAllLines(resolved);
            var selectedLines = allLines.Skip(offset).Take(limit).ToArray();
            var content = string.Join('\n', selectedLines);

            var totalLines = allLines.Length;
            var returnedLines = selectedLines.Length;
            var header = $"[{userPath}] ({totalLines} lines total, showing {offset}-{offset + returnedLines - 1})\n";

            return Task.FromResult(AgentToolResult.Ok(header + content));
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
}
