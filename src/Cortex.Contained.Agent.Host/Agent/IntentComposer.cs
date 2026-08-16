using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Builds the throwaway session a fired timer intent is answered in.
/// <para>
/// A timer used to be appended to the live conversation as a <c>Role = "user"</c> message, so the
/// model could not structurally tell a timer from the person speaking, and the instruction text
/// permanently consumed conversation context. Instead the intent is answered by a focused run over
/// a bounded, media-stripped tail of the conversation, and is never written into the conversation
/// at all — only whatever the agent decides to say or do comes back.
/// </para>
/// <para>
/// The session is throwaway but NOT isolated: it carries the real conversation id, so every tool
/// still acts on the conversation the timer belongs to.
/// </para>
/// </summary>
internal sealed partial class IntentComposer
{
    /// <summary>
    /// Framing appended to the system prompt for a composer run.
    /// <para>
    /// This is what makes a timer structurally distinguishable from the user. The intent still
    /// arrives as the final <c>user</c> message — that is simply how an instruction is handed to a
    /// model — but the system prompt states outright that it is not the user speaking. An internal
    /// message type could not do this job: no provider client serialises it.
    /// </para>
    /// </summary>
    internal const string Framing =
        "TIMER RUN — the user has NOT said anything just now.\n"
        + "A timer you set earlier has fired. The messages above are the recent conversation, for "
        + "context only; the final message is the INTENT you recorded when you set the timer, not "
        + "something the user typed or said. Do not reply to it as though it were a question, and "
        + "do not repeat it back verbatim.\n"
        + "Act on it: decide what — if anything — is worth saying or doing now, given what has "
        + "happened since. If it is no longer relevant, say nothing and do nothing. To reach the "
        + "user you must actually send a message; simply writing a reply here does not deliver it.";

    private readonly int tailTurns;
    private readonly IOptionsMonitor<ImageAgingConfig> imageAging;
    private readonly IImageDescriber? describer;
    private readonly ILogger<IntentComposer> logger;

    public IntentComposer(
        int tailTurns,
        IOptionsMonitor<ImageAgingConfig> imageAging,
        IImageDescriber? describer,
        ILogger<IntentComposer> logger)
    {
        this.tailTurns = tailTurns;
        this.imageAging = imageAging;
        this.describer = describer;
        this.logger = logger;
    }

    /// <summary>
    /// A fresh session holding the recent conversation followed by <paramref name="intentText"/>.
    /// The live session is not modified.
    /// </summary>
    public async Task<AgentSession> CreateSessionAsync(
        AgentSession live,
        string intentText,
        CancellationToken cancellationToken)
    {
        var composed = new AgentSession(live.ConversationId) { SystemPromptSuffix = Framing };

        // Read at use time, like every other consumer of this config — but never DESCRIBE. A
        // composer run is small and frequent, and this happens BEFORE the turn is abortable, so
        // one vision call per undescribed image in the tail would be both unstoppable and
        // untracked. Any description already cached on the block is still used.
        var configured = this.imageAging.CurrentValue;
        var aging = new ImageAgingConfig
        {
            PreserveRecentTurns = configured.PreserveRecentTurns,
            DescribeOnStrip = false,
        };

        var tail = ConversationTail.SelectLast(live.GetHistory(), this.tailTurns);
        foreach (var message in tail)
        {
            // Images become text stand-ins: a composer run is small and frequent, and shipping raw
            // image bytes into it would be expensive and, for most intents, pointless.
            var stripped = await ContextManager
                .StripMediaFromMessageAsync(message, aging, this.describer, cancellationToken)
                .ConfigureAwait(false);

            composed.AddMessage(stripped);
        }

        composed.AddMessage(new LlmMessage
        {
            Role = "user",
            Content = intentText,

            // Nothing that reaches a history surface should be mistakable for something the user
            // said, even though this session is discarded. The model is told via Framing above.
            MessageType = LlmMessageType.ScheduledTaskInstruction,
        });

        this.LogComposerSessionBuilt(live.ConversationId, tail.Count, this.tailTurns);
        return composed;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Composer session for {ConversationId}: {TailMessages} message(s) of tail (limit {TailTurns} turns)")]
    private partial void LogComposerSessionBuilt(string conversationId, int tailMessages, int tailTurns);
}
