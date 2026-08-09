using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Media;
using Cortex.Contained.Bridge.Connectors.Protocol;
using Cortex.Contained.Bridge.Connectors.Security;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Connectors;

/// <summary>
/// Keeps <c>docs/connector-protocol.schema.json</c> honest against the wire DTOs.
/// </summary>
/// <remarks>
/// The schema is the artefact third parties generate clients from, so silent drift between it and
/// the C# types is worse than having no schema at all — an integrator would build against a
/// contract the Bridge no longer honours. These tests fail the build when a field is added,
/// renamed or removed on one side only.
/// </remarks>
public sealed class ConnectorProtocolSchemaTests
{
    private static readonly JsonDocument Schema = LoadSchema();

    private static JsonDocument LoadSchema()
    {
        var path = FindRepoFile(Path.Combine("docs", "connector-protocol.schema.json"));
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}' walking up from the test assembly.");
    }

    private static JsonElement Definition(string name) =>
        Schema.RootElement.GetProperty("$defs").GetProperty(name);

    private static JsonElement PayloadProperties(string frameDefinition) =>
        Definition(frameDefinition)
            .GetProperty("properties")
            .GetProperty("payload")
            .GetProperty("properties");

    /// <summary>The JSON names a DTO actually serialises, honouring <c>[JsonPropertyName]</c>.</summary>
    private static IEnumerable<string> WireNames<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name));

    private static void AssertCovers<T>(JsonElement schemaProperties, params string[] except)
    {
        var excluded = except.ToHashSet(StringComparer.Ordinal);

        foreach (var name in WireNames<T>().Where(n => !excluded.Contains(n)))
        {
            Assert.True(
                schemaProperties.TryGetProperty(name, out _),
                $"{typeof(T).Name}.{name} is on the wire but missing from the published schema.");
        }
    }

    private static void AssertNoExtras<T>(JsonElement schemaProperties, params string[] except)
    {
        var wire = WireNames<T>().ToHashSet(StringComparer.Ordinal);
        var excluded = except.ToHashSet(StringComparer.Ordinal);

        foreach (var property in schemaProperties.EnumerateObject())
        {
            if (excluded.Contains(property.Name))
            {
                continue;
            }

            Assert.True(
                wire.Contains(property.Name),
                $"The schema documents '{property.Name}' on {typeof(T).Name}, but no such field is serialised.");
        }
    }

    // ── The schema parses and is well-formed ─────────────────────────

    [Fact]
    public void Schema_IsValidJsonWithADraft2020Identifier()
    {
        Assert.Equal(
            "https://json-schema.org/draft/2020-12/schema",
            Schema.RootElement.GetProperty("$schema").GetString());
    }

    [Fact]
    public void Schema_DocumentsEveryFrameType()
    {
        var documented = Definition("errorFrame") is { } // force $defs load
            ? Schema.RootElement.GetProperty("$defs").EnumerateObject()
                .Where(d => d.Value.TryGetProperty("properties", out var props)
                    && props.TryGetProperty("type", out var t)
                    && t.TryGetProperty("const", out _))
                .Select(d => d.Value.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : [];

        string[] everyFrameType =
        [
            ConnectorFrameTypes.Hello,
            ConnectorFrameTypes.Inbound,
            ConnectorFrameTypes.Abort,
            ConnectorFrameTypes.Pong,
            ConnectorFrameTypes.PairingRequired,
            ConnectorFrameTypes.Paired,
            ConnectorFrameTypes.PairingDenied,
            ConnectorFrameTypes.Ready,
            ConnectorFrameTypes.Typing,
            ConnectorFrameTypes.Stream,
            ConnectorFrameTypes.Outbound,
            ConnectorFrameTypes.Error,
            ConnectorFrameTypes.Ping,
        ];

        foreach (var frameType in everyFrameType)
        {
            Assert.Contains(frameType, documented);
        }
    }

    // ── Payload shapes match the DTOs ────────────────────────────────

    [Fact]
    public void Schema_HelloPayload_MatchesTheDto()
    {
        var properties = PayloadProperties("helloFrame");
        AssertCovers<ConnectorHelloPayload>(properties);
        AssertNoExtras<ConnectorHelloPayload>(properties);
    }

    [Fact]
    public void Schema_InboundPayload_MatchesTheDto()
    {
        var properties = PayloadProperties("inboundFrame");
        AssertCovers<ConnectorInboundPayload>(properties);
        AssertNoExtras<ConnectorInboundPayload>(properties);
    }

    [Fact]
    public void Schema_OutboundPayload_MatchesTheDto()
    {
        var properties = PayloadProperties("outboundFrame");
        AssertCovers<ConnectorOutboundPayload>(properties);
        AssertNoExtras<ConnectorOutboundPayload>(properties);
    }

    [Fact]
    public void Schema_ContentShape_MatchesTheDto()
    {
        var properties = Definition("content").GetProperty("properties");
        AssertCovers<ConnectorContentPayload>(properties);
        AssertNoExtras<ConnectorContentPayload>(properties);
    }

    [Fact]
    public void Schema_CapabilitiesShape_MatchesTheDto()
    {
        var properties = Definition("capabilities").GetProperty("properties");
        AssertCovers<ConnectorCapabilitiesPayload>(properties);
        AssertNoExtras<ConnectorCapabilitiesPayload>(properties);
    }

    [Fact]
    public void Schema_AttachmentShape_MatchesTheDtoExceptForTheRejectedUrlField()
    {
        var properties = Definition("attachment").GetProperty("properties");

        // `url` exists on the DTO ONLY so a frame carrying it can be positively rejected. It is
        // not part of the contract, so the schema must not advertise it.
        AssertCovers<ConnectorAttachmentPayload>(properties, except: "url");
        AssertNoExtras<ConnectorAttachmentPayload>(properties);

        Assert.False(
            properties.TryGetProperty("url", out _),
            "the schema must not advertise 'url' - the Bridge rejects any attachment carrying it");
    }

    [Fact]
    public void Schema_AttachmentShape_ForbidsUrlOutright()
    {
        var not = Definition("attachment").GetProperty("not").GetProperty("required");

        Assert.Contains("url", not.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Schema_AttachmentShape_RequiresExactlyOneCarryingMode()
    {
        var modes = Definition("attachment").GetProperty("oneOf");

        Assert.Equal(2, modes.GetArrayLength());
    }

    // ── Documented constants track the implementation ────────────────

    [Fact]
    public void Schema_AttachmentMimeTypes_MatchTheShippedAllowList()
    {
        var documented = Definition("attachment")
            .GetProperty("properties").GetProperty("mimeType").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Equal(ConnectorMediaConfig.DefaultAllowedMimeTypes, documented);
    }

    [Fact]
    public void Schema_EveryDocumentedErrorCode_ExistsInTheImplementation()
    {
        var documented = Definition("errorFrame")
            .GetProperty("properties").GetProperty("payload")
            .GetProperty("properties").GetProperty("code")
            .GetProperty("examples")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        var implemented = typeof(ConnectorErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var code in documented)
        {
            Assert.Contains(code, implemented);
        }
    }

    [Fact]
    public void Schema_EveryImplementedErrorCode_IsDocumented()
    {
        var documented = Definition("errorFrame")
            .GetProperty("properties").GetProperty("payload")
            .GetProperty("properties").GetProperty("code")
            .GetProperty("examples")
            .EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);

        var implemented = typeof(ConnectorErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

        foreach (var code in implemented)
        {
            // not_paired is defined but unreachable, and is documented as such in prose rather
            // than offered to integrators as something to handle.
            if (string.Equals(code, ConnectorErrorCodes.NotPaired, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Contains(code, documented);
        }
    }

    [Fact]
    public void Schema_AttachmentHandlePattern_AcceptsWhatTheStoreIssues()
    {
        var pattern = Definition("attachment")
            .GetProperty("properties").GetProperty("handle").GetProperty("pattern").GetString()!;

        var handle = ConnectorAttachmentStore.CreateHandle();

        Assert.Matches(pattern, handle);
    }

    [Fact]
    public void Schema_PairingCodePattern_AcceptsWhatTheGeneratorProduces()
    {
        var pattern = Definition("pairingRequiredFrame")
            .GetProperty("properties").GetProperty("payload")
            .GetProperty("properties").GetProperty("code").GetProperty("pattern").GetString()!;

        for (var i = 0; i < 100; i++)
        {
            Assert.Matches(pattern, ConnectorTokenGenerator.CreatePairingCode());
        }
    }

    [Fact]
    public void Schema_ChannelIdPattern_AcceptsWhatTheBridgeAssigns()
    {
        var pattern = Definition("pairedFrame")
            .GetProperty("properties").GetProperty("payload")
            .GetProperty("properties").GetProperty("channelId").GetProperty("pattern").GetString()!;

        Assert.Matches(pattern, ConnectorChannelId.Create("terminal", "default"));
    }

    [Fact]
    public void Schema_AttachmentLengthLimits_MatchTheValidator()
    {
        var attachment = Definition("attachment").GetProperty("properties");

        Assert.Equal(
            ConnectorAttachmentValidator.MaxFileNameLength,
            attachment.GetProperty("fileName").GetProperty("maxLength").GetInt32());

        Assert.Equal(
            ConnectorAttachmentValidator.MaxCaptionLength,
            attachment.GetProperty("caption").GetProperty("maxLength").GetInt32());

        Assert.Equal(
            ConnectorAttachmentValidator.MaxHandleLength,
            attachment.GetProperty("handle").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void Schema_EntityIdLimit_MatchesTheSession()
    {
        Assert.Equal(
            ConnectorSession.MaxIdLength,
            Definition("entityId").GetProperty("maxLength").GetInt32());
    }
}
