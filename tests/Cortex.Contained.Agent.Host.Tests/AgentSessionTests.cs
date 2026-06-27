using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class AgentSessionTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var session = new AgentSession("conv-1");

        Assert.Equal("conv-1", session.ConversationId);
        Assert.Null(session.Title);
        Assert.False(session.IsGenerating);
        Assert.Equal(0, session.MessageCount);
    }

    [Fact]
    public void AddMessage_IncrementsCount()
    {
        var session = new AgentSession("conv-1");

        session.AddMessage(new LlmMessage { Role = "user", Content = "Hello" });

        Assert.Equal(1, session.MessageCount);
    }

    [Fact]
    public void AddMessage_UpdatesLastMessageAt()
    {
        var session = new AgentSession("conv-1");
        var before = session.LastMessageAt;

        // Small delay to ensure timestamp differs
        Thread.Sleep(10);
        session.AddMessage(new LlmMessage { Role = "user", Content = "Hello" });

        Assert.True(session.LastMessageAt >= before);
    }

    [Fact]
    public void GetHistory_ReturnsAllMessages()
    {
        var session = new AgentSession("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Hello" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "Hi there" });

        var history = session.GetHistory();

        Assert.Equal(2, history.Count);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("assistant", history[1].Role);
    }

    [Fact]
    public void GetHistory_ReturnsCopy()
    {
        var session = new AgentSession("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Hello" });

        var history1 = session.GetHistory();
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "Hi" });
        var history2 = session.GetHistory();

        // Original copy should not be affected
        Assert.Single(history1);
        Assert.Equal(2, history2.Count);
    }

    [Fact]
    public void GetChatHistory_RespectsLimit()
    {
        var session = new AgentSession("conv-1");
        for (var i = 0; i < 10; i++)
        {
            session.AddMessage(new LlmMessage { Role = "user", Content = $"msg-{i}" });
        }

        var chatHistory = session.GetChatHistory(3);

        Assert.Equal(3, chatHistory.Count);
    }

    [Fact]
    public void BeginGeneration_SetsIsGenerating()
    {
        var session = new AgentSession("conv-1");

        session.BeginGeneration(CancellationToken.None);

        Assert.True(session.IsGenerating);
    }

    [Fact]
    public void EndGeneration_ClearsIsGenerating()
    {
        var session = new AgentSession("conv-1");
        session.BeginGeneration(CancellationToken.None);

        session.EndGeneration();

        Assert.False(session.IsGenerating);
    }

    [Fact]
    public void BeginGeneration_ReturnsLinkedToken()
    {
        using var cts = new CancellationTokenSource();
        var session = new AgentSession("conv-1");

        var token = session.BeginGeneration(cts.Token);

        Assert.False(token.IsCancellationRequested);
        cts.Cancel();
        Assert.True(token.IsCancellationRequested);

        session.EndGeneration();
    }

    [Fact]
    public void AbortGeneration_CancelsToken()
    {
        var session = new AgentSession("conv-1");
        var token = session.BeginGeneration(CancellationToken.None);

        session.AbortGeneration();

        Assert.True(token.IsCancellationRequested);
        session.EndGeneration();
    }

    [Fact]
    public void TrimHistory_KeepsLastMessages()
    {
        var session = new AgentSession("conv-1");
        for (var i = 0; i < 20; i++)
        {
            session.AddMessage(new LlmMessage { Role = "user", Content = $"msg-{i}" });
        }

        session.TrimHistory(10);

        Assert.Equal(10, session.MessageCount);
        var history = session.GetHistory();
        Assert.Equal("msg-10", history[0].Content);
    }

    [Fact]
    public void TrimHistory_WhenUnderLimit_DoesNothing()
    {
        var session = new AgentSession("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "msg-1" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "msg-2" });

        session.TrimHistory(100);

        Assert.Equal(2, session.MessageCount);
    }

    [Fact]
    public void TrimHistory_DoesNotOrphanToolResults()
    {
        var session = new AgentSession("conv-1");

        // Add some old messages
        session.AddMessage(new LlmMessage { Role = "user", Content = "old-1" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "old-2" });

        // Add a tool-call group: assistant with ToolCalls + 2 tool results
        session.AddMessage(new LlmMessage
        {
            Role = "assistant",
            Content = "Let me check that",
            ToolCalls = [new LlmToolCall { Id = "call-1", Name = "search", Arguments = "{}" },
                         new LlmToolCall { Id = "call-2", Name = "read", Arguments = "{}" }],
        });
        session.AddMessage(new LlmMessage { Role = "tool", Content = "result-1", ToolCallId = "call-1" });
        session.AddMessage(new LlmMessage { Role = "tool", Content = "result-2", ToolCallId = "call-2" });

        // Add a final assistant message
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "Here is the answer" });

        // Trim to 5 — naive trim would cut between assistant-with-tools and tool results
        session.TrimHistory(5);

        var history = session.GetHistory();

        // Verify no orphaned tool results: every tool message must have a preceding
        // assistant message with matching ToolCalls
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role == "tool")
            {
                // There must be a preceding assistant message with ToolCalls
                var hasPrecedingToolUse = false;
                for (var j = i - 1; j >= 0; j--)
                {
                    if (history[j].Role == "assistant" && history[j].ToolCalls is { Count: > 0 })
                    {
                        hasPrecedingToolUse = true;
                        break;
                    }

                    if (history[j].Role != "tool") break; // stop at non-tool boundary
                }

                Assert.True(hasPrecedingToolUse,
                    $"Tool result at index {i} (ToolCallId={history[i].ToolCallId}) has no matching tool_use");
            }
        }
    }

    [Fact]
    public void TrimHistory_DropsBothToolUseAndResults_WhenGroupExceedsLimit()
    {
        var session = new AgentSession("conv-1");

        // Tool-call group (3 messages)
        session.AddMessage(new LlmMessage
        {
            Role = "assistant",
            ToolCalls = [new LlmToolCall { Id = "call-1", Name = "search", Arguments = "{}" }],
        });
        session.AddMessage(new LlmMessage { Role = "tool", Content = "result-1", ToolCallId = "call-1" });

        // Then normal messages
        session.AddMessage(new LlmMessage { Role = "user", Content = "Thanks" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "You're welcome" });

        // Trim to 3 — the tool group (2 msgs) + the 2 normal msgs = 4 total
        // Should drop the tool group entirely rather than orphan tool results
        session.TrimHistory(3);

        var history = session.GetHistory();
        Assert.DoesNotContain(history, m => m.Role == "tool");
    }

    [Fact]
    public void ClearHistory_RemovesAllMessages()
    {
        var session = new AgentSession("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Hello" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "Hi" });

        session.ClearHistory();

        Assert.Equal(0, session.MessageCount);
        Assert.Empty(session.GetHistory());
    }

    [Fact]
    public void ToConversationInfo_ReturnsSnapshot()
    {
        var session = new AgentSession("conv-1");
        session.Title = "Test Title";
        session.AddMessage(new LlmMessage { Role = "user", Content = "Hello" });

        var info = session.ToConversationInfo();

        Assert.Equal("conv-1", info.ConversationId);
        Assert.Equal("Test Title", info.Title);
        Assert.Equal(1, info.MessageCount);
    }
}

public class AgentSessionStoreTests
{
    private static AgentSessionStore CreateStore(SessionConfig? config = null, MemorySettingsStore? settingsStore = null)
    {
        return new AgentSessionStore(
            config ?? new SessionConfig(),
            settingsStore ?? new MemorySettingsStore(),
            NullLogger<AgentSessionStore>.Instance);
    }

    [Fact]
    public void GetOrCreate_CreatesNewSession()
    {
        var store = CreateStore();

        var session = store.GetOrCreate("conv-1");

        Assert.NotNull(session);
        Assert.Equal("conv-1", session.ConversationId);
    }

    [Fact]
    public void GetOrCreate_ReturnsSameSession()
    {
        var store = CreateStore();
        var session1 = store.GetOrCreate("conv-1");
        var session2 = store.GetOrCreate("conv-1");

        Assert.Same(session1, session2);
    }

    [Fact]
    public void TryGet_ExistingSession_ReturnsTrue()
    {
        var store = CreateStore();
        store.GetOrCreate("conv-1");

        var found = store.TryGet("conv-1", out var session);

        Assert.True(found);
        Assert.NotNull(session);
    }

    [Fact]
    public void TryGet_NonExistentSession_ReturnsFalse()
    {
        var store = CreateStore();

        var found = store.TryGet("conv-999", out _);

        Assert.False(found);
    }

    [Fact]
    public void Remove_ExistingSession_ReturnsTrue()
    {
        var store = CreateStore();
        store.GetOrCreate("conv-1");

        var removed = store.Remove("conv-1");

        Assert.True(removed);
        Assert.False(store.TryGet("conv-1", out _));
    }

    [Fact]
    public void Remove_NonExistentSession_ReturnsFalse()
    {
        var store = CreateStore();

        var removed = store.Remove("conv-999");

        Assert.False(removed);
    }

    [Fact]
    public void GetAll_ReturnsAllSessions()
    {
        var store = CreateStore();
        store.GetOrCreate("conv-1");
        store.GetOrCreate("conv-2");
        store.GetOrCreate("conv-3");

        var all = store.GetAll();

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void GetAll_EmptyStore_ReturnsEmpty()
    {
        var store = CreateStore();

        var all = store.GetAll();

        Assert.Empty(all);
    }

    [Fact]
    public void Reset_ClearsSessionHistory()
    {
        var store = CreateStore();
        var session = store.GetOrCreate("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Hello" });
        store.GetOrCreate("conv-2").AddMessage(new LlmMessage { Role = "user", Content = "World" });

        store.Reset("conv-1");

        // conv-1 should have its history cleared
        Assert.True(store.TryGet("conv-1", out var cleared));
        Assert.Equal(0, cleared!.MessageCount);
        // conv-2 should still have its message
        Assert.True(store.TryGet("conv-2", out var remaining));
        Assert.Equal(1, remaining!.MessageCount);
    }

    [Fact]
    public void Reset_NonExistent_DoesNotThrow()
    {
        var store = CreateStore();

        store.Reset("nonexistent");

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void ResetAll_ClearsAllSessionHistories()
    {
        var store = CreateStore();
        store.GetOrCreate("conv-1").AddMessage(new LlmMessage { Role = "user", Content = "Hello" });
        store.GetOrCreate("conv-2").AddMessage(new LlmMessage { Role = "user", Content = "World" });

        store.ResetAll();

        // Sessions still exist but histories are cleared
        Assert.Equal(2, store.GetAll().Count);
        foreach (var session in store.GetAll())
        {
            Assert.Equal(0, session.MessageCount);
        }
    }

    [Fact]
    public void Seed_PopulatesSessionHistory()
    {
        var store = CreateStore();
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there" },
            new() { Role = "user", Content = "How are you?" },
        };

        store.Seed("conv-seed", messages, maxHistory: 100);

        Assert.True(store.TryGet("conv-seed", out var session));
        Assert.Equal(3, session!.MessageCount);
        var history = session.GetHistory();
        Assert.Equal("Hello", history[0].Content);
        Assert.Equal("Hi there", history[1].Content);
        Assert.Equal("How are you?", history[2].Content);
    }

    [Fact]
    public void Seed_RespectsMaxHistory()
    {
        var store = CreateStore();
        var messages = new List<LlmMessage>();
        for (var i = 0; i < 10; i++)
        {
            messages.Add(new LlmMessage { Role = "user", Content = $"msg-{i}" });
        }

        store.Seed("conv-seed", messages, maxHistory: 3);

        Assert.True(store.TryGet("conv-seed", out var session));
        Assert.Equal(3, session!.MessageCount);
        var history = session.GetHistory();
        Assert.Equal("msg-7", history[0].Content);
    }

    [Fact]
    public void Seed_ReplacesExistingSession()
    {
        var store = CreateStore();
        var session = store.GetOrCreate("conv-seed");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Old message" });

        var newMessages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "New message" },
        };

        store.Seed("conv-seed", newMessages, maxHistory: 100);

        Assert.True(store.TryGet("conv-seed", out var updated));
        Assert.Equal(1, updated!.MessageCount);
        Assert.Equal("New message", updated.GetHistory()[0].Content);
    }

    [Fact]
    public void GetOrCreateWithIdleCheck_CreatesNewSession()
    {
        var store = CreateStore();

        var session = store.GetOrCreateWithIdleCheck("conv-1");

        Assert.NotNull(session);
        Assert.Equal("conv-1", session.ConversationId);
    }

    [Fact]
    public void GetOrCreateWithIdleCheck_ReturnsSameSession_WhenInMemory()
    {
        var store = CreateStore();
        var session1 = store.GetOrCreateWithIdleCheck("conv-1");
        var session2 = store.GetOrCreateWithIdleCheck("conv-1");

        Assert.Same(session1, session2);
    }

    [Fact]
    public void IdleReset_DoesNotClearHistory_WhenDisabled()
    {
        var config = new SessionConfig { IdleResetMinutes = 0 }; // 0 means disabled
        var store = CreateStore(config);

        var session = store.GetOrCreateWithIdleCheck("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Hello" });

        // With IdleResetMinutes = 0 (disabled), history should NOT be cleared
        var retrieved = store.GetOrCreateWithIdleCheck("conv-1");
        Assert.Equal(1, retrieved.MessageCount);
    }

    [Fact]
    public void IdleReset_FlagsForCompaction_WhenIdleCompactionEnabled()
    {
        // Use a very short idle timeout so the session is immediately idle
        var config = new SessionConfig { IdleResetMinutes = 1 };
        var settingsStore = new MemorySettingsStore();
        settingsStore.Update(null, null, null, idleCompactionEnabled: true, idleResetMinutes: 1);
        var store = CreateStore(config, settingsStore);

        var session = store.GetOrCreateWithIdleCheck("conv-1");
        // Add enough messages to qualify for compaction (>= 6)
        for (var i = 0; i < 8; i++)
        {
            session.AddMessage(new LlmMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = $"Message {i}" });
        }

        // Simulate idle time by setting LastMessageAt far in the past via reflection
        var lastMsgProp = typeof(AgentSession).GetProperty("LastMessageAt")!;
        lastMsgProp.SetValue(session, DateTimeOffset.UtcNow.AddMinutes(-10));

        // Retrieve with idle check — should flag for compaction, NOT wipe
        var retrieved = store.GetOrCreateWithIdleCheck("conv-1");
        Assert.True(retrieved.NeedsIdleCompaction);
        Assert.Equal(8, retrieved.MessageCount); // Messages preserved, not wiped
    }

    [Fact]
    public void IdleReset_WipesHistory_WhenIdleCompactionDisabled()
    {
        var config = new SessionConfig { IdleResetMinutes = 1 };
        var settingsStore = new MemorySettingsStore();
        settingsStore.Update(null, null, null, idleCompactionEnabled: false, idleResetMinutes: 1);
        var store = CreateStore(config, settingsStore);

        var session = store.GetOrCreateWithIdleCheck("conv-1");
        for (var i = 0; i < 8; i++)
        {
            session.AddMessage(new LlmMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = $"Message {i}" });
        }

        var lastMsgProp = typeof(AgentSession).GetProperty("LastMessageAt")!;
        lastMsgProp.SetValue(session, DateTimeOffset.UtcNow.AddMinutes(-10));

        var retrieved = store.GetOrCreateWithIdleCheck("conv-1");
        Assert.False(retrieved.NeedsIdleCompaction);
        Assert.Equal(0, retrieved.MessageCount); // History wiped (original behavior)
    }

    [Fact]
    public void IdleReset_WipesHistory_WhenTooFewMessagesForCompaction()
    {
        var config = new SessionConfig { IdleResetMinutes = 1 };
        var settingsStore = new MemorySettingsStore();
        settingsStore.Update(null, null, null, idleCompactionEnabled: true, idleResetMinutes: 1);
        var store = CreateStore(config, settingsStore);

        var session = store.GetOrCreateWithIdleCheck("conv-1");
        // Only 2 messages — below the 6-message threshold for compaction
        session.AddMessage(new LlmMessage { Role = "user", Content = "Hello" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "Hi" });

        var lastMsgProp = typeof(AgentSession).GetProperty("LastMessageAt")!;
        lastMsgProp.SetValue(session, DateTimeOffset.UtcNow.AddMinutes(-10));

        var retrieved = store.GetOrCreateWithIdleCheck("conv-1");
        Assert.False(retrieved.NeedsIdleCompaction);
        Assert.Equal(0, retrieved.MessageCount); // Too few messages, falls back to wipe
    }

    [Fact]
    public void IdleReset_UsesRuntimeOverride_ForIdleMinutes()
    {
        // Config says 60 minutes, but runtime override says 1 minute
        var config = new SessionConfig { IdleResetMinutes = 60 };
        var settingsStore = new MemorySettingsStore();
        settingsStore.Update(null, null, null, idleCompactionEnabled: true, idleResetMinutes: 1);
        var store = CreateStore(config, settingsStore);

        var session = store.GetOrCreateWithIdleCheck("conv-1");
        for (var i = 0; i < 8; i++)
        {
            session.AddMessage(new LlmMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = $"Message {i}" });
        }

        // Session idle for 5 minutes — exceeds the 1-minute runtime override
        var lastMsgProp = typeof(AgentSession).GetProperty("LastMessageAt")!;
        lastMsgProp.SetValue(session, DateTimeOffset.UtcNow.AddMinutes(-5));

        var retrieved = store.GetOrCreateWithIdleCheck("conv-1");
        Assert.True(retrieved.NeedsIdleCompaction); // Triggered by runtime override (1 min), not config (60 min)
    }

    // ── AppendOrGlueAssistantMessage ─────────────────────────────────────

    [Fact]
    public void AppendOrGlueAssistantMessage_EmptyHistory_AddsNewMessage()
    {
        var session = new AgentSession("conv-1");

        session.AppendOrGlueAssistantMessage("Hello from background task.");

        var history = session.GetHistory();
        Assert.Single(history);
        Assert.Equal("assistant", history[0].Role);
        Assert.Equal("Hello from background task.", history[0].Content);
        Assert.Equal(LlmMessageType.Proactive, history[0].MessageType);
    }

    [Fact]
    public void AppendOrGlueAssistantMessage_LastIsUser_AddsNewMessage()
    {
        var session = new AgentSession("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "What's up?" });

        session.AppendOrGlueAssistantMessage("Background task done.");

        var history = session.GetHistory();
        Assert.Equal(2, history.Count);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("assistant", history[1].Role);
        Assert.Equal("Background task done.", history[1].Content);
    }

    [Fact]
    public void AppendOrGlueAssistantMessage_LastIsAssistant_GluesContent()
    {
        var session = new AgentSession("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Do something" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "On it!" });

        session.AppendOrGlueAssistantMessage("The task is done now.");

        var history = session.GetHistory();
        Assert.Equal(2, history.Count);
        // Glued as a natural phrase — no "---" separator block (feedback 2026-05-15).
        Assert.Equal("On it!\n\nThe task is done now.", history[1].Content);
        Assert.DoesNotContain("---", history[1].Content!);
    }

    [Fact]
    public void AppendOrGlue_TimerMessageAfterReply_ReadsAsNaturalPhrase_NoDashSeparator()
    {
        // Regression: proactive/timer messages must read as a natural phrase in
        // history — no "---" separator block above them (user feedback 2026-05-15).
        var session = new AgentSession("discord-voice");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Start a 60 second timer" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "Timer started for 60 seconds." });

        session.AppendOrGlueAssistantMessage("Time's up! Great work — ready for the next set?");

        var content = session.GetHistory()[^1].Content!;
        Assert.Equal(
            "Timer started for 60 seconds.\n\nTime's up! Great work — ready for the next set?",
            content);
        Assert.DoesNotContain("---", content);
    }

    [Fact]
    public void AppendOrGlueAssistantMessage_LastIsAssistantWithToolCalls_AddsNewMessage()
    {
        var session = new AgentSession("conv-1");
        session.AddMessage(new LlmMessage
        {
            Role = "assistant",
            Content = "Let me check...",
            ToolCalls = [new LlmToolCall { Id = "call_1", Name = "grep", Arguments = "{}" }],
        });
        session.AddMessage(new LlmMessage { Role = "tool", Content = "results", ToolCallId = "call_1" });

        session.AppendOrGlueAssistantMessage("Background result.");

        var history = session.GetHistory();
        Assert.Equal(3, history.Count);
        Assert.Equal("Background result.", history[2].Content);
    }

    [Fact]
    public void AppendOrGlueAssistantMessage_MultipleGlues_AccumulatesContent()
    {
        var session = new AgentSession("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Go" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "First." });

        session.AppendOrGlueAssistantMessage("Second.");
        session.AppendOrGlueAssistantMessage("Third.");

        var history = session.GetHistory();
        Assert.Equal(2, history.Count);
        Assert.Contains("First.", history[1].Content!);
        Assert.Contains("Second.", history[1].Content!);
        Assert.Contains("Third.", history[1].Content!);
    }

    // ── Proactive message gluing scenarios ────────────────────────────────

    [Fact]
    public void AppendOrGlue_ProactiveAfterNormalResponse_Glues()
    {
        // Scenario: agent responds to user, then scheduled task sends proactive to same channel.
        // The proactive message should glue onto the agent's response.
        var session = new AgentSession("discord-dm");
        session.AddMessage(new LlmMessage { Role = "user", Content = "What's up?" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "Not much!" });

        session.AppendOrGlueAssistantMessage("Weather update: it's sunny.");

        var history = session.GetHistory();
        Assert.Equal(2, history.Count); // user + merged assistant
        Assert.Contains("Not much!", history[1].Content!);
        Assert.Contains("Weather update: it's sunny.", history[1].Content!);
    }

    [Fact]
    public void AppendOrGlue_ProactiveOnIdleSession_AddsNew()
    {
        // Scenario: proactive message arrives on a session with no history.
        var session = new AgentSession("discord-dm");

        session.AppendOrGlueAssistantMessage("Good morning! Here's your weather.");

        var history = session.GetHistory();
        Assert.Single(history);
        Assert.Equal("assistant", history[0].Role);
        Assert.Equal(LlmMessageType.Proactive, history[0].MessageType);
    }

    [Fact]
    public void AppendOrGlue_UserMessageBetweenProactives_SeparateMessages()
    {
        // Scenario: proactive, then user responds, then another proactive.
        // The second proactive should NOT glue to the first (user message in between).
        var session = new AgentSession("discord-dm");
        session.AppendOrGlueAssistantMessage("Reminder: gym in 1 hour!");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Thanks!" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "You're welcome!" });

        session.AppendOrGlueAssistantMessage("Weather update: rain expected.");

        var history = session.GetHistory();
        Assert.Equal(3, history.Count); // proactive + user + merged(assistant + proactive)
        Assert.Contains("Reminder: gym", history[0].Content!);
        Assert.Equal("user", history[1].Role);
        Assert.Contains("You're welcome!", history[2].Content!);
        Assert.Contains("Weather update: rain", history[2].Content!); // glued to "You're welcome!"
    }

    [Fact]
    public void AppendOrGlue_MultipleProactivesNoUserMessage_AllGlued()
    {
        // Scenario: multiple scheduled tasks fire, each sends proactive to same channel.
        var session = new AgentSession("discord-dm");

        session.AppendOrGlueAssistantMessage("Weather: sunny, 18°C.");
        session.AppendOrGlueAssistantMessage("Gym reminder: legs day!");
        session.AppendOrGlueAssistantMessage("News: new Claude model released.");

        var history = session.GetHistory();
        Assert.Single(history); // all glued into one
        Assert.Contains("Weather", history[0].Content!);
        Assert.Contains("Gym reminder", history[0].Content!);
        Assert.Contains("News", history[0].Content!);
    }

    [Fact]
    public void AppendOrGlue_BackgroundTaskCompletion_GluesToExistingResponse()
    {
        // Scenario: agent tells user "I started the research", then subagent completes,
        // main agent processes and responds — glues to the earlier response.
        var session = new AgentSession("discord-dm");
        session.AddMessage(new LlmMessage { Role = "user", Content = "Research A2A protocol" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "On it! I've started a background task." });

        // Subagent completion processed by main agent — response glued
        session.AppendOrGlueAssistantMessage("Research complete! Here's a summary of the A2A protocol...");

        var history = session.GetHistory();
        Assert.Equal(2, history.Count); // user + merged assistant
        Assert.Contains("On it!", history[1].Content!);
        Assert.Contains("Research complete!", history[1].Content!);
    }

    [Fact]
    public void AppendOrGlue_AfterToolCallResponse_AddsNewMessage()
    {
        // Scenario: last message is assistant with tool_calls — proactive should NOT glue
        // (tool_calls messages are special, gluing would corrupt them).
        var session = new AgentSession("discord-dm");
        session.AddMessage(new LlmMessage
        {
            Role = "assistant",
            Content = "Let me check...",
            ToolCalls = [new LlmToolCall { Id = "call_1", Name = "date_time", Arguments = "{}" }],
        });
        session.AddMessage(new LlmMessage { Role = "tool", Content = "2026-03-26", ToolCallId = "call_1" });

        session.AppendOrGlueAssistantMessage("By the way, your task completed.");

        var history = session.GetHistory();
        Assert.Equal(3, history.Count); // assistant+tool_call, tool, new assistant
        Assert.Equal("By the way, your task completed.", history[2].Content);
    }

    [Fact]
    public void AppendOrGlue_CrossChannelProactive_IsolatedSessions()
    {
        // Scenario: scheduled task sends proactive to discord-dm, but conversation
        // is on webchat. Each channel has its own session — no cross-contamination.
        var webchatSession = new AgentSession("webchat-default");
        var discordSession = new AgentSession("discord-dm");

        webchatSession.AddMessage(new LlmMessage { Role = "user", Content = "Hello" });
        webchatSession.AddMessage(new LlmMessage { Role = "assistant", Content = "Hi!" });

        // Proactive goes to Discord session, not webchat
        discordSession.AppendOrGlueAssistantMessage("Weather update for Discord.");

        Assert.Equal(2, webchatSession.GetHistory().Count); // unchanged
        Assert.Single(discordSession.GetHistory()); // new proactive
        Assert.Contains("Weather update", discordSession.GetHistory()[0].Content!);
    }

    // ── barge-in interruption state ───────────────────────────────────

    [Fact]
    public void MarkInterrupted_SetsPlayedText()
    {
        var s = new AgentSession("c1");
        Assert.Null(s.InterruptedPlayedText);

        s.MarkInterrupted("Sure. There was an engineer…");

        Assert.Equal("Sure. There was an engineer…", s.InterruptedPlayedText);
    }

    [Fact]
    public void ClearInterruption_ClearsText_ButRetainsRecordIdForSecondBargeIn()
    {
        var s = new AgentSession("c1");
        s.MarkInterrupted("played…");
        s.SetLastAssistantRecordId(42);

        Assert.Equal(42, s.LastAssistantRecordId);

        s.ClearInterruption();

        Assert.Null(s.InterruptedPlayedText);
        // Retained so a SECOND barge-in on the same turn can still update the
        // row; only BeginGeneration (next turn) zeroes it.
        Assert.Equal(42, s.LastAssistantRecordId);
    }

    [Fact]
    public void BeginGeneration_ClearsStaleInterruption()
    {
        var s = new AgentSession("c1");
        s.MarkInterrupted("stale played…");
        s.SetLastAssistantRecordId(7);

        s.BeginGeneration(CancellationToken.None);

        Assert.Null(s.InterruptedPlayedText);
        Assert.Equal(0, s.LastAssistantRecordId);
    }
}
