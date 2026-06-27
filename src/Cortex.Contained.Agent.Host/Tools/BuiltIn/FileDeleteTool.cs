using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Deletes a file or empty directory from the sandbox data directory.
/// Refuses to delete non-empty directories for safety.
/// </summary>
internal sealed class FileDeleteTool : IAgentTool
{
    private readonly string sandboxRoot;

    public FileDeleteTool(string sandboxRoot)
    {
        this.sandboxRoot = sandboxRoot;
    }

    public string Name => "file_delete";

    public string Description =>
        "Delete a file or an empty directory. " +
        "Non-empty directories cannot be deleted (remove contents first).";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path to the file or empty directory to delete"
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

            // Don't allow deleting the sandbox root itself
            var rootFull = Path.GetFullPath(this.sandboxRoot);
            if (string.Equals(resolved, rootFull, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resolved, rootFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AgentToolResult.Fail("Cannot delete the root data directory."));
            }

            if (File.Exists(resolved))
            {
                File.Delete(resolved);
                return Task.FromResult(AgentToolResult.Ok($"Deleted file: {userPath}"));
            }

            if (Directory.Exists(resolved))
            {
                if (Directory.EnumerateFileSystemEntries(resolved).Any())
                {
                    return Task.FromResult(AgentToolResult.Fail($"Directory is not empty: {userPath}. Remove contents first."));
                }

                Directory.Delete(resolved);
                return Task.FromResult(AgentToolResult.Ok($"Deleted empty directory: {userPath}"));
            }

            return Task.FromResult(AgentToolResult.Fail($"Not found: {userPath}"));
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
