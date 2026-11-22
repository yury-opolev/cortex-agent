using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Tests.StorageEncryption;

/// <summary>
/// Bridge and channel logs (14 days under %LOCALAPPDATA%\Cortex\logs) must not hold message text or
/// transcripts: no <c>[LoggerMessage]</c> template may carry a content placeholder.
/// </summary>
public sealed partial class BridgeLogTemplateHygieneTests
{
    private static readonly HashSet<string> ContentPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content", "Text", "Title", "Query", "Response", "LlmResponse", "Transcript", "Prompt", "Summary", "Snippet", "Fact", "Utterance",
    };

    public static TheoryData<string> Assemblies => new()
    {
        typeof(Cortex.Contained.Bridge.Security.SecretManager).Assembly.GetName().Name!,
        typeof(Cortex.Contained.Channels.Voice.VoiceChannel).Assembly.GetName().Name!,
        typeof(Cortex.Contained.Channels.Discord.DiscordChannel).Assembly.GetName().Name!,
        typeof(Cortex.Contained.Channels.WebChat.WebChatHub).Assembly.GetName().Name!,
        typeof(Cortex.Contained.Channels.CloudMessaging.CloudMessagingChannel).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void LoggerMessages_CarryNoContentPlaceholders(string assemblyName)
    {
        var offenders = Assembly.Load(assemblyName).GetTypes()
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

    [GeneratedRegex(@"\{(\w+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();
}
