using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SessionSnapshotTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AgentSessionStore _sessionStore;

    public SessionSnapshotTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cortex-snapshot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sessionStore = CreateSessionStore();
    }

    private static AgentSessionStore CreateSessionStore() =>
        new(new SessionConfig(), new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveAndRestore_PreservesFullConversation()
    {
        // Arrange — build a session with system, user, assistant, tool call, and tool result
        var session = _sessionStore.GetOrCreate("discord-dm");
        session.Title = "Test conversation";
        session.LastPromptTokens = 5000;
        session.LastCompactionRound = 3;

        session.AddMessage(new LlmMessage { Role = "system", Content = "You are a helpful assistant." });
        session.AddMessage(new LlmMessage { Role = "user", Content = "What's the weather?" });
        session.AddMessage(new LlmMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls =
            [
                new LlmToolCall { Id = "call_1", Name = "get_weather", Arguments = """{"city":"London"}""" },
            ],
        });
        session.AddMessage(new LlmMessage { Role = "tool", Content = "Sunny, 22°C", ToolCallId = "call_1" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "It's sunny and 22°C in London!" });

        // Act — save
        var snapshot = SessionSnapshotSerializer.CaptureSnapshot(_sessionStore);
        await SessionSnapshotSerializer.SaveAsync(snapshot, _tempDir, NullLogger.Instance);

        // Verify file exists
        var filePath = Path.Combine(_tempDir, SessionSnapshotSerializer.SnapshotFileName);
        Assert.True(File.Exists(filePath));

        // Act — restore into a fresh store
        var freshStore = CreateSessionStore();
        var restored = SessionSnapshotSerializer.TryRestore(freshStore, _tempDir, NullLogger.Instance);

        // Assert
        Assert.Equal(1, restored);
        Assert.True(freshStore.TryGet("discord-dm", out var restoredSession));
        Assert.NotNull(restoredSession);

        var history = restoredSession!.GetHistory();
        Assert.Equal(5, history.Count);

        // System message
        Assert.Equal("system", history[0].Role);
        Assert.Equal("You are a helpful assistant.", history[0].Content);

        // User message
        Assert.Equal("user", history[1].Role);

        // Assistant with tool call
        Assert.Equal("assistant", history[2].Role);
        Assert.NotNull(history[2].ToolCalls);
        var toolCall = Assert.Single(history[2].ToolCalls!);
        Assert.Equal("get_weather", history[2].ToolCalls![0].Name);
        Assert.Equal("""{"city":"London"}""", history[2].ToolCalls![0].Arguments);

        // Tool result
        Assert.Equal("tool", history[3].Role);
        Assert.Equal("call_1", history[3].ToolCallId);
        Assert.Equal("Sunny, 22°C", history[3].Content);

        // Final assistant
        Assert.Equal("assistant", history[4].Role);
        Assert.Contains("sunny", history[4].Content!);

        // Metadata
        Assert.Equal("Test conversation", restoredSession.Title);
        Assert.Equal(5000, restoredSession.LastPromptTokens);
        Assert.Equal(3, restoredSession.LastCompactionRound);

        // Snapshot file should be deleted after restore
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task SaveAndRestore_PreservesExtractionBuffer()
    {
        var session = _sessionStore.GetOrCreate("test-channel");
        session.AddMessage(new LlmMessage { Role = "user", Content = "hello" });
        session.AppendToExtractionBuffer(new ExtractionEntry
        {
            Role = "user",
            Content = "hello",
            Timestamp = DateTimeOffset.UtcNow,
        });
        session.AppendToExtractionBuffer(new ExtractionEntry
        {
            Role = "assistant",
            Content = "hi there",
            Timestamp = DateTimeOffset.UtcNow,
        });

        var snapshot = SessionSnapshotSerializer.CaptureSnapshot(_sessionStore);
        await SessionSnapshotSerializer.SaveAsync(snapshot, _tempDir, NullLogger.Instance);

        var freshStore = CreateSessionStore();
        SessionSnapshotSerializer.TryRestore(freshStore, _tempDir, NullLogger.Instance);

        Assert.True(freshStore.TryGet("test-channel", out var restored));
        Assert.Equal(2, restored!.ExtractionBufferCount);
    }

    [Fact]
    public async Task SaveAndRestore_PreservesContentBlocks()
    {
        var session = _sessionStore.GetOrCreate("multimodal");
        session.AddMessage(new LlmMessage
        {
            Role = "user",
            ContentBlocks =
            [
                LlmContentBlock.TextBlock("What's in this image?"),
                LlmContentBlock.ImageBlock("base64data==", "image/png"),
            ],
        });

        var snapshot = SessionSnapshotSerializer.CaptureSnapshot(_sessionStore);
        await SessionSnapshotSerializer.SaveAsync(snapshot, _tempDir, NullLogger.Instance);

        var freshStore = CreateSessionStore();
        SessionSnapshotSerializer.TryRestore(freshStore, _tempDir, NullLogger.Instance);

        Assert.True(freshStore.TryGet("multimodal", out var restored));
        var msg = restored!.GetHistory()[0];
        Assert.NotNull(msg.ContentBlocks);
        Assert.Equal(2, msg.ContentBlocks!.Count);
        Assert.Equal("text", msg.ContentBlocks[0].Type);
        Assert.Equal("image", msg.ContentBlocks[1].Type);
        Assert.Equal("base64data==", msg.ContentBlocks[1].ImageData);
    }

    [Fact]
    public void TryRestore_NoSnapshotFile_ReturnsZero()
    {
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        var result = SessionSnapshotSerializer.TryRestore(_sessionStore, emptyDir, NullLogger.Instance);

        Assert.Equal(0, result);
    }

    [Fact]
    public void TryRestore_CorruptFile_ReturnsZeroAndDeletesFile()
    {
        var filePath = Path.Combine(_tempDir, SessionSnapshotSerializer.SnapshotFileName);
        File.WriteAllText(filePath, "{{{{not valid json}}}}");

        var result = SessionSnapshotSerializer.TryRestore(_sessionStore, _tempDir, NullLogger.Instance);

        Assert.Equal(0, result);
        Assert.False(File.Exists(filePath)); // Corrupt file should be deleted
    }

    [Fact]
    public async Task SaveAndRestore_MultipleSessions()
    {
        var session1 = _sessionStore.GetOrCreate("channel-1");
        session1.AddMessage(new LlmMessage { Role = "user", Content = "Hello from channel 1" });

        var session2 = _sessionStore.GetOrCreate("channel-2");
        session2.AddMessage(new LlmMessage { Role = "user", Content = "Hello from channel 2" });
        session2.AddMessage(new LlmMessage { Role = "assistant", Content = "Hi channel 2!" });

        var snapshot = SessionSnapshotSerializer.CaptureSnapshot(_sessionStore);
        await SessionSnapshotSerializer.SaveAsync(snapshot, _tempDir, NullLogger.Instance);

        var freshStore = CreateSessionStore();
        var restored = SessionSnapshotSerializer.TryRestore(freshStore, _tempDir, NullLogger.Instance);

        Assert.Equal(2, restored);
        Assert.True(freshStore.TryGet("channel-1", out var r1));
        Assert.True(freshStore.TryGet("channel-2", out var r2));
        Assert.Single(r1!.GetHistory());
        Assert.Equal(2, r2!.GetHistory().Count);
    }

    [Fact]
    public void CaptureSnapshot_EmptySessions_SkipsEmpty()
    {
        _sessionStore.GetOrCreate("empty-session"); // No messages added

        var snapshot = SessionSnapshotSerializer.CaptureSnapshot(_sessionStore);

        Assert.Empty(snapshot.Sessions);
    }

    [Fact]
    public async Task SaveAndRestore_PreservesMessageType()
    {
        var session = _sessionStore.GetOrCreate("types-test");
        session.AddMessage(new LlmMessage
        {
            Role = "user",
            Content = "compact",
            MessageType = LlmMessageType.CompactionSummary,
        });
        session.AddMessage(new LlmMessage
        {
            Role = "assistant",
            Content = "Here's the summary...",
            MessageType = LlmMessageType.CompactionSummary,
        });

        var snapshot = SessionSnapshotSerializer.CaptureSnapshot(_sessionStore);
        await SessionSnapshotSerializer.SaveAsync(snapshot, _tempDir, NullLogger.Instance);

        var freshStore = CreateSessionStore();
        SessionSnapshotSerializer.TryRestore(freshStore, _tempDir, NullLogger.Instance);

        Assert.True(freshStore.TryGet("types-test", out var restored));
        var history = restored!.GetHistory();
        Assert.Equal(LlmMessageType.CompactionSummary, history[0].MessageType);
        Assert.Equal(LlmMessageType.CompactionSummary, history[1].MessageType);
    }
}
