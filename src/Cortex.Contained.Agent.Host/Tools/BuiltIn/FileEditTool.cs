using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Edits a file by replacing occurrences of old text with new text.
/// Supports single replacement or replace-all mode.
/// </summary>
internal sealed class FileEditTool : IAgentTool
{
    private readonly string sandboxRoot;

    public FileEditTool(string sandboxRoot)
    {
        this.sandboxRoot = sandboxRoot;
    }

    public string Name => "file_edit";

    public string Description =>
        "Edit a file by replacing specific text. Provide the exact text to find " +
        "and the replacement text. Set 'replace_all' to true to replace all occurrences.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path to the file within the data directory"
            },
            "old_text": {
              "type": "string",
              "description": "The exact text to find in the file"
            },
            "new_text": {
              "type": "string",
              "description": "The replacement text"
            },
            "replace_all": {
              "type": "boolean",
              "description": "Replace all occurrences (default: false, replaces only the first)"
            }
          },
          "required": ["path", "old_text", "new_text"]
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

            if (!root.TryGetProperty("old_text", out var oldTextElement))
            {
                return AgentToolResult.Fail("Missing required parameter: old_text");
            }

            if (!root.TryGetProperty("new_text", out var newTextElement))
            {
                return AgentToolResult.Fail("Missing required parameter: new_text");
            }

            var userPath = pathElement.GetString() ?? string.Empty;
            var oldText = oldTextElement.GetString() ?? string.Empty;
            var newText = newTextElement.GetString() ?? string.Empty;
            var replaceAll = false;

            if (root.TryGetProperty("replace_all", out var replaceAllEl))
            {
                replaceAll = replaceAllEl.GetBoolean();
            }

            if (string.IsNullOrEmpty(oldText))
            {
                return AgentToolResult.Fail("old_text cannot be empty");
            }

            var resolved = SandboxPathResolver.ResolveAndVerify(this.sandboxRoot, userPath);

            if (!File.Exists(resolved))
            {
                return AgentToolResult.Fail($"File not found: {userPath}");
            }

            var content = await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);

            if (!content.Contains(oldText, StringComparison.Ordinal))
            {
                return AgentToolResult.Fail("old_text not found in file");
            }

            string updatedContent;
            int replacementCount;

            if (replaceAll)
            {
                replacementCount = CountOccurrences(content, oldText);
                updatedContent = content.Replace(oldText, newText, StringComparison.Ordinal);
            }
            else
            {
                replacementCount = 1;
                var index = content.IndexOf(oldText, StringComparison.Ordinal);
                updatedContent = string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));
            }

            await File.WriteAllTextAsync(resolved, updatedContent, cancellationToken).ConfigureAwait(false);

            return AgentToolResult.Ok($"Replaced {replacementCount} occurrence(s) in {userPath}");
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

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
