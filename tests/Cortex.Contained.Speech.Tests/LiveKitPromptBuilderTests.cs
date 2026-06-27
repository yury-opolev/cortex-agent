using Cortex.Contained.Speech.Stt;

namespace Cortex.Contained.Speech.Tests;

/// <summary>
/// Tests for the pure text-shaping logic that converts a chat history into the
/// prompt string the LiveKit turn-detector model expects. Kept separate from
/// the ONNX-dependent tests because this logic is where any subtle divergence
/// from LiveKit's Python reference would hide, and it's fully testable without
/// the model loaded.
/// </summary>
public class LiveKitPromptBuilderTests
{
    // ── normalization ─────────────────────────────────────────────────────

    [Fact]
    public void Normalize_Lowercases()
    {
        Assert.Equal("hello", LiveKitPromptBuilder.NormalizeMultilingual("Hello"));
    }

    [Fact]
    public void Normalize_StripsPunctuationExceptApostropheAndHyphen()
    {
        Assert.Equal("don't co-op yes", LiveKitPromptBuilder.NormalizeMultilingual("Don't co-op, yes!"));
    }

    [Fact]
    public void Normalize_CollapsesWhitespace()
    {
        Assert.Equal("hello world", LiveKitPromptBuilder.NormalizeMultilingual("hello    world"));
    }

    [Fact]
    public void Normalize_TrimsEdges()
    {
        Assert.Equal("hi", LiveKitPromptBuilder.NormalizeMultilingual("  hi  "));
    }

    [Fact]
    public void Normalize_AppliesNFKC()
    {
        // Full-width digit should fold to ASCII via NFKC before lowercase.
        Assert.Equal("42", LiveKitPromptBuilder.NormalizeMultilingual("\uFF14\uFF12"));
    }

    // ── adjacent-role merging and turn limit ──────────────────────────────

    [Fact]
    public void BuildPrompt_MergesAdjacentUserMessages()
    {
        var turns = new[]
        {
            new TurnDetectorMessage("user", "Hello"),
            new TurnDetectorMessage("user", "there"),
        };

        var prompt = LiveKitPromptBuilder.BuildPrompt(turns);

        // Two adjacent 'user' messages collapse into one <|im_start|>user block.
        Assert.Equal("<|im_start|>user\nhello there", prompt);
    }

    [Fact]
    public void BuildPrompt_DoesNotMergeDifferentRoles()
    {
        var turns = new[]
        {
            new TurnDetectorMessage("user", "hi"),
            new TurnDetectorMessage("assistant", "hi back"),
            new TurnDetectorMessage("user", "cool"),
        };

        var prompt = LiveKitPromptBuilder.BuildPrompt(turns);

        Assert.Equal(
            "<|im_start|>user\nhi<|im_end|>\n<|im_start|>assistant\nhi back<|im_end|>\n<|im_start|>user\ncool",
            prompt);
    }

    [Fact]
    public void BuildPrompt_LimitsToLastSixTurns()
    {
        var turns = new List<TurnDetectorMessage>();
        for (var i = 0; i < 10; i++)
        {
            turns.Add(new TurnDetectorMessage("user", $"msg{i}"));
            turns.Add(new TurnDetectorMessage("assistant", $"reply{i}"));
        }

        var prompt = LiveKitPromptBuilder.BuildPrompt(turns);

        // Only the last 6 turns (3 user + 3 assistant) survive. msg0..msg6 should
        // all be gone; msg7 is the first surviving user turn.
        Assert.DoesNotContain("msg6", prompt);
        Assert.Contains("msg7", prompt);
        Assert.Contains("reply9", prompt);
    }

    [Fact]
    public void BuildPrompt_FiltersNonUserAssistantRoles()
    {
        var turns = new[]
        {
            new TurnDetectorMessage("system", "you are helpful"),
            new TurnDetectorMessage("tool", "result"),
            new TurnDetectorMessage("user", "hello"),
        };

        var prompt = LiveKitPromptBuilder.BuildPrompt(turns);

        Assert.DoesNotContain("system", prompt);
        Assert.DoesNotContain("tool", prompt);
        Assert.Equal("<|im_start|>user\nhello", prompt);
    }

    [Fact]
    public void BuildPrompt_StripsTrailingImEndOnlyOnLastMessage()
    {
        // Intermediate messages keep their <|im_end|>; the final user message
        // has it stripped so the model's prediction is "emit <|im_end|> or not?"
        var turns = new[]
        {
            new TurnDetectorMessage("user", "what time is it"),
            new TurnDetectorMessage("assistant", "three pm"),
            new TurnDetectorMessage("user", "thanks"),
        };

        var prompt = LiveKitPromptBuilder.BuildPrompt(turns);

        Assert.EndsWith("thanks", prompt);
        Assert.DoesNotMatch("thanks<\\|im_end\\|>\\s*$", prompt);
        Assert.Contains("three pm<|im_end|>", prompt);   // intermediate kept
    }

    [Fact]
    public void BuildPrompt_EmptyTurns_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LiveKitPromptBuilder.BuildPrompt([]));
    }

    [Fact]
    public void BuildPrompt_SkipsEmptyContentAfterNormalization()
    {
        // Punctuation-only content becomes empty after normalization and
        // should not emit an empty turn.
        var turns = new[]
        {
            new TurnDetectorMessage("user", "hello"),
            new TurnDetectorMessage("user", "!?!"),
        };

        var prompt = LiveKitPromptBuilder.BuildPrompt(turns);

        // The "!?!" turn should have been dropped (normalized content is empty)
        // or merged as whitespace.
        Assert.Contains("hello", prompt);
        Assert.DoesNotContain("!", prompt);
        Assert.DoesNotContain("?", prompt);
    }
}
