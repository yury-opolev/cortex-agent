namespace Cortex.Contained.Channels.CloudMessaging.Envelope;

/// <summary>
/// Canonical wire type strings (kebab-case). Must match the ai-messenger
/// contracts exactly. Do NOT change without a versioned migration.
/// </summary>
internal static class EnvelopeTypes
{
    internal const string Text = "text";
    internal const string StreamChunk = "stream-chunk";
    internal const string Finalize = "finalize";
    internal const string Typing = "typing";
    internal const string Control = "control";
    internal const string Error = "error";
}

/// <summary>
/// Canonical wire "from" values (lowercase).
/// </summary>
internal static class EnvelopeFrom
{
    internal const string User = "user";
    internal const string Agent = "agent";
    internal const string System = "system";
}
