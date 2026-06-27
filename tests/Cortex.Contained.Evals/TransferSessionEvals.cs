using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cortex.Contained.Evals;

/// <summary>
/// Eval test for the <see cref="TransferSessionTool"/> slicer prompt.
/// Uses a REAL LLM call (via <see cref="EvalFixture"/>) to verify that the slicer
/// correctly identifies a topic boundary in a synthetic conversation with a clear
/// topic shift, summarizes the earlier topic, and carries the current-topic
/// messages verbatim into the target session.
///
/// Assertions are soft (presence checks and bands) because LLM output varies.
///
/// Run with: dotnet test tests/Cortex.Contained.Evals --filter "Category=TransferSession"
/// </summary>
[Trait("Category", "TransferSession")]
[Collection("Evals")]
public sealed class TransferSessionEvals
{
    private readonly EvalFixture fixture;
    private readonly ITestOutputHelper output;

    public TransferSessionEvals(EvalFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [Fact(DisplayName = "Slicer identifies a clear topic shift and summarizes the earlier topic")]
    public async Task Slicer_IdentifiesTopicShift_AndSummarizesEarlierTopic()
    {
        // ── Arrange: build a 20-message conversation with a clear topic shift around turn 10.
        //   Turns 0-9: cooking (pasta carbonara recipe)
        //   Turns 10-19: software debugging (JavaScript async/await race condition)
        var cookingTurns = new (string User, string Assistant)[]
        {
            ("Hey, I'm trying to make pasta carbonara tonight but mine always turns out scrambled. What am I doing wrong?",
             "The classic mistake — the pan is too hot when you add the egg. Pull the pan off the heat first, then stir in the egg mixture. The residual heat cooks the egg gently into a creamy sauce."),
            ("Should I use just yolks or whole eggs?",
             "Most Roman recipes call for whole eggs plus an extra yolk. The yolks give richness, the whites add a bit of body. About 3 yolks + 1 whole egg for 4 servings is a common ratio."),
            ("And pecorino vs parmesan?",
             "Traditional carbonara uses pecorino romano — sharper, saltier, more authentic. Parmesan works in a pinch but the flavor is rounder. Mix them 50/50 if you want a middle ground."),
            ("What about the pasta water — how much should I save?",
             "Save at least a cup. The starchy water is what lets you adjust the sauce's consistency at the end. Too thick? Add a splash. Too loose? Skip it. You'll always need more than you think."),
            ("Guanciale or pancetta?",
             "Guanciale is traditional — cured pork jowl, very fatty, melts into the sauce. Pancetta is a close substitute. Bacon works but the smoke flavor isn't authentic. Render it slowly so the fat coats the pasta."),
        };

        var debuggingTurns = new (string User, string Assistant)[]
        {
            ("OK switching topics. I have a JavaScript bug where two async functions seem to race and the wrong one wins. How do I debug it?",
             "Classic async race. Start by adding console.log with timestamps at the entry and exit of each function. Look at the order of calls vs the order of completions — that gap is where the race lives."),
            ("They both touch the same global state variable.",
             "That's almost certainly your problem. Either serialize them with await, or use a mutex pattern (a Promise queue), or restructure so they don't share state. Shared mutable state across async = bugs."),
            ("If I use await, do I lose the parallelism?",
             "Only if you await them sequentially. You can run them in parallel with Promise.all and still await both — but if they touch the same state, parallelism is what's biting you. Sometimes serialization is the answer."),
            ("What does Promise.all do differently than awaiting them one at a time?",
             "Promise.all kicks them all off simultaneously and waits for all to settle. Sequential await means function B doesn't even start until A resolves. For independent work parallel is faster; for state-sharing work it's the source of races."),
            ("Should I use a try/catch around the whole Promise.all?",
             "Yes — and remember Promise.all rejects on the first failure. If you need all results regardless of failures, use Promise.allSettled instead. That returns an array of {status, value/reason} per promise."),
        };

        var sessionStore = new AgentSessionStore(
            new SessionConfig(),
            new MemorySettingsStore(),
            NullLogger<AgentSessionStore>.Instance);
        var activeChannelStore = new ActiveChannelStore();
        activeChannelStore.Set(["webchat-default", "voice-default"]);

        var modelProvider = new ModelProvider();
        modelProvider.SetDefaultModel(this.fixture.Model);
        modelProvider.SetMemoryModel(this.fixture.Model);

        await using var messageStore = new MessageStore(":memory:", NullLogger<MessageStore>.Instance);

        var slicer = new LlmTopicSlicer(
            this.fixture.LlmClient,
            modelProvider,
            NullLogger<LlmTopicSlicer>.Instance);

        // Stand-in runtime: TransferSessionAsync just defers to the same drain + Seed
        // primitives that the real runtime implementation does. We don't need a full
        // AgentRuntime for the eval — the slicer is the only LLM-backed component
        // under test.
        var sessionStoreCapture = sessionStore;
        var fakeRuntime = Substitute.For<IAgentRuntime>();
        fakeRuntime
            .When(r => r.TransferSessionAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<LlmMessage>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var channelId = (string)call.Args()[0];
                var messages = (IReadOnlyList<LlmMessage>)call.Args()[1];
                var session = sessionStoreCapture.GetOrCreateWithIdleCheck(channelId);
                _ = session.DrainExtractionBuffer();
                sessionStoreCapture.Seed(channelId, messages, maxHistory: int.MaxValue);
            });

        // Stub dispatcher — evals exercise the slicer, not the proactive-message path.
        var dispatcher = Substitute.For<IProactiveMessageDispatcher>();
        dispatcher
            .DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToolExecutionContext?>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveDispatchResult { Success = true });

        var subagentTempDir1 = Path.Combine(Path.GetTempPath(), "transfer-evals-1-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(subagentTempDir1);
        try
        {
            using var subagentStore1 = new SubagentSessionStore(subagentTempDir1, NullLogger<SubagentSessionStore>.Instance);

            var tool = new TransferSessionTool(
                sessionStore,
                activeChannelStore,
                slicer,
                () => fakeRuntime,
                dispatcher,
                messageStore,
                NullLogger<TransferSessionTool>.Instance,
                new ChannelConversationResolver(),
                subagentStore1);

            // Seed source session with the cooking turns followed by the debugging turns.
            var sourceSession = sessionStore.GetOrCreate("webchat-default");
            sourceSession.ClearHistory();
            foreach (var (u, a) in cookingTurns)
            {
                sourceSession.AddMessage(new LlmMessage { Role = "user", Content = u });
                sourceSession.AddMessage(new LlmMessage { Role = "assistant", Content = a });
            }

            foreach (var (u, a) in debuggingTurns)
            {
                sourceSession.AddMessage(new LlmMessage { Role = "user", Content = u });
                sourceSession.AddMessage(new LlmMessage { Role = "assistant", Content = a });
            }

            Assert.Equal(20, sourceSession.MessageCount);

            var context = new ToolExecutionContext
            {
                ConversationId = "webchat-default",
                ChannelId = "webchat-default",
            };

            // ── Act: real LLM slicer call.
            var result = await tool.ExecuteAsync(
                """{"target_channel":"voice-default","user_confirmed":true}""",
                context,
                CancellationToken.None);

            this.output.WriteLine($"Result.Success: {result.Success}");
            this.output.WriteLine($"Result.Content:\n{result.Content}");
            this.output.WriteLine($"Result.Error: {result.Error}");

            // ── Assert: soft checks on observable state.
            Assert.True(result.Success, $"Transfer failed: {result.Error}");

            var targetSession = sessionStore.GetOrCreate("voice-default");
            var targetHistory = targetSession.GetHistory();

            this.output.WriteLine($"Target session has {targetHistory.Count} messages.");
            for (var i = 0; i < targetHistory.Count; i++)
            {
                var m = targetHistory[i];
                this.output.WriteLine($"  [{i}] {m.Role}/{m.MessageType}: {Truncate(m.Content ?? "", 200)}");
            }

            // The conversation has two distinct topics — slicer should produce a prior-summary
            // for the cooking portion and carry the debugging portion verbatim.
            Assert.NotEmpty(targetHistory);
            Assert.Equal(LlmMessageType.CompactionSummary, targetHistory[0].MessageType);
            Assert.Contains("transferred from webchat-default", targetHistory[0].Content);

            // The marker / pre-topic summary should mention at least one cooking-related token.
            var markerContent = targetHistory[0].Content ?? string.Empty;
            var cookingKeywords = new[] { "carbonara", "pasta", "egg", "pecorino", "guanciale", "cooking", "recipe" };
            Assert.True(
                cookingKeywords.Any(k => markerContent.Contains(k, StringComparison.OrdinalIgnoreCase)),
                $"Expected pre-topic summary to mention cooking; got: {markerContent}");

            // The marker / pre-topic summary should NOT contain the current topic's content
            // verbatim (otherwise the slicer is summarizing the wrong segment).
            var debuggingPhrases = new[] { "Promise.all", "Promise.allSettled" };
            var summaryLeakedCurrentTopic = debuggingPhrases.Any(p => markerContent.Contains(p, StringComparison.Ordinal));
            if (summaryLeakedCurrentTopic)
            {
                this.output.WriteLine("WARN: pre-topic summary mentions debugging keywords — slicer may have mis-sliced.");
            }

            // The verbatim tail (everything after [0]) should contain at least one debugging keyword.
            var verbatimTail = targetHistory.Skip(1).ToList();
            Assert.NotEmpty(verbatimTail);
            var verbatimText = string.Join(" ", verbatimTail.Select(m => m.Content ?? string.Empty));
            var debuggingKeywords = new[] { "async", "await", "race", "Promise", "JavaScript" };
            Assert.True(
                debuggingKeywords.Any(k => verbatimText.Contains(k, StringComparison.OrdinalIgnoreCase)),
                $"Expected verbatim tail to contain debugging keywords; got: {Truncate(verbatimText, 500)}");

            // Verbatim count should be in a reasonable band: there are 10 debugging messages
            // (5 user + 5 assistant). Boundary mis-slicing typically lands at +/- a few turns
            // around the true shift. Accept anything from 4..20 verbatim messages.
            var verbatimCount = verbatimTail.Count;
            this.output.WriteLine($"Verbatim count: {verbatimCount} (expected ~10, tolerance 4..20)");
            Assert.InRange(verbatimCount, 4, 20);

            // MessageStore breadcrumb should be persisted to target channel.
            var stored = await messageStore.GetMessagesAsync("voice-default", limit: 10);
            Assert.Contains(stored, m =>
                m.Category == Cortex.Contained.Contracts.Hub.MessageCategory.Transfer
                && m.Content.Contains("Continued from WebChat", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(subagentTempDir1, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact(DisplayName = "Slicer works in reverse direction (voice → text) with a clear topic shift")]
    public async Task Slicer_ReverseDirection_VoiceToText()
    {
        // Same shape as the forward test but with source=voice-default, target=webchat-default.
        // Topic shift: travel planning → kitchen renovation.
        var travelTurns = new (string User, string Assistant)[]
        {
            ("I'm planning a trip to Lisbon next month. Any recommendations?",
             "Lisbon is wonderful in spring. Stay in Alfama for the charm, Príncipe Real for nightlife. Don't miss a pastel de nata from Manteigaria."),
            ("How many days should I plan?",
             "Five is the sweet spot — three for Lisbon proper, one day-trip to Sintra, and one to either Cascais or Évora depending on whether you prefer coast or wine country."),
            ("What's the weather like in April?",
             "Mild — highs around 18-21°C, lows around 11. Pack layers and a light rain jacket. April has occasional showers but they pass quickly."),
            ("Do I need to book Sintra in advance?",
             "Yes — Pena Palace and Quinta da Regaleira both sell timed-entry tickets that go fast. Book at least a week ahead, ideally two."),
            ("Should I rent a car?",
             "Only if you're doing Évora or wine country. In Lisbon itself the tram-and-walk combo is faster than driving, and parking is brutal."),
        };

        var kitchenTurns = new (string User, string Assistant)[]
        {
            ("Totally different topic — I'm redoing my kitchen and stuck on countertop material. Quartz vs marble vs butcher block?",
             "Quartz is the practical winner — durable, non-porous, no sealing. Marble is gorgeous but stains and etches. Butcher block is warmer but needs oiling every few months."),
            ("If I bake a lot, does that change the answer?",
             "It nudges toward marble for rolling pastry — the cool surface is great. But if you're not a pastry chef every weekend, you can chill a quartz section by setting a marble cutting board on top when you bake."),
            ("What about waterfall edges?",
             "Pretty but pricey — adds 30-50% to material cost and you lose under-counter storage on that end. Skip unless the island is a focal point."),
            ("How thick should the slab be?",
             "Three centimeters is standard and structurally fine. Two-cm looks more refined but needs more support. Don't go thinner than 2cm or it'll feel cheap."),
            ("Any brand recommendations for quartz?",
             "Caesarstone, Silestone, and Cambria are the three majors. Caesarstone has the widest color range, Silestone is best for high-traffic, Cambria for marble look-alikes."),
        };

        var sessionStore = new AgentSessionStore(
            new SessionConfig(),
            new MemorySettingsStore(),
            NullLogger<AgentSessionStore>.Instance);
        var activeChannelStore = new ActiveChannelStore();
        activeChannelStore.Set(["webchat-default", "voice-default"]);

        var modelProvider = new ModelProvider();
        modelProvider.SetDefaultModel(this.fixture.Model);
        modelProvider.SetMemoryModel(this.fixture.Model);

        await using var messageStore = new MessageStore(":memory:", NullLogger<MessageStore>.Instance);

        var slicer = new LlmTopicSlicer(
            this.fixture.LlmClient,
            modelProvider,
            NullLogger<LlmTopicSlicer>.Instance);

        // Stand-in runtime: TransferSessionAsync just defers to the same drain + Seed
        // primitives that the real runtime implementation does. We don't need a full
        // AgentRuntime for the eval — the slicer is the only LLM-backed component
        // under test.
        var sessionStoreCapture = sessionStore;
        var fakeRuntime = Substitute.For<IAgentRuntime>();
        fakeRuntime
            .When(r => r.TransferSessionAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<LlmMessage>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var channelId = (string)call.Args()[0];
                var messages = (IReadOnlyList<LlmMessage>)call.Args()[1];
                var session = sessionStoreCapture.GetOrCreateWithIdleCheck(channelId);
                _ = session.DrainExtractionBuffer();
                sessionStoreCapture.Seed(channelId, messages, maxHistory: int.MaxValue);
            });

        // Stub dispatcher — evals exercise the slicer, not the proactive-message path.
        var dispatcher = Substitute.For<IProactiveMessageDispatcher>();
        dispatcher
            .DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToolExecutionContext?>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveDispatchResult { Success = true });

        var subagentTempDir2 = Path.Combine(Path.GetTempPath(), "transfer-evals-2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(subagentTempDir2);
        try
        {
            using var subagentStore2 = new SubagentSessionStore(subagentTempDir2, NullLogger<SubagentSessionStore>.Instance);

            var tool = new TransferSessionTool(
                sessionStore,
                activeChannelStore,
                slicer,
                () => fakeRuntime,
                dispatcher,
                messageStore,
                NullLogger<TransferSessionTool>.Instance,
                new ChannelConversationResolver(),
                subagentStore2);

            var sourceSession = sessionStore.GetOrCreate("voice-default");
            sourceSession.ClearHistory();
            foreach (var (u, a) in travelTurns)
            {
                sourceSession.AddMessage(new LlmMessage { Role = "user", Content = u });
                sourceSession.AddMessage(new LlmMessage { Role = "assistant", Content = a });
            }

            foreach (var (u, a) in kitchenTurns)
            {
                sourceSession.AddMessage(new LlmMessage { Role = "user", Content = u });
                sourceSession.AddMessage(new LlmMessage { Role = "assistant", Content = a });
            }

            var context = new ToolExecutionContext
            {
                ConversationId = "voice-default",
                ChannelId = "voice-default",
            };

            var result = await tool.ExecuteAsync(
                """{"target_channel":"webchat-default","user_confirmed":true}""",
                context,
                CancellationToken.None);

            this.output.WriteLine($"Result.Success: {result.Success}");
            this.output.WriteLine($"Result.Content:\n{result.Content}");
            this.output.WriteLine($"Result.Error: {result.Error}");

            Assert.True(result.Success, $"Transfer failed: {result.Error}");

            var targetSession = sessionStore.GetOrCreate("webchat-default");
            var targetHistory = targetSession.GetHistory();

            Assert.NotEmpty(targetHistory);
            Assert.Equal(LlmMessageType.CompactionSummary, targetHistory[0].MessageType);
            Assert.Contains("transferred from voice-default", targetHistory[0].Content);

            var markerContent = targetHistory[0].Content ?? string.Empty;
            var travelKeywords = new[] { "Lisbon", "Sintra", "travel", "trip", "Portugal" };
            Assert.True(
                travelKeywords.Any(k => markerContent.Contains(k, StringComparison.OrdinalIgnoreCase)),
                $"Expected pre-topic summary to mention travel; got: {markerContent}");

            var verbatimTail = targetHistory.Skip(1).ToList();
            Assert.NotEmpty(verbatimTail);
            var verbatimText = string.Join(" ", verbatimTail.Select(m => m.Content ?? string.Empty));
            var kitchenKeywords = new[] { "quartz", "marble", "kitchen", "countertop", "butcher" };
            Assert.True(
                kitchenKeywords.Any(k => verbatimText.Contains(k, StringComparison.OrdinalIgnoreCase)),
                $"Expected verbatim tail to mention kitchen topic; got: {Truncate(verbatimText, 500)}");

            // Source breadcrumb in voice channel (the reverse-direction case adds it there).
            var sourceMessages = await messageStore.GetMessagesAsync("voice-default", limit: 10);
            Assert.Contains(sourceMessages, m =>
                m.Category == Cortex.Contained.Contracts.Hub.MessageCategory.Transfer
                && m.Content.Contains("Continued in WebChat", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(subagentTempDir2, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}
