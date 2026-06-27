namespace Cortex.Contained.Contracts;

/// <summary>Standard error codes used across the system.</summary>
public static class ErrorCodes
{
    public const string AuthFailed = "AUTH_FAILED";
    public const string RateLimited = "RATE_LIMITED";
    public const string InvalidMessage = "INVALID_MESSAGE";
    public const string ConversationNotFound = "CONVERSATION_NOT_FOUND";
    public const string AgentBusy = "AGENT_BUSY";
    public const string LlmError = "LLM_ERROR";
    public const string LlmTimeout = "LLM_TIMEOUT";
    public const string LlmProviderUnavailable = "LLM_PROVIDER_UNAVAILABLE";
    public const string LlmBudgetExceeded = "LLM_BUDGET_EXCEEDED";
    public const string ToolError = "TOOL_ERROR";
    public const string ChannelError = "CHANNEL_ERROR";
    public const string ConfigError = "CONFIG_ERROR";
    public const string InternalError = "INTERNAL_ERROR";
    public const string MessageTooLong = "MESSAGE_TOO_LONG";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
}
