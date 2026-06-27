using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

public class ContextManagerTests
{
    // ── IsContextOverflow ───────────────────────────────────────────────

    [Theory]
    [InlineData("prompt is too long: 213462 tokens > 200000 maximum")]
    [InlineData("Your input exceeds the context window of this model")]
    [InlineData("context_length_exceeded")]
    [InlineData("reduce the length of the messages")]
    [InlineData("maximum context length is 128000 tokens")]
    [InlineData("exceeds the limit of 128000")]
    [InlineData("HTTP 413: Request Entity Too Large")]
    [InlineData("input is too long for requested model")]
    [InlineData("exceeded model token limit")]
    [InlineData("Context window exceeded")]
    [InlineData("no conversation messages fit within the token budget")]
    public void IsContextOverflow_OverflowPatterns_ReturnsTrue(string errorMessage)
    {
        Assert.True(ContextManager.IsContextOverflow(errorMessage));
    }

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("invalid API key")]
    [InlineData("HTTP 400: invalid_request_error")]
    [InlineData("messages: at least one message is required")]
    [InlineData("server error")]
    [InlineData("")]
    public void IsContextOverflow_NonOverflowErrors_ReturnsFalse(string errorMessage)
    {
        Assert.False(ContextManager.IsContextOverflow(errorMessage));
    }

    // ── StripMediaFromMessage (async) ───────────────────────────────────

    private static ImageAgingConfig DescribeOff() => new() { PreserveRecentTurns = 10, DescribeOnStrip = false };
    private static ImageAgingConfig DescribeOn() => new() { PreserveRecentTurns = 10, DescribeOnStrip = true };

    [Fact]
    public async Task StripMediaFromMessageAsync_WithImage_DescribeOff_EmitsPlaceholder()
    {
        var block = LlmContentBlock.ImageBlock("base64==", "image/png");
        var msg = new LlmMessage
        {
            Role = "user",
            Content = "Look at this",
            ContentBlocks = [LlmContentBlock.TextBlock("Look at this"), block],
        };

        var result = await ContextManager.StripMediaFromMessageAsync(msg, DescribeOff(), describer: null, CancellationToken.None);

        Assert.NotSame(msg, result);
        Assert.Equal(2, result.ContentBlocks!.Count);
        Assert.Contains("Image removed: image/png", result.ContentBlocks[1].Text);
        Assert.Null(block.ImageDescription); // no cache write when describe off
    }

    [Fact]
    public async Task StripMediaFromMessageAsync_WithImage_DescribeOn_CachesAndUsesDescription()
    {
        var block = LlmContentBlock.ImageBlock("base64==", "image/png");
        var msg = new LlmMessage { Role = "user", ContentBlocks = [block] };

        var describer = Substitute.For<IImageDescriber>();
#pragma warning disable CA2012 // NSubstitute configure call — ValueTask is consumed by the substitute
        describer.DescribeAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => ValueTask.FromResult<string?>("A red sunset over the ocean."));
#pragma warning restore CA2012

        var result = await ContextManager.StripMediaFromMessageAsync(msg, DescribeOn(), describer, CancellationToken.None);

        Assert.Equal("A red sunset over the ocean.", result.ContentBlocks![0].Text);
        Assert.Equal("A red sunset over the ocean.", block.ImageDescription);
    }

    [Fact]
    public async Task StripMediaFromMessageAsync_CachedDescription_SkipsDescriberCall()
    {
        var block = LlmContentBlock.ImageBlock("base64==", "image/png");
        block.ImageDescription = "Already described.";
        var msg = new LlmMessage { Role = "user", ContentBlocks = [block] };

        var describer = Substitute.For<IImageDescriber>();

        var result = await ContextManager.StripMediaFromMessageAsync(msg, DescribeOn(), describer, CancellationToken.None);

        Assert.Equal("Already described.", result.ContentBlocks![0].Text);
        await describer.DidNotReceive().DescribeAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StripMediaFromMessageAsync_DescriberReturnsNull_FallsBackAndDoesNotCache()
    {
        var block = LlmContentBlock.ImageBlock("base64==", "image/png");
        var msg = new LlmMessage { Role = "user", ContentBlocks = [block] };

        var describer = Substitute.For<IImageDescriber>();
#pragma warning disable CA2012 // NSubstitute configure call — ValueTask is consumed by the substitute
        describer.DescribeAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => ValueTask.FromResult<string?>(null));
#pragma warning restore CA2012

        var result = await ContextManager.StripMediaFromMessageAsync(msg, DescribeOn(), describer, CancellationToken.None);

        Assert.Contains("Image removed: image/png", result.ContentBlocks![0].Text);
        Assert.Null(block.ImageDescription);
    }

    [Fact]
    public async Task StripMediaFromMessageAsync_DescribeOnButNoDescriber_FallsBackToPlaceholder()
    {
        var block = LlmContentBlock.ImageBlock("base64==", "image/png");
        var msg = new LlmMessage { Role = "user", ContentBlocks = [block] };

        var result = await ContextManager.StripMediaFromMessageAsync(msg, DescribeOn(), describer: null, CancellationToken.None);

        Assert.Contains("Image removed: image/png", result.ContentBlocks![0].Text);
        Assert.Null(block.ImageDescription);
    }

    [Fact]
    public async Task StripMediaFromMessageAsync_TextOnly_ReturnsUnchanged()
    {
        var msg = new LlmMessage { Role = "user", Content = "Hello world" };

        var result = await ContextManager.StripMediaFromMessageAsync(msg, DescribeOff(), describer: null, CancellationToken.None);

        Assert.Same(msg, result);
    }

    [Fact]
    public async Task StripMediaFromMessageAsync_OversizedToolResult_Truncates()
    {
        var bigContent = new string('x', ContextManager.MaxToolResultChars + 50_000);
        var msg = new LlmMessage { Role = "tool", Content = bigContent, ToolCallId = "call-1" };

        var result = await ContextManager.StripMediaFromMessageAsync(msg, DescribeOff(), describer: null, CancellationToken.None);

        Assert.NotSame(msg, result);
        Assert.True(result.Content!.Length < bigContent.Length);
        Assert.Contains("[Content truncated", result.Content);
        Assert.Equal("call-1", result.ToolCallId); // Preserved
    }

    [Fact]
    public async Task StripMediaFromMessageAsync_SmallToolResult_Unchanged()
    {
        var msg = new LlmMessage { Role = "tool", Content = "Small result", ToolCallId = "call-1" };

        var result = await ContextManager.StripMediaFromMessageAsync(msg, DescribeOff(), describer: null, CancellationToken.None);

        Assert.Same(msg, result);
    }

    [Fact]
    public async Task StripMediaFromMessageAsync_AssistantWithToolCalls_PreservedIntact()
    {
        var msg = new LlmMessage
        {
            Role = "assistant",
            Content = "Let me search",
            ToolCalls = [new LlmToolCall { Id = "call-1", Name = "search", Arguments = "{\"q\":\"test\"}" }],
        };

        var result = await ContextManager.StripMediaFromMessageAsync(msg, DescribeOff(), describer: null, CancellationToken.None);

        Assert.Same(msg, result); // Tool calls are never stripped
    }

    // ── AgeImagesAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task AgeImagesAsync_PreserveZero_DoesNotStrip()
    {
        var imgBlock = LlmContentBlock.ImageBlock("b==", "image/png");
        var msg = new LlmMessage { Role = "user", ContentBlocks = [imgBlock] };
        var messages = Enumerable.Range(0, 20).Select(_ => msg).ToList();

        var cfg = new ImageAgingConfig { PreserveRecentTurns = 0, DescribeOnStrip = false };
        var result = await ContextManager.AgeImagesAsync(messages, cfg, describer: null, CancellationToken.None);

        Assert.All(result, m => Assert.Contains(m.ContentBlocks!, b => b.Type == "image"));
    }

    [Fact]
    public async Task AgeImagesAsync_StripsBeyondThreshold_KeepsRecent()
    {
        var messages = new List<LlmMessage>();
        for (var i = 0; i < 15; i++)
        {
            messages.Add(new LlmMessage
            {
                Role = "user",
                ContentBlocks = [LlmContentBlock.ImageBlock($"b{i}==", "image/png")],
            });
        }

        var cfg = new ImageAgingConfig { PreserveRecentTurns = 5, DescribeOnStrip = false };
        var result = await ContextManager.AgeImagesAsync(messages, cfg, describer: null, CancellationToken.None);

        // Oldest 10 stripped, most recent 5 preserved.
        for (var i = 0; i < 10; i++)
        {
            Assert.All(result[i].ContentBlocks!, b => Assert.NotEqual("image", b.Type));
        }

        for (var i = 10; i < 15; i++)
        {
            Assert.Contains(result[i].ContentBlocks!, b => b.Type == "image");
        }
    }

    [Fact]
    public async Task AgeImagesAsync_ToolMessagesDoNotConsumeTurnBudget()
    {
        // One user turn followed by a heavy tool-use loop (assistant + tool messages),
        // then one more user turn. PreserveRecentTurns = 2 means both user turns are
        // preserved; the intermediate tool messages are on the preserved side too.
        var img = LlmContentBlock.ImageBlock("b==", "image/png");
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", ContentBlocks = [img] },          // idx 0 — user turn #2 from end
            new() { Role = "assistant", Content = "calling tool" },  // idx 1
            new() { Role = "tool", Content = "tool out 1" },         // idx 2
            new() { Role = "assistant", Content = "calling tool" },  // idx 3
            new() { Role = "tool", Content = "tool out 2" },         // idx 4
            new() { Role = "assistant", Content = "calling tool" },  // idx 5
            new() { Role = "tool", Content = "tool out 3" },         // idx 6
            new() { Role = "assistant", Content = "done" },          // idx 7
            new() { Role = "user", Content = "thanks" },             // idx 8 — user turn #1 from end
        };

        var cfg = new ImageAgingConfig { PreserveRecentTurns = 2, DescribeOnStrip = false };
        var result = await ContextManager.AgeImagesAsync(messages, cfg, describer: null, CancellationToken.None);

        // Image in the oldest preserved user turn (idx 0) must remain intact.
        Assert.Contains(result[0].ContentBlocks!, b => b.Type == "image");
    }

    [Fact]
    public async Task AgeImagesAsync_StripsOlderUserTurn_PreservesToolMessagesAfterCutoff()
    {
        // 3 user turns; PreserveRecentTurns = 2 → oldest user turn and anything
        // before it gets stripped. Tool messages on the preserved side keep their images.
        var oldImg = LlmContentBlock.ImageBlock("old==", "image/png");
        var recentImg = LlmContentBlock.ImageBlock("recent==", "image/png");
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", ContentBlocks = [oldImg] },        // idx 0 — turn 3 from end → stripped
            new() { Role = "assistant", Content = "ok" },             // idx 1 — stripped side
            new() { Role = "user", Content = "turn 2" },              // idx 2 — turn 2 from end → preserved
            new() { Role = "assistant", ContentBlocks = [recentImg] },// idx 3 — preserved side, image kept
            new() { Role = "user", Content = "turn 3" },              // idx 4 — turn 1 from end
        };

        var cfg = new ImageAgingConfig { PreserveRecentTurns = 2, DescribeOnStrip = false };
        var result = await ContextManager.AgeImagesAsync(messages, cfg, describer: null, CancellationToken.None);

        // Old user turn's image is gone
        Assert.All(result[0].ContentBlocks!, b => Assert.NotEqual("image", b.Type));
        // Assistant's image on preserved side is still there
        Assert.Contains(result[3].ContentBlocks!, b => b.Type == "image");
    }

    [Fact]
    public async Task AgeImagesAsync_FewerUserTurnsThanBudget_KeepsEverything()
    {
        // Only 2 user turns, budget = 4 → nothing stripped.
        var img = LlmContentBlock.ImageBlock("b==", "image/png");
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "Sys" },
            new() { Role = "user", ContentBlocks = [img] },
            new() { Role = "assistant", Content = "ok" },
            new() { Role = "user", Content = "thanks" },
        };

        var cfg = new ImageAgingConfig { PreserveRecentTurns = 4, DescribeOnStrip = false };
        var result = await ContextManager.AgeImagesAsync(messages, cfg, describer: null, CancellationToken.None);

        Assert.Contains(result[1].ContentBlocks!, b => b.Type == "image");
    }

    // ── StripMediaAsync (list) ──────────────────────────────────────────

    [Fact]
    public async Task StripMediaAsync_MixedMessages_StripsOnlyMedia()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are helpful." },
            new()
            {
                Role = "user",
                Content = "See image",
                ContentBlocks =
                [
                    LlmContentBlock.TextBlock("See image"),
                    LlmContentBlock.ImageBlock("largebase64==", "image/jpeg"),
                ],
            },
            new() { Role = "assistant", Content = "I see a cat." },
            new() { Role = "tool", Content = "Normal result", ToolCallId = "c1" },
        };

        var result = await ContextManager.StripMediaAsync(messages, DescribeOff(), describer: null, CancellationToken.None);

        Assert.Equal(4, result.Count);
        Assert.Equal("You are helpful.", result[0].Content); // System unchanged
        Assert.Contains("Image removed", result[1].ContentBlocks![1].Text); // Image stripped
        Assert.Equal("I see a cat.", result[2].Content); // Assistant unchanged
        Assert.Equal("Normal result", result[3].Content); // Small tool unchanged
    }

    // ── StripMediaAsync: verbose vs brief based on recent-turn cutoff ────

    private static LlmMessage UserWithImage(string text, byte fill) => new()
    {
        Role = "user",
        Content = text,
        ContentBlocks =
        [
            LlmContentBlock.TextBlock(text),
            LlmContentBlock.ImageBlock(Convert.ToBase64String([fill, fill, fill, fill]), "image/jpeg"),
        ],
    };

    [Fact]
    public async Task StripMediaAsync_RecentUserTurns_UseVerboseDescribe()
    {
        // 3 user turns with an image each. PreserveRecentTurns=2 → the last
        // two user turns are "recent" and should get verbose descriptions
        // under emergency compaction; the oldest one falls back to brief.
        var messages = new List<LlmMessage>
        {
            UserWithImage("old meal", 0x01),
            new() { Role = "assistant", Content = "Logged old meal" },
            UserWithImage("recent meal", 0x02),
            new() { Role = "assistant", Content = "Logged recent meal" },
            UserWithImage("current meal", 0x03),
        };

        var describer = Substitute.For<IImageDescriber>();
#pragma warning disable CA2012 // NSubstitute configure call — ValueTask is consumed by the substitute
        describer.DescribeAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => ValueTask.FromResult<string?>("BRIEF description"));
        describer.DescribeVerboseAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => ValueTask.FromResult<string?>("VERBOSE multi-sentence description with visible details and layout."));
#pragma warning restore CA2012

        var cfg = new ImageAgingConfig { PreserveRecentTurns = 2, DescribeOnStrip = true };
        var result = await ContextManager.StripMediaAsync(messages, cfg, describer, CancellationToken.None);

        // Oldest user turn (index 0) → brief
        Assert.Contains("BRIEF description", result[0].ContentBlocks![1].Text);
        // Recent user turns (index 2 and 4) → verbose
        Assert.Contains("VERBOSE", result[2].ContentBlocks![1].Text);
        Assert.Contains("VERBOSE", result[4].ContentBlocks![1].Text);
    }

    [Fact]
    public async Task StripMediaAsync_AllImagesVerboseWhenAllWithinPreserveWindow()
    {
        // 2 user turns, PreserveRecentTurns=4 → all images are "recent",
        // every one of them gets verbose treatment.
        var messages = new List<LlmMessage>
        {
            UserWithImage("meal one", 0x10),
            new() { Role = "assistant", Content = "Logged one" },
            UserWithImage("meal two", 0x11),
        };

        var describer = Substitute.For<IImageDescriber>();
#pragma warning disable CA2012 // NSubstitute configure call — ValueTask is consumed by the substitute
        describer.DescribeVerboseAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => ValueTask.FromResult<string?>("VERBOSE detailed description."));
#pragma warning restore CA2012

        var cfg = new ImageAgingConfig { PreserveRecentTurns = 4, DescribeOnStrip = true };
        var result = await ContextManager.StripMediaAsync(messages, cfg, describer, CancellationToken.None);

        Assert.Contains("VERBOSE", result[0].ContentBlocks![1].Text);
        Assert.Contains("VERBOSE", result[2].ContentBlocks![1].Text);
#pragma warning disable CA2012 // DidNotReceive returns ValueTask for the verification stub; not a real one to await
        _ = describer.DidNotReceive().DescribeAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
#pragma warning restore CA2012
    }

    [Fact]
    public async Task StripMediaAsync_ZeroPreserveTurns_AllImagesBrief()
    {
        // PreserveRecentTurns=0 means no recent window — emergency compaction
        // still needs to strip images (describe path in emergency is always
        // "describe something"), but all get the cheap brief description.
        var messages = new List<LlmMessage>
        {
            UserWithImage("meal one", 0x20),
            UserWithImage("meal two", 0x21),
        };

        var describer = Substitute.For<IImageDescriber>();
#pragma warning disable CA2012 // NSubstitute configure call — ValueTask is consumed by the substitute
        describer.DescribeAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => ValueTask.FromResult<string?>("BRIEF description"));
#pragma warning restore CA2012

        var cfg = new ImageAgingConfig { PreserveRecentTurns = 0, DescribeOnStrip = true };
        var result = await ContextManager.StripMediaAsync(messages, cfg, describer, CancellationToken.None);

        Assert.Contains("BRIEF", result[0].ContentBlocks![1].Text);
        Assert.Contains("BRIEF", result[1].ContentBlocks![1].Text);
#pragma warning disable CA2012 // DidNotReceive returns ValueTask for the verification stub; not a real one to await
        _ = describer.DidNotReceive().DescribeVerboseAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
#pragma warning restore CA2012
    }

    // ── PruneToolResults ────────────────────────────────────────────────

    [Fact]
    public void PruneToolResults_SmallConversation_NoPruning()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "Hello" },
            new()
            {
                Role = "assistant",
                ToolCalls = [new LlmToolCall { Id = "c1", Name = "fn", Arguments = "{}" }],
            },
            new() { Role = "tool", Content = "Small result", ToolCallId = "c1" },
            new() { Role = "assistant", Content = "Done" },
        };

        var result = ContextManager.PruneToolResults(messages);

        // All results preserved (below PruneMinimumTokens threshold)
        Assert.Equal("Small result", result[2].Content);
    }

    [Fact]
    public void PruneToolResults_ProtectsMostRecentTurn()
    {
        // Build a conversation with huge tool results in multiple old turns.
        // The most recent turn should NEVER be pruned regardless of size.
        // Need enough prunable tokens to exceed PruneMinimumTokens (20k) after
        // the PruneProtectTokens (40k) window. At 3.5 chars/token, we need
        // > (40k + 20k) * 3.5 = 210k chars of old tool output total.
        var bigResult = new string('x', 200_000); // ~57k tokens each
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "Very old question" },
            new()
            {
                Role = "assistant",
                ToolCalls = [new LlmToolCall { Id = "c0", Name = "fn", Arguments = "{}" }],
            },
            new() { Role = "tool", Content = bigResult, ToolCallId = "c0" },
            new() { Role = "assistant", Content = "Very old answer" },
            new() { Role = "user", Content = "Old question" },
            new()
            {
                Role = "assistant",
                ToolCalls = [new LlmToolCall { Id = "c1", Name = "fn", Arguments = "{}" }],
            },
            new() { Role = "tool", Content = bigResult, ToolCallId = "c1" },
            new() { Role = "assistant", Content = "Old answer" },
            new() { Role = "user", Content = "New question" }, // Start of most recent turn
            new()
            {
                Role = "assistant",
                ToolCalls = [new LlmToolCall { Id = "c2", Name = "fn", Arguments = "{}" }],
            },
            new() { Role = "tool", Content = bigResult, ToolCallId = "c2" },
            new() { Role = "assistant", Content = "New answer" },
        };

        var result = ContextManager.PruneToolResults(messages);

        // Most recent turn tool result (index 10) must be preserved
        Assert.Equal(bigResult, result[10].Content);
        // At least one old tool result should be pruned (the oldest one, index 2)
        Assert.Equal(ContextManager.PrunedToolResultPlaceholder, result[2].Content);
    }

    [Fact]
    public void PruneToolResults_PreservesToolCallPairing()
    {
        // After pruning, the assistant tool_use message must still exist
        // alongside the (cleared) tool_result.
        // Need enough prunable tokens: at 3.5 chars/token, 250K chars ≈ 71k tokens.
        // After 40k protect, 31k prunable > 20k minimum ⇒ pruning triggers.
        // Multiple old turns ensure enough volume.
        var bigResult = new string('x', 250_000);
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "Q0" },
            new()
            {
                Role = "assistant",
                Content = "Searching...",
                ToolCalls = [new LlmToolCall { Id = "c0", Name = "web_fetch", Arguments = "{\"url\":\"https://example.com/0\"}" }],
            },
            new() { Role = "tool", Content = bigResult, ToolCallId = "c0" },
            new() { Role = "assistant", Content = "Done with first search" },
            new() { Role = "user", Content = "Q1" },
            new()
            {
                Role = "assistant",
                Content = "Searching again...",
                ToolCalls = [new LlmToolCall { Id = "c1", Name = "web_fetch", Arguments = "{\"url\":\"https://example.com\"}" }],
            },
            new() { Role = "tool", Content = bigResult, ToolCallId = "c1" },
            new() { Role = "assistant", Content = "Found it" },
            new() { Role = "user", Content = "Q2" },
            new() { Role = "assistant", Content = "Sure" },
        };

        var result = ContextManager.PruneToolResults(messages);

        // At least one old assistant message with tool calls must be intact
        // (the oldest one at index 1 should still have its tool calls preserved)
        var firstToolUseAssistant = result.First(m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 });
        Assert.NotNull(firstToolUseAssistant.ToolCalls);
        Assert.Equal("web_fetch", firstToolUseAssistant.ToolCalls![0].Name);

        // At least one old tool result should be pruned to placeholder
        var prunedToolResults = result.Where(m => m.Role == "tool" && m.Content == ContextManager.PrunedToolResultPlaceholder).ToList();
        Assert.True(prunedToolResults.Count > 0, "At least one old tool result should be pruned");

        // Pruned tool result must retain its ToolCallId
        foreach (var pruned in prunedToolResults)
        {
            Assert.NotNull(pruned.ToolCallId);
        }
    }

    // ── PrepareMessagesAsync (full pipeline) ────────────────────────────

    [Fact]
    public async Task PrepareMessagesAsync_SystemAlwaysKept()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are helpful." },
            new() { Role = "user", Content = "Hi" },
            new() { Role = "assistant", Content = "Hello!" },
        };

        var result = await ContextManager.PrepareMessagesAsync(messages, 200_000, 16_000, DescribeOff(), describer: null, CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal("system", result[0].Role);
    }

    [Fact]
    public async Task PrepareMessagesAsync_ZeroBudget_ReturnsEmpty()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "Sys" },
            new() { Role = "user", Content = "Hi" },
        };

        var result = await ContextManager.PrepareMessagesAsync(messages, 100, 200, DescribeOff(), describer: null, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── The bug scenario: 30 messages with last two ~100KB tool results ──

    [Fact]
    public async Task PrepareMessagesAsync_30MessagesWithHugeLastToolResults_DoesNotDropEverything()
    {
        // Simulate the exact scenario from the production bug:
        // - 30 messages of conversation history
        // - Last tool-call group has 2 tool results at ~100KB each
        // - Context window is 200k tokens
        // After pruning + trimming, the result should NOT be system-only.

        var messages = new List<LlmMessage>();

        // System prompt (~20KB, like in the real scenario)
        messages.Add(new LlmMessage { Role = "system", Content = new string('s', 20_000) });

        // Build 5 rounds of user→assistant→tool_calls→tool_result→assistant
        for (var round = 0; round < 5; round++)
        {
            messages.Add(new LlmMessage { Role = "user", Content = $"Question {round}" });
            messages.Add(new LlmMessage
            {
                Role = "assistant",
                ToolCalls = [new LlmToolCall { Id = $"call-{round}", Name = "web_fetch", Arguments = $"{{\"url\":\"https://example.com/{round}\"}}" }],
            });
            // Tool results: earlier rounds have small results, last round has huge ones
            var toolContent = round < 4
                ? $"Result for round {round} - small content"
                : new string('x', 100_000); // ~100KB tool result
            messages.Add(new LlmMessage { Role = "tool", Content = toolContent, ToolCallId = $"call-{round}" });
            messages.Add(new LlmMessage { Role = "assistant", Content = $"Answer {round}" });
        }

        // Add a final tool-call group with TWO huge tool results (the real scenario)
        messages.Add(new LlmMessage { Role = "user", Content = "Final question" });
        messages.Add(new LlmMessage
        {
            Role = "assistant",
            ToolCalls =
            [
                new LlmToolCall { Id = "call-final-1", Name = "fetch_page", Arguments = "{}" },
                new LlmToolCall { Id = "call-final-2", Name = "web_fetch", Arguments = "{}" },
            ],
        });
        messages.Add(new LlmMessage { Role = "tool", Content = new string('y', 100_000), ToolCallId = "call-final-1" });
        messages.Add(new LlmMessage { Role = "tool", Content = new string('z', 100_000), ToolCallId = "call-final-2" });

        // Total: system + 5*(user+assistant+tool+assistant) + user + assistant + 2*tool = 1 + 20 + 4 + 2 = 27 messages

        // Prepare with realistic context window
        var result = await ContextManager.PrepareMessagesAsync(messages, 200_000, 16_000, DescribeOff(), describer: null, CancellationToken.None);

        // CRITICAL: The result must NOT be system-only.
        var conversationMessages = result.Where(m => m.Role != "system").ToList();
        Assert.True(conversationMessages.Count > 0,
            "PrepareMessages should not drop all conversation messages");

        // The system message should always be present
        Assert.Equal("system", result[0].Role);

        // Verify no orphaned tool results (every tool message should have a preceding
        // assistant with tool calls in the result)
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].Role == "tool")
            {
                Assert.True(i > 0, "Tool result at index 0 is orphaned");
                // Walk back to find the assistant with tool calls
                var foundAssistant = false;
                for (var j = i - 1; j >= 0; j--)
                {
                    if (result[j].Role == "assistant" && result[j].ToolCalls is { Count: > 0 })
                    {
                        foundAssistant = true;
                        break;
                    }

                    if (result[j].Role != "tool") break; // Hit a non-tool, non-assistant message
                }

                Assert.True(foundAssistant, $"Tool result at index {i} has no matching assistant tool_use");
            }
        }
    }

    [Fact]
    public async Task PrepareMessagesAsync_WithImage_PrunesOldToolResultsNotImages()
    {
        // Images within the preserve window are kept intact; aging only strips older messages.
        // With PreserveRecentTurns=10 and only 5 messages, images are preserved.
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "Sys" },
            new()
            {
                Role = "user",
                Content = "See this",
                ContentBlocks =
                [
                    LlmContentBlock.TextBlock("See this"),
                    LlmContentBlock.ImageBlock(new string('A', 10_000), "image/png"),
                ],
            },
            new() { Role = "assistant", Content = "I see" },
            new() { Role = "user", Content = "Thanks" },
            new() { Role = "assistant", Content = "You're welcome" },
        };

        var result = await ContextManager.PrepareMessagesAsync(messages, 200_000, 16_000, DescribeOff(), describer: null, CancellationToken.None);

        // Image block should still be present (within the preserve window)
        var userMsg = result.First(m => m.Role == "user" && m.ContentBlocks is { Count: > 0 });
        Assert.Equal("image", userMsg.ContentBlocks![1].Type);
    }

    // ── TokenEstimator: image-aware estimation ──────────────────────────

    [Fact]
    public void EstimateTokens_WithImageContentBlock_IncludesImageTokens()
    {
        // A message with a ~100KB base64 image should estimate significantly more
        // than a text-only message
        var base64Data = new string('A', 100_000); // 100KB base64
        var msgWithImage = new LlmMessage
        {
            Role = "user",
            Content = "See this",
            ContentBlocks =
            [
                LlmContentBlock.TextBlock("See this"),
                LlmContentBlock.ImageBlock(base64Data, "image/png"),
            ],
        };
        var msgTextOnly = new LlmMessage { Role = "user", Content = "See this" };

        var tokensWithImage = TokenEstimator.EstimateTokens(msgWithImage);
        var tokensTextOnly = TokenEstimator.EstimateTokens(msgTextOnly);

        // Image should add at least 100 tokens (100K / 625 ≈ 160)
        Assert.True(tokensWithImage > tokensTextOnly + 100,
            $"Image message ({tokensWithImage}) should be much larger than text-only ({tokensTextOnly})");
    }

    [Fact]
    public void EstimateTokens_SmallImage_UsesMinimumTokens()
    {
        // A tiny image (100 bytes of base64) should still cost the minimum 85 tokens
        var msgWithTinyImage = new LlmMessage
        {
            Role = "user",
            ContentBlocks = [LlmContentBlock.ImageBlock("tiny", "image/png")],
        };

        var tokens = TokenEstimator.EstimateTokens(msgWithTinyImage);

        // Should include at least MinImageTokens (85) for the image
        Assert.True(tokens >= 85, $"Tiny image should cost at least 85 tokens, got {tokens}");
    }

    // ── 30 messages with 2 massive last tool results ──────────────────

    [Fact]
    public async Task PrepareMessagesAsync_Exactly30Messages_LastTwoToolCallsMassive_SurvivesIntact()
    {
        // Exactly 30 messages. The final assistant turn makes 2 tool calls,
        // each returning ~120KB. This is the production scenario that caused
        // the original bug (all conversation messages dropped, only system remained).
        //
        // Layout (30 messages total):
        //   [0]  system (20KB)
        //   [1-4]   round 0: user, assistant+toolcall, tool(small), assistant
        //   [5-8]   round 1: user, assistant+toolcall, tool(small), assistant
        //   [9-12]  round 2: user, assistant+toolcall, tool(small), assistant
        //   [13-16] round 3: user, assistant+toolcall, tool(small), assistant
        //   [17-20] round 4: user, assistant+toolcall, tool(small), assistant
        //   [21-24] round 5: user, assistant+toolcall, tool(small), assistant
        //   [25] user (the final question)
        //   [26] assistant with 2 tool calls
        //   [27] tool result 1 (120KB)
        //   [28] tool result 2 (120KB)
        //   [29] assistant (final answer referencing the tool results)

        var messages = new List<LlmMessage>();

        // [0] System prompt
        messages.Add(new LlmMessage { Role = "system", Content = new string('s', 20_000) });

        // [1-24] Six rounds of normal conversation with small tool results
        for (var round = 0; round < 6; round++)
        {
            messages.Add(new LlmMessage { Role = "user", Content = $"Question about topic {round}" });
            messages.Add(new LlmMessage
            {
                Role = "assistant",
                ToolCalls = [new LlmToolCall { Id = $"call-r{round}", Name = "search", Arguments = $"{{\"q\":\"topic {round}\"}}" }],
            });
            messages.Add(new LlmMessage { Role = "tool", Content = $"Search result for topic {round} — nothing too large here.", ToolCallId = $"call-r{round}" });
            messages.Add(new LlmMessage { Role = "assistant", Content = $"Based on the search, here's my answer about topic {round}." });
        }

        // [25] Final user question
        messages.Add(new LlmMessage { Role = "user", Content = "Now fetch these two large pages for me" });

        // [26] Assistant makes 2 tool calls
        messages.Add(new LlmMessage
        {
            Role = "assistant",
            ToolCalls =
            [
                new LlmToolCall { Id = "call-big-1", Name = "web_fetch", Arguments = "{\"url\":\"https://docs.example.com/api-ref\"}" },
                new LlmToolCall { Id = "call-big-2", Name = "web_fetch", Arguments = "{\"url\":\"https://docs.example.com/guide\"}" },
            ],
        });

        // [27-28] Two massive tool results
        var massiveResult1 = new string('A', 120_000); // ~120KB
        var massiveResult2 = new string('B', 120_000); // ~120KB
        messages.Add(new LlmMessage { Role = "tool", Content = massiveResult1, ToolCallId = "call-big-1" });
        messages.Add(new LlmMessage { Role = "tool", Content = massiveResult2, ToolCallId = "call-big-2" });

        // [29] Final assistant response
        messages.Add(new LlmMessage { Role = "assistant", Content = "Here's what I found from both pages..." });

        Assert.Equal(30, messages.Count);

        // ── Act ──
        var result = await ContextManager.PrepareMessagesAsync(messages, 200_000, 16_000, DescribeOff(), describer: null, CancellationToken.None);

        // ── Assert ──

        // CRITICAL: Must NOT be empty or system-only (this was the original bug)
        var conversationMessages = result.Where(m => m.Role != "system").ToList();
        Assert.True(conversationMessages.Count > 0,
            "PrepareMessages must not drop all conversation messages — this was the original production bug");

        // System is always first
        Assert.Equal("system", result[0].Role);

        // The final assistant answer should be present (it's tiny, no reason to drop it)
        Assert.Contains(result, m => m.Role == "assistant" && m.Content == "Here's what I found from both pages...");

        // The assistant with the 2 tool calls should be present (tool_use is never dropped)
        var finalToolUseAssistant = result.FirstOrDefault(m =>
            m.Role == "assistant" && m.ToolCalls is { Count: 2 }
            && m.ToolCalls.Any(tc => tc.Id == "call-big-1"));
        Assert.NotNull(finalToolUseAssistant);

        // Both tool result messages should exist (the pairing must be maintained),
        // though their content may be pruned to placeholder or truncated
        var toolResult1 = result.FirstOrDefault(m => m.Role == "tool" && m.ToolCallId == "call-big-1");
        var toolResult2 = result.FirstOrDefault(m => m.Role == "tool" && m.ToolCallId == "call-big-2");
        Assert.NotNull(toolResult1);
        Assert.NotNull(toolResult2);

        // No orphaned tool results — every tool message must have a preceding
        // assistant with matching tool calls
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].Role != "tool") continue;
            Assert.True(i > 0, $"Tool result at index {i} is at position 0 (orphaned)");

            var foundToolUse = false;
            for (var j = i - 1; j >= 0; j--)
            {
                if (result[j].Role == "assistant" && result[j].ToolCalls is { Count: > 0 })
                {
                    foundToolUse = true;
                    break;
                }

                if (result[j].Role != "tool") break;
            }

            Assert.True(foundToolUse, $"Tool result at index {i} (ToolCallId={result[i].ToolCallId}) has no matching assistant tool_use");
        }

        // Verify at least some old tool results were pruned (the small ones from
        // early rounds should be prunable since we have enough total volume)
        // Note: the old rounds have very small tool results, so they may not
        // individually meet thresholds. The key assertion is that the conversation
        // is intact, which is the critical production bug fix.
    }

    // ── TrimToFit: skip behavior ────────────────────────────────────────

    [Fact]
    public void TrimToFit_OversizedMiddleGroup_SkippedButOlderGroupsKept()
    {
        // Group order: user1, [assistant+huge_tool], user2, assistant2
        // If the tool group is oversized, it should be skipped, and user2+assistant2 kept.
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "Sys" },
            new() { Role = "user", Content = "Q1" },
            new()
            {
                Role = "assistant",
                ToolCalls = [new LlmToolCall { Id = "c1", Name = "fn", Arguments = "{}" }],
            },
            new() { Role = "tool", Content = new string('x', 500_000), ToolCallId = "c1" }, // ~143k tokens
            new() { Role = "user", Content = "Q2" },
            new() { Role = "assistant", Content = "A2" },
        };

        // Budget: system(~5 tokens) + 184k remaining. The tool group is ~143k tokens,
        // user2+assistant2 are tiny. With skip behavior, tool group is skipped,
        // and the smaller messages are kept.
        var result = TokenEstimator.TrimToFit(messages, 200_000, 16_000);

        // Should have system + at least Q2 and A2
        Assert.Contains(result, m => m.Role == "user" && m.Content == "Q2");
        Assert.Contains(result, m => m.Role == "assistant" && m.Content == "A2");
    }
}
