using System.Reflection;
using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>
/// Agent logs (14 days in the data volume) must not hold message or memory text, or encryption at rest
/// would be bypassed through them: no <c>[LoggerMessage]</c> template may carry a content placeholder.
/// </summary>
public sealed partial class LogTemplateHygieneTests
{
    private static readonly HashSet<string> ContentPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content", "Text", "Title", "Query", "Response", "LlmResponse", "Transcript", "Prompt", "Summary", "Snippet", "Fact", "Utterance",
        "Description", "Name", "Arguments",
    };

    [Fact]
    public void AgentHost_LoggerMessages_CarryNoContentPlaceholders()
    {
        var offenders = typeof(Cortex.Contained.Agent.Host.Hubs.AgentHub).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(x => x.Attribute?.Message is not null)
            .SelectMany(x => Placeholder().Matches(x.Attribute!.Message!)
                .Select(p => p.Groups[1].Value)
                .Where(ContentPlaceholders.Contains)
                .Select(p => $"{x.Method.DeclaringType!.Name}.{x.Method.Name}: {{{p}}}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ContentRef_FormatsLengthAndHashNeverTheText()
    {
        var text = "Call Zürich about the merger";

        var formatted = ((ContentRef)text).ToString();

        Assert.DoesNotContain("merger", formatted, StringComparison.Ordinal);
        Assert.StartsWith("28 chars #", formatted, StringComparison.Ordinal);
        Assert.Equal(formatted, ((ContentRef)text).ToString());
        Assert.NotEqual(formatted, ((ContentRef)"Call Zürich about the mergers").ToString());
        Assert.Equal("0 chars", ((ContentRef)(string?)null).ToString());
    }

    [GeneratedRegex(@"\{(\w+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();
}
