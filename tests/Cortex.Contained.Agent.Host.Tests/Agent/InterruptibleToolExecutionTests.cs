using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public class InterruptibleToolExecutionTests
{
    [Fact]
    public void DrainPendingMessages_InjectsIntoHistory_BetweenToolRounds()
    {
        // Arrange: session with some history, then enqueue a pending message
        var session = new AgentSession("test");
        session.AddMessage(new LlmMessage { Role = "user", Content = "do something" });
        session.AddMessage(new LlmMessage
        {
            Role = "assistant",
            Content = "calling tool",
            ToolCalls = [new LlmToolCall { Id = "t1", Name = "file_read", Arguments = "{}" }],
        });
        session.AddMessage(new LlmMessage { Role = "tool", Content = "file contents", ToolCallId = "t1" });

        // Simulate user sending message during tool execution
        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "test",
            ChannelId = "test",
            Text = "stop, try something else",
            Source = AgentMessageSource.User,
        });

        // Act: drain (this is what AgentRuntime does between tool rounds)
        var pending = session.DrainPendingMessages();

        // Inject into history (simulating what AgentRuntime does)
        foreach (var msg in pending)
        {
            session.AddMessage(new LlmMessage { Role = "user", Content = msg.Text });
        }

        // Assert: history now has the injected user message after the tool result
        var history = session.GetHistory();
        Assert.Equal(4, history.Count);
        Assert.Equal("tool", history[2].Role);
        Assert.Equal("user", history[3].Role);
        Assert.Equal("stop, try something else", history[3].Content);
    }

    [Fact]
    public void MultiplePendingMessages_AllDrainedInOrder()
    {
        var session = new AgentSession("test");

        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "test",
            ChannelId = "test",
            Text = "first",
            Source = AgentMessageSource.User,
        });
        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "test",
            ChannelId = "test",
            Text = "second",
            Source = AgentMessageSource.User,
        });
        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "test",
            ChannelId = "test",
            Text = "third",
            Source = AgentMessageSource.User,
        });

        var drained = session.DrainPendingMessages();
        Assert.Equal(3, drained.Count);
        Assert.Equal("first", drained[0].Text);
        Assert.Equal("second", drained[1].Text);
        Assert.Equal("third", drained[2].Text);

        // Queue is empty after drain
        Assert.Equal(0, session.PendingMessageCount);
    }

    [Fact]
    public void ScheduledTaskSource_GetsPrefixAndInternalType()
    {
        var session = new AgentSession("test");
        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "test",
            ChannelId = "test",
            Text = "run daily report",
            Source = AgentMessageSource.ScheduledTask,
        });

        var pending = session.DrainPendingMessages();
        var msg = pending[0];

        // Simulate what AgentRuntime does in the drain path
        var userText = msg.Text;
        if (msg.Source == AgentMessageSource.ScheduledTask)
        {
            userText = $"[Scheduled Task] {userText}";
        }

        var messageType = msg.Source == AgentMessageSource.User
            ? LlmMessageType.Normal
            : LlmMessageType.ScheduledTaskInstruction;

        var llmMessage = new LlmMessage
        {
            Role = "user",
            Content = userText,
            MessageType = messageType,
        };

        Assert.Equal("[Scheduled Task] run daily report", llmMessage.Content);
        Assert.Equal(LlmMessageType.ScheduledTaskInstruction, llmMessage.MessageType);
        Assert.True(llmMessage.IsInternal);
    }

    [Fact]
    public void SubagentCompletionSource_GetsPrefixAndInternalType()
    {
        var session = new AgentSession("test");
        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "test",
            ChannelId = "test",
            Text = "Task completed: found 3 issues",
            Source = AgentMessageSource.SubagentCompletion,
        });

        var pending = session.DrainPendingMessages();
        var msg = pending[0];

        var userText = msg.Text;
        if (msg.Source == AgentMessageSource.SubagentCompletion)
        {
            userText = $"[Background Task Completed] {userText}";
        }

        var messageType = msg.Source == AgentMessageSource.User
            ? LlmMessageType.Normal
            : LlmMessageType.ScheduledTaskInstruction;

        var llmMessage = new LlmMessage
        {
            Role = "user",
            Content = userText,
            MessageType = messageType,
        };

        Assert.Equal("[Background Task Completed] Task completed: found 3 issues", llmMessage.Content);
        Assert.True(llmMessage.IsInternal);
    }

    [Fact]
    public void UserSource_NoPrefixAndNotInternal()
    {
        var session = new AgentSession("test");
        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "test",
            ChannelId = "test",
            Text = "hello",
            Source = AgentMessageSource.User,
        });

        var pending = session.DrainPendingMessages();
        var msg = pending[0];

        var userText = msg.Text;
        // No prefix for user messages

        var messageType = msg.Source == AgentMessageSource.User
            ? LlmMessageType.Normal
            : LlmMessageType.ScheduledTaskInstruction;

        var llmMessage = new LlmMessage
        {
            Role = "user",
            Content = userText,
            MessageType = messageType,
        };

        Assert.Equal("hello", llmMessage.Content);
        Assert.Equal(LlmMessageType.Normal, llmMessage.MessageType);
        Assert.False(llmMessage.IsInternal);
    }

    [Fact]
    public void EmptyDrain_NoMessagesInjected()
    {
        var session = new AgentSession("test");
        session.AddMessage(new LlmMessage { Role = "user", Content = "original" });

        // Drain when nothing is pending
        var pending = session.DrainPendingMessages();
        Assert.Empty(pending);

        // History unchanged
        var history = session.GetHistory();
        Assert.Single(history);
        Assert.Equal("original", history[0].Content);
    }

    [Fact]
    public async Task SessionLoop_SubagentCompletionWhileIdle_WakesUp()
    {
        // Simulates: main agent processes user message, then waits.
        // Sub-agent completes later, enqueues completion message.
        // Session loop wakes up and processes it.
        var session = new AgentSession("test");
        var processedMessages = new List<AgentMessage>();

        // Start a session loop simulation
        var loopTask = Task.Run(async () =>
        {
            // Process two rounds: user message, then subagent completion
            for (var i = 0; i < 2; i++)
            {
                await session.WaitForPendingAsync(CancellationToken.None);
                var messages = session.DrainPendingMessages();
                foreach (var msg in messages)
                {
                    processedMessages.Add(msg);
                }
            }
        });

        // Round 1: user message arrives immediately
        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "test", ChannelId = "webchat",
            Text = "start a background task",
            Source = AgentMessageSource.User,
        });

        // Wait for it to be processed
        await Task.Delay(100);
        Assert.Single(processedMessages);
        Assert.Equal("start a background task", processedMessages[0].Text);

        // Round 2: sub-agent completes after a delay (simulating async work)
        await Task.Delay(200);
        session.EnqueuePending(new AgentMessage
        {
            ConversationId = "test", ChannelId = "webchat",
            Text = "Background task result: found 5 items",
            Source = AgentMessageSource.SubagentCompletion,
        });

        // Wait for loop to process
        await loopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, processedMessages.Count);
        Assert.Equal(AgentMessageSource.User, processedMessages[0].Source);
        Assert.Equal(AgentMessageSource.SubagentCompletion, processedMessages[1].Source);
        Assert.Equal("Background task result: found 5 items", processedMessages[1].Text);
    }
}
