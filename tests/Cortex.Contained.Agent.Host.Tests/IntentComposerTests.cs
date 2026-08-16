using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Covers building the throwaway session a fired timer intent is answered in.
/// <para>
/// Previously a fired timer was appended to the live conversation as a <c>Role = "user"</c>
/// message, so the model could not structurally tell a timer from the person speaking, and the
/// instruction text permanently consumed conversation context. Instead the intent is answered by a
/// focused run over a bounded, media-stripped tail — and is never written into the conversation at
/// all.
/// </para>
/// </summary>
public sealed class IntentComposerTests
{
    private static readonly ImageAgingConfig Aging = new() { PreserveRecentTurns = 0 };

    private static IOptionsMonitor<ImageAgingConfig> AgingMonitor()
    {
        var monitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        monitor.CurrentValue.Returns(Aging);
        return monitor;
    }

    private static IntentComposer Composer(int tailTurns = 16) =>
        new(tailTurns, AgingMonitor(), describer: null, NullLogger<IntentComposer>.Instance);

    private static AgentSession Live(params LlmMessage[] history)
    {
        var session = new AgentSession("conv-1");
        foreach (var message in history)
        {
            session.AddMessage(message);
        }

        return session;
    }

    private static LlmMessage User(string text) => new() { Role = "user", Content = text };

    private static LlmMessage Assistant(string text) => new() { Role = "assistant", Content = text };

    [Fact]
    public async Task The_composer_session_ends_with_the_intent()
    {
        var live = Live(User("we're on round 1"), Assistant("noted"));

        var composed = await Composer().CreateSessionAsync(live, "call the next round", CancellationToken.None);

        var last = composed.GetHistory()[^1];
        Assert.Contains("call the next round", last.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_composer_session_carries_the_recent_conversation()
    {
        // Without the tail the composer would answer the intent blind, which is the whole reason
        // a timer fires back into the conversation that created it rather than into a vacuum.
        var live = Live(User("we're on round 1"), Assistant("noted"));

        var composed = await Composer().CreateSessionAsync(live, "call the next round", CancellationToken.None);

        var history = composed.GetHistory();
        Assert.Equal("we're on round 1", history[0].Content);
        Assert.Equal("noted", history[1].Content);
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public async Task Only_the_configured_number_of_turns_is_carried()
    {
        var messages = new List<LlmMessage>();
        for (var i = 0; i < 20; i++)
        {
            messages.Add(User($"u{i}"));
            messages.Add(Assistant($"a{i}"));
        }

        var composed = await Composer(tailTurns: 4)
            .CreateSessionAsync(Live([.. messages]), "go", CancellationToken.None);

        // 4 turns of tail + the intent.
        var history = composed.GetHistory();
        Assert.Equal(5, history.Count);
        Assert.Equal("u18", history[0].Content);
    }

    [Fact]
    public async Task The_composer_session_targets_the_same_conversation_so_tools_act_on_it()
    {
        // Tools resolve their target from the conversation id; a composer run has to be able to
        // send, schedule and read against the conversation the timer belongs to.
        var composed = await Composer().CreateSessionAsync(Live(User("hi")), "go", CancellationToken.None);

        Assert.Equal("conv-1", composed.ConversationId);
    }

    [Fact]
    public async Task The_live_conversation_is_left_untouched()
    {
        // The intent must never become part of the conversation — not as a user turn, not at all.
        var live = Live(User("we're on round 1"), Assistant("noted"));

        await Composer().CreateSessionAsync(live, "call the next round", CancellationToken.None);

        Assert.Equal(2, live.GetHistory().Count);
        Assert.DoesNotContain(live.GetHistory(), m => m.Content?.Contains("call the next round", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Images_in_the_tail_are_replaced_with_a_textual_stand_in()
    {
        // A composer run is a small, frequent call; shipping raw image bytes into it would be
        // expensive and, for most intents, pointless.
        var withImage = new LlmMessage
        {
            Role = "user",
            Content = "look at this",
            ContentBlocks =
            [
                LlmContentBlock.TextBlock("look at this"),
                new LlmContentBlock { Type = "image", ImageData = "AAAA", ImageMediaType = "image/png" },
            ],
        };

        var composed = await Composer().CreateSessionAsync(Live(withImage), "go", CancellationToken.None);

        var carried = composed.GetHistory()[0];
        Assert.DoesNotContain(carried.ContentBlocks ?? [], b => b.Type == "image");
        Assert.Contains(carried.ContentBlocks ?? [], b => b.Type == "text");
    }

    [Fact]
    public async Task An_empty_conversation_still_produces_a_runnable_session()
    {
        var composed = await Composer().CreateSessionAsync(Live(), "go", CancellationToken.None);

        var only = Assert.Single(composed.GetHistory());
        Assert.Contains("go", only.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_composer_session_tells_the_model_this_is_not_the_user_speaking()
    {
        // This is what actually makes a timer distinguishable. MessageType cannot do it — no
        // provider client serialises it — so the framing has to ride on the system prompt.
        var composed = await Composer().CreateSessionAsync(Live(User("hi")), "go", CancellationToken.None);

        Assert.Contains("has NOT said anything", composed.SystemPromptSuffix, StringComparison.Ordinal);
        Assert.Contains("timer", composed.SystemPromptSuffix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_intent_is_marked_internal_so_it_never_surfaces_as_chat()
    {
        // Belt and braces: the composer session is discarded, but nothing that reaches a history
        // surface should be mistakable for something the user said. The model is told which is
        // which by the system framing, not by this field.
        var composed = await Composer().CreateSessionAsync(Live(User("hi")), "go", CancellationToken.None);

        var intent = composed.GetHistory()[^1];
        Assert.Equal(LlmMessageType.ScheduledTaskInstruction, intent.MessageType);

        // Asserted deliberately: an instruction is handed to a model as a user-role turn, and the
        // framing above is what stops it reading as the user.
        Assert.Equal("user", intent.Role);
    }
}
