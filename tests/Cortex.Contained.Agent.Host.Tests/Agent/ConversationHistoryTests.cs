using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public sealed class ConversationHistoryTests
{
    // ── Add ──────────────────────────────────────────────────────────────

    [Fact]
    public void Add_IncrementsCount()
    {
        var history = new ConversationHistory();
        history.Add(UserMessage("hello"));

        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Add_UpdatesLastMessageAt()
    {
        var history = new ConversationHistory();
        var before = DateTimeOffset.UtcNow;
        history.Add(UserMessage("hello"));
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(history.LastMessageAt, before, after);
    }

    // ── AppendOrGlueAssistant ─────────────────────────────────────────────

    [Fact]
    public void AppendOrGlueAssistant_MergesConsecutivePlainAssistantMessages()
    {
        var history = new ConversationHistory();
        history.Add(AssistantMessage("Hello"));
        history.AppendOrGlueAssistant("world");

        Assert.Equal(1, history.Count);
        Assert.Equal("Hello\n\nworld", history.Snapshot()[0].Content);
    }

    [Fact]
    public void AppendOrGlueAssistant_DoesNotMergeAcrossToolCallAssistant()
    {
        var history = new ConversationHistory();
        // Add an assistant message WITH tool calls — should not be glued.
        history.Add(new LlmMessage
        {
            Role = "assistant",
            Content = "I'll call a tool.",
            ToolCalls = [new LlmToolCall { Id = "tc1", Name = "read_file", Arguments = "{}" }],
        });
        history.AppendOrGlueAssistant("Done.");

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void AppendOrGlueAssistant_AddsNewMessageWhenHistoryEmpty()
    {
        var history = new ConversationHistory();
        history.AppendOrGlueAssistant("Hello");

        Assert.Equal(1, history.Count);
        Assert.Equal("Hello", history.Snapshot()[0].Content);
    }

    [Fact]
    public void AppendOrGlueAssistant_AddsNewMessageWhenLastIsUser()
    {
        var history = new ConversationHistory();
        history.Add(UserMessage("hi"));
        history.AppendOrGlueAssistant("Hello");

        Assert.Equal(2, history.Count);
    }

    // ── ReplaceOrAppendTrailingAssistant ──────────────────────────────────

    [Fact]
    public void ReplaceOrAppendTrailingAssistant_ReplacesLastPlainAssistant()
    {
        var history = new ConversationHistory();
        history.Add(AssistantMessage("original"));
        history.ReplaceOrAppendTrailingAssistant("replaced");

        Assert.Equal(1, history.Count);
        Assert.Equal("replaced", history.Snapshot()[0].Content);
    }

    [Fact]
    public void ReplaceOrAppendTrailingAssistant_AppendsWhenLastIsUser()
    {
        var history = new ConversationHistory();
        history.Add(UserMessage("hi"));
        history.ReplaceOrAppendTrailingAssistant("new");

        Assert.Equal(2, history.Count);
        Assert.Equal("new", history.Snapshot()[1].Content);
    }

    [Fact]
    public void ReplaceOrAppendTrailingAssistant_AppendsWhenLastHasToolCalls()
    {
        var history = new ConversationHistory();
        history.Add(new LlmMessage
        {
            Role = "assistant",
            Content = "Calling tool",
            ToolCalls = [new LlmToolCall { Id = "tc1", Name = "tool", Arguments = "{}" }],
        });
        history.ReplaceOrAppendTrailingAssistant("after tool");

        Assert.Equal(2, history.Count);
    }

    // ── Snapshot ─────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_ReturnsCopy_NotSameReference()
    {
        var history = new ConversationHistory();
        history.Add(UserMessage("hello"));

        var snap1 = history.Snapshot();
        history.Add(UserMessage("world"));
        var snap2 = history.Snapshot();

        Assert.Single(snap1);
        Assert.Equal(2, snap2.Count);
    }

    // ── GetChatHistory ────────────────────────────────────────────────────

    [Fact]
    public void GetChatHistory_ExcludesInternalMessages()
    {
        var history = new ConversationHistory();
        history.Add(UserMessage("hello"));
        history.Add(new LlmMessage
        {
            Role = "user",
            Content = "scheduled task",
            MessageType = LlmMessageType.ScheduledTaskInstruction,
        });

        var chat = history.GetChatHistory("conv-1", 10);

        Assert.Single(chat);
        Assert.Equal("hello", chat[0].Text);
    }

    [Fact]
    public void GetChatHistory_TakesLastN()
    {
        var history = new ConversationHistory();
        for (var i = 0; i < 5; i++)
        {
            history.Add(UserMessage($"msg-{i}"));
        }

        var chat = history.GetChatHistory("conv-1", 3);

        Assert.Equal(3, chat.Count);
        Assert.Equal("msg-2", chat[0].Text);
        Assert.Equal("msg-4", chat[2].Text);
    }

    // ── Trim ─────────────────────────────────────────────────────────────

    [Fact]
    public void Trim_KeepsSystemMessages()
    {
        var history = new ConversationHistory();
        history.Add(SystemMessage("You are helpful."));
        for (var i = 0; i < 5; i++)
        {
            history.Add(UserMessage($"msg-{i}"));
        }

        history.Trim(3); // system + 2 non-system

        var snap = history.Snapshot();
        Assert.Equal("system", snap[0].Role);
    }

    [Fact]
    public void Trim_DropsOldestNonSystemMessages()
    {
        var history = new ConversationHistory();
        history.Add(SystemMessage("sys"));
        history.Add(UserMessage("old-1"));
        history.Add(UserMessage("old-2"));
        history.Add(UserMessage("keep-1"));
        history.Add(UserMessage("keep-2"));

        history.Trim(3); // system + 2 non-system → keeps last 2

        var snap = history.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.Equal("keep-1", snap[1].Content);
        Assert.Equal("keep-2", snap[2].Content);
    }

    [Fact]
    public void Trim_DoesNotOrphanLeadingToolMessages()
    {
        var history = new ConversationHistory();
        history.Add(SystemMessage("sys"));
        history.Add(UserMessage("user"));
        // Tool-call assistant + matching tool result — would be orphaned if we start at tool
        history.Add(new LlmMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls = [new LlmToolCall { Id = "tc1", Name = "tool", Arguments = "{}" }],
        });
        history.Add(new LlmMessage { Role = "tool", Content = "result", ToolCallId = "tc1" });
        history.Add(AssistantMessage("final"));

        // Trim to 3: sys + 2; startIndex would land on the tool message — must skip past it.
        history.Trim(3);

        var snap = history.Snapshot();
        // No orphan tool message at the start (after system).
        Assert.All(snap.Skip(1), m => Assert.NotEqual("tool", m.Role));
    }

    [Fact]
    public void Trim_DoesNothingWhenUnderMax()
    {
        var history = new ConversationHistory();
        history.Add(UserMessage("a"));
        history.Add(UserMessage("b"));

        history.Trim(5);

        Assert.Equal(2, history.Count);
    }

    // ── Clear ─────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_RemovesAllMessages()
    {
        var history = new ConversationHistory();
        history.Add(UserMessage("a"));
        history.Add(UserMessage("b"));

        history.Clear();

        Assert.Equal(0, history.Count);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static LlmMessage UserMessage(string content) =>
        new() { Role = "user", Content = content };

    private static LlmMessage AssistantMessage(string content) =>
        new() { Role = "assistant", Content = content };

    private static LlmMessage SystemMessage(string content) =>
        new() { Role = "system", Content = content };
}
