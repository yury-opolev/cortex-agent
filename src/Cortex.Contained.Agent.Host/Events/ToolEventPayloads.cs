using System.Text;
using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>
/// Builds <c>tool_call</c> / <c>tool_result</c> payloads under <see cref="ToolPayloadPolicy"/>.
/// Metadata is always recorded; args and digests only for allowlisted tools in a run that belongs
/// to a conversation (so they share that conversation's erasure domain), always redacted.
/// </summary>
public sealed class ToolEventPayloads
{
    private readonly KnownSecrets secrets;

    public ToolEventPayloads(KnownSecrets secrets)
    {
        this.secrets = secrets;
    }

    /// <summary>The payload of a <c>tool_call</c> event.</summary>
    public JsonObject ForCall(string toolName, string toolCallId, string? arguments, bool conversationKnown)
    {
        var payload = new JsonObject { ["tool"] = toolName, ["toolCallId"] = toolCallId };
        var policy = ToolPayloadPolicy.Classify(toolName);
        if (conversationKnown && policy is ToolPayloadClass.Allowlisted or ToolPayloadClass.Recall)
        {
            this.AddText(payload, "args", arguments ?? string.Empty, ToolPayloadPolicy.MaxArgsChars);
        }

        return payload;
    }

    /// <summary>The payload of a <c>tool_result</c> event.</summary>
    public JsonObject ForResult(string toolName, string toolCallId, AgentToolResult result, string? spillFile, bool conversationKnown)
    {
        ArgumentNullException.ThrowIfNull(result);
        var payload = new JsonObject { ["tool"] = toolName, ["toolCallId"] = toolCallId, ["ok"] = result.Success };
        if (!result.Success)
        {
            payload["errorClass"] = ErrorClass(result.Error);
        }

        var policy = ToolPayloadPolicy.Classify(toolName);
        if (policy == ToolPayloadClass.Mcp)
        {
            return payload;
        }

        var body = result.Success ? result.Content : result.Error ?? string.Empty;
        payload["bytes"] = Encoding.UTF8.GetByteCount(body);
        payload["sha256"] = EventHashing.Sha256Hex(body);
        if (spillFile is not null)
        {
            payload["spill"] = Path.GetFileName(spillFile);
        }

        if (conversationKnown && policy == ToolPayloadClass.Allowlisted)
        {
            this.AddText(payload, "digest", body, ToolPayloadPolicy.MaxDigestChars);
        }

        return payload;
    }

    /// <summary>A short, content-free class for a failure message.</summary>
    internal static string ErrorClass(string? error) => error switch
    {
        null or "" => "tool_error",
        _ when error.StartsWith("Unknown tool", StringComparison.Ordinal) => "unknown_tool",
        _ when error.Contains("cancelled", StringComparison.OrdinalIgnoreCase) => "cancelled",
        _ when error.StartsWith("Tool execution failed", StringComparison.Ordinal) => "exception",
        _ => "tool_error",
    };

    private void AddText(JsonObject payload, string name, string text, int maxChars)
    {
        if (!this.secrets.IsLoaded)
        {
            payload[name] = SensitiveDataRedactor.RedactionUnavailable;
            payload["redaction"] = "unavailable";
            return;
        }

        var redacted = SensitiveDataRedactor.RedactForPersistence(text, this.secrets.Values);
        var cut = redacted.Length > maxChars;
        payload[name] = cut ? redacted[..maxChars] : redacted;
        if (cut)
        {
            payload[name + "Truncated"] = true;
        }
    }
}
