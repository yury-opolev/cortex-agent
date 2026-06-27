namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// File-backed store for agent self-notes — operational knowledge the agent
/// writes for itself (rules, tips, learned patterns). Injected into the system
/// prompt alongside personality. The agent reads/writes via dedicated tools.
/// </summary>
public sealed class SelfNotesStore
{
    /// <summary>Maximum character count (~4000 tokens). Enforced on write.</summary>
    public const int MaxCharacters = 16000;

    /// <summary>Default content for new agents (bootstrap).</summary>
    public const string DefaultContent = """
        ## Getting started
        No notes yet. Introduce yourself and learn about the user.
        Save important observations here using self_notes_write.

        ## How to work
        - Search memory (memory_search) before answering when context might help
        - For tasks requiring 3+ tool calls, delegate to sub_agent
        - Save operational knowledge and learned patterns to self-notes
        """;

    private readonly string filePath;

    public SelfNotesStore(string filePath)
    {
        this.filePath = filePath;
    }

    /// <summary>
    /// Reads the current self-notes. Returns <see cref="DefaultContent"/> if the file
    /// doesn't exist yet (first run).
    /// </summary>
    public string Read()
    {
        try
        {
            if (File.Exists(this.filePath))
            {
                var content = File.ReadAllText(this.filePath).Trim();
                if (content.Length > 0)
                {
                    return content;
                }
            }
        }
        catch (IOException)
        {
            // Fall through to default
        }

        return DefaultContent;
    }

    /// <summary>
    /// Writes new self-notes content. Enforces a character budget.
    /// </summary>
    /// <returns>True if written successfully, false if content exceeds budget.</returns>
    public bool Write(string content)
    {
        if (content.Length > MaxCharacters)
        {
            return false;
        }

        try
        {
            var dir = Path.GetDirectoryName(this.filePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(this.filePath, content);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Path to the self-notes file (for migration/diagnostics).</summary>
    public string FilePath => this.filePath;
}
