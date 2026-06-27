using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Writes content to a file in the sandbox data directory.
/// Auto-creates parent directories. Overwrites existing files.
/// </summary>
internal sealed class FileWriteTool : IAgentTool
{
    private readonly string sandboxRoot;
    private readonly SkillRegistry? skillRegistry;
    private const int MaxContentBytes = 10 * 1024 * 1024; // 10 MB

    public FileWriteTool(string sandboxRoot, SkillRegistry? skillRegistry = null)
    {
        this.sandboxRoot = sandboxRoot;
        this.skillRegistry = skillRegistry;
    }

    public string Name => "file_write";

    public string Description =>
        "Write content to a file. Creates parent directories automatically. " +
        "Overwrites the file if it already exists. " +
        "Use this to create memos, skills, scripts, config files, and any structured data.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path to the file within the data directory"
            },
            "content": {
              "type": "string",
              "description": "The content to write to the file"
            }
          },
          "required": ["path", "content"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("path", out var pathElement))
            {
                return AgentToolResult.Fail("Missing required parameter: path");
            }

            if (!root.TryGetProperty("content", out var contentElement))
            {
                return AgentToolResult.Fail("Missing required parameter: content");
            }

            var userPath = pathElement.GetString() ?? string.Empty;
            var content = contentElement.GetString() ?? string.Empty;

            if (content.Length > MaxContentBytes)
            {
                return AgentToolResult.Fail($"Content too large ({content.Length:N0} chars). Maximum: {MaxContentBytes:N0}.");
            }

            var resolved = SandboxPathResolver.Resolve(this.sandboxRoot, userPath);

            // Auto-create parent directories
            var dir = Path.GetDirectoryName(resolved);
            if (dir is not null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(resolved, content, cancellationToken).ConfigureAwait(false);

            // Invalidate the skill cache when a skill file is written so the new/updated
            // skill appears in the system prompt on the next LLM call.
            if (this.skillRegistry is not null
                && userPath.StartsWith("skills/", StringComparison.OrdinalIgnoreCase)
                && userPath.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                this.skillRegistry.Invalidate();
            }

            var fileInfo = new FileInfo(resolved);
            return AgentToolResult.Ok($"Written {fileInfo.Length:N0} bytes to {userPath}");
        }
        catch (ArgumentException ex)
        {
            return AgentToolResult.Fail(ex.Message);
        }
        catch (IOException ex)
        {
            return AgentToolResult.Fail($"IO error: {ex.Message}");
        }
    }
}
