using System.Text.RegularExpressions;
using Cortex.Contained.Contracts.SystemPrompt;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Pure validator for <see cref="SystemPromptConfig"/>. Blocks unknown placeholders and
/// oversized fields; warns on missing recommended placeholders.
/// </summary>
public static partial class SystemPromptValidator
{
    [GeneratedRegex(@"\{\{([a-z_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    /// <summary>Validate the configuration.</summary>
    public static SystemPromptValidationResult Validate(SystemPromptConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var result = new SystemPromptValidationResult();

        ValidateTemplate(result, "MainTemplate", config.MainTemplate,
            SystemPromptPlaceholders.Main, SystemPromptPlaceholders.MainRecommended);
        ValidateTemplate(result, "SubagentTemplate", config.SubagentTemplate,
            SystemPromptPlaceholders.Subagent, SystemPromptPlaceholders.SubagentRecommended);

        CheckSegmentCap(result, "VoiceMode", config.VoiceMode);
        CheckSegmentCap(result, "CodingRelay", config.CodingRelay);
        CheckSegmentCap(result, "SubagentInstructions", config.SubagentInstructions);

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    private static void ValidateTemplate(
        SystemPromptValidationResult result, string field, string template,
        System.Collections.Frozen.FrozenSet<string> allowed, string[] recommended)
    {
        if (template.Length > SystemPromptPlaceholders.TemplateMaxChars)
        {
            result.Errors.Add($"{field} exceeds {SystemPromptPlaceholders.TemplateMaxChars} characters.");
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in PlaceholderRegex().Matches(template))
        {
            var name = m.Groups[1].Value;
            used.Add(name);
            if (!allowed.Contains(name))
            {
                result.Errors.Add($"{field} uses unknown placeholder {{{{{name}}}}}.");
            }
        }

        foreach (var name in recommended)
        {
            if (!used.Contains(name))
            {
                result.Warnings.Add($"{field} is missing recommended placeholder {{{{{name}}}}}.");
            }
        }
    }

    private static void CheckSegmentCap(SystemPromptValidationResult result, string field, string value)
    {
        if (value.Length > SystemPromptPlaceholders.SegmentMaxChars)
        {
            result.Errors.Add($"{field} exceeds {SystemPromptPlaceholders.SegmentMaxChars} characters.");
        }
    }
}
