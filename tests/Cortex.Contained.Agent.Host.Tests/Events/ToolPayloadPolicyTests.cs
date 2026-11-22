using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Tests.Events;

public sealed class ToolPayloadPolicyTests
{
    private static ToolEventPayloads Payloads(bool loaded = true)
    {
        var secrets = new KnownSecrets();
        if (loaded)
        {
            secrets.Load(new LlmCredentials
            {
                Providers = [new LlmProviderCredential { Name = "p", Api = "openai-completions", ApiKey = "sk-known-secret-123", Models = [] }],
            });
        }

        return new ToolEventPayloads(secrets);
    }

    [Fact]
    public void Classify_IsExactAndDefaultDeny()
    {
        Assert.Equal(ToolPayloadClass.Allowlisted, ToolPayloadPolicy.Classify("date_time"));
        Assert.Equal(ToolPayloadClass.MetadataOnly, ToolPayloadPolicy.Classify("Date_Time"));
        Assert.Equal(ToolPayloadClass.MetadataOnly, ToolPayloadPolicy.Classify("brand_new_tool"));
        Assert.Equal(ToolPayloadClass.Recall, ToolPayloadPolicy.Classify("history_search"));
        Assert.Equal(ToolPayloadClass.Mcp, ToolPayloadPolicy.Classify("mcp__fitbit__sleep"));
    }

    [Fact]
    public void EveryBuiltInTool_IsReviewedInThePolicy()
    {
        // Reviewed whenever the tool set changes: a new IAgentTool fails here until it is classified.
        var names = typeof(IAgentTool).Assembly.GetTypes()
            .Where(t => typeof(IAgentTool).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Select(t =>
            {
                try
                {
                    return ((IAgentTool)RuntimeHelpers.GetUninitializedObject(t)).Name;
                }
                catch (NullReferenceException)
                {
                    return null; // name comes from instance state (MCP proxies): covered by the mcp__ prefix
                }
            })
            .OfType<string>()
            .ToList();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.True(ToolPayloadPolicy.IsReviewed(name), $"{name} is not classified in ToolPayloadPolicy"));
    }

    [Fact]
    public void ForResult_Allowlisted_KeepsRedactedTruncatedDigest()
    {
        var result = AgentToolResult.Ok("key sk-known-secret-123 " + string.Join(' ', Enumerable.Repeat("word", 400)));

        var payload = Payloads().ForResult("date_time", "c1", result, null, conversationKnown: true);

        var digest = (string)payload["digest"]!;
        Assert.Equal(ToolPayloadPolicy.MaxDigestChars, digest.Length);
        Assert.DoesNotContain("sk-known-secret-123", digest, StringComparison.Ordinal);
        Assert.True((bool)payload["digestTruncated"]!);
        Assert.NotNull(payload["sha256"]);
    }

    [Fact]
    public void ForResult_MetadataOnlyTool_NeverStoresBody()
    {
        var payload = Payloads().ForResult("file_read", "c1", AgentToolResult.Ok("zqfilebody"), "/app/data/tool-output/2026-x.txt", conversationKnown: true);

        Assert.DoesNotContain("zqfilebody", payload.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("2026-x.txt", (string?)payload["spill"]);
        Assert.Equal(10, (int)payload["bytes"]!);
    }

    [Fact]
    public void ForResult_Recall_IsHashOnly_AndCallArgsAreTheLocator()
    {
        var payloads = Payloads();

        var call = payloads.ForCall("history_search", "c1", "{\"query\":\"train\"}", conversationKnown: true);
        var result = payloads.ForResult("history_search", "c1", AgentToolResult.Ok("zqrecalled text"), null, conversationKnown: true);

        Assert.Contains("train", (string)call["args"]!, StringComparison.Ordinal);
        Assert.Null(result["digest"]);
        Assert.NotNull(result["sha256"]);
    }

    [Fact]
    public void ForResult_Mcp_DropsWholePayload()
    {
        var payload = Payloads().ForResult("mcp__mail__read", "c1", AgentToolResult.Ok("zqmailbody"), null, conversationKnown: true);

        Assert.Null(payload["sha256"]);
        Assert.Null(payload["bytes"]);
        Assert.DoesNotContain("zqmailbody", payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForResult_McpWithSpill_RecordsOnlyTheSpillFileName()
    {
        var payload = Payloads().ForResult("mcp__mail__read", "c1", AgentToolResult.Ok("zqmailbody"), "/app/data/tool-output/2026-m.txt", conversationKnown: true);

        Assert.Equal("2026-m.txt", (string?)payload["spill"]);
        Assert.Null(payload["sha256"]);
        Assert.DoesNotContain("zqmailbody", payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForCall_NoConversation_StoresNoArgs()
    {
        var payload = Payloads().ForCall("date_time", "c1", "{\"tz\":\"x\"}", conversationKnown: false);

        Assert.Null(payload["args"]);
    }

    [Fact]
    public void ForCall_SecretsNotLoaded_StoresRedactionUnavailable()
    {
        var payload = Payloads(loaded: false).ForCall("date_time", "c1", "{}", conversationKnown: true);

        Assert.Equal(SensitiveDataRedactor.RedactionUnavailable, (string?)payload["args"]);
        Assert.Equal("unavailable", (string?)payload["redaction"]);
    }

    [Fact]
    public void ForResult_Failure_RecordsErrorClass()
    {
        var payload = Payloads().ForResult("date_time", "c1", AgentToolResult.Fail("Tool execution was cancelled."), null, conversationKnown: true);

        Assert.False((bool)payload["ok"]!);
        Assert.Equal("cancelled", (string?)payload["errorClass"]);
    }
}
