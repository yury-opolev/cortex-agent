namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Discovers and indexes agent skills from the filesystem.
/// Skills are SKILL.md files under <c>/app/data/skills/{name}/SKILL.md</c>
/// with YAML frontmatter containing name and description.
/// </summary>
public sealed class SkillRegistry
{
    private readonly string skillsRoot;
    private IReadOnlyList<SkillEntry>? cache;

    public SkillRegistry(string skillsRoot)
    {
        this.skillsRoot = skillsRoot;
    }

    /// <summary>
    /// Returns all discovered skills (name, description, path).
    /// Results are cached until <see cref="Invalidate"/> is called.
    /// </summary>
    public IReadOnlyList<SkillEntry> GetSkillsSummary()
    {
        if (this.cache is not null)
        {
            return this.cache;
        }

        this.cache = this.ScanSkills();
        return this.cache;
    }

    /// <summary>Clears the cache so the next call to <see cref="GetSkillsSummary"/> re-scans.</summary>
    public void Invalidate()
    {
        this.cache = null;
    }

    /// <summary>
    /// Formats the skills section for injection into the system prompt.
    /// Always returns a section — with the skill list if skills exist,
    /// or with instructions on how to create skills if none exist yet.
    /// </summary>
    public string FormatForSystemPrompt()
    {
        var skills = this.GetSkillsSummary();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\n## Skills");
        sb.AppendLine("Reusable workflow skills. Load a skill with file_read before starting.");
        sb.AppendLine("Create or update skills by writing to skills/<name>/SKILL.md.");
        sb.AppendLine();
        sb.AppendLine("Every SKILL.md must start with this exact header format:");
        sb.AppendLine("```");
        sb.AppendLine("---");
        sb.AppendLine("name: skill-name");
        sb.AppendLine("description: One-line description of what this skill does");
        sb.AppendLine("---");
        sb.AppendLine("```");
        sb.AppendLine("The rest of the file is freeform (workflow steps, notes, examples, etc.).");
        sb.AppendLine("Skills can include subfolders (scripts/, templates/, references/). Skills appear here automatically.");

        if (skills.Count == 0)
        {
            sb.AppendLine("\nNo skills exist yet.");
        }
        else
        {
            sb.AppendLine();
            foreach (var skill in skills)
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"- {skill.Name} ({skill.RelativePath})");
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {skill.Description}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads the full content of a skill's SKILL.md file.
    /// Returns null if the skill does not exist.
    /// </summary>
    public string? ReadSkillContent(string skillName)
    {
        var path = Path.Combine(this.skillsRoot, skillName, "SKILL.md");
        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Returns relative paths of all <c>skills/{name}/SKILL.md</c> files that exist
    /// but have malformed or missing YAML frontmatter (and would be silently skipped
    /// by <see cref="GetSkillsSummary"/>).
    /// </summary>
    public List<string> FindMalformedSkills()
    {
        if (!Directory.Exists(this.skillsRoot))
        {
            return [];
        }

        var malformed = new List<string>();
        foreach (var dir in Directory.GetDirectories(this.skillsRoot))
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                continue;
            }

            var (name, description) = ParseFrontmatter(skillFile);
            if (name is null || description is null)
            {
                var dirName = Path.GetFileName(dir);
                malformed.Add($"skills/{dirName}/SKILL.md");
            }
        }

        return malformed;
    }

    private List<SkillEntry> ScanSkills()
    {
        if (!Directory.Exists(this.skillsRoot))
        {
            return [];
        }

        var entries = new List<SkillEntry>();

        foreach (var dir in Directory.GetDirectories(this.skillsRoot))
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                continue;
            }

            var (name, description) = ParseFrontmatter(skillFile);
            if (name is null || description is null)
            {
                continue;
            }

            var dirName = Path.GetFileName(dir);
            entries.Add(new SkillEntry(name, description, $"skills/{dirName}/SKILL.md"));
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return entries;
    }

    private static (string? Name, string? Description) ParseFrontmatter(string filePath)
    {
        // Read only enough lines to cover the frontmatter block; the body is irrelevant here.
        var lines = File.ReadLines(filePath).Take(50).ToArray();
        if (lines.Length < 3 || lines[0].Trim() != "---")
        {
            return (null, null);
        }

        string? name = null;
        string? description = null;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "---")
            {
                break;
            }

            if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                name = line["name:".Length..].Trim();
            }
            else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                description = line["description:".Length..].Trim();
            }
        }

        return (name, description);
    }
}

/// <summary>A discovered skill's metadata.</summary>
public sealed record SkillEntry(string Name, string Description, string RelativePath);
