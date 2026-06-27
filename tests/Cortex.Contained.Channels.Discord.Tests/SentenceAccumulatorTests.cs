using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class SentenceAccumulatorTests
{
    // ── Basic sentence extraction ────────────────────────────────────

    [Fact]
    public void SingleCompleteSentence_ExtractedCorrectly()
    {
        var acc = new SentenceAccumulator();
        acc.Append("Hello, this is a test sentence. ");

        Assert.True(acc.TryGetNextSentence(out var sentence));
        Assert.Equal("Hello, this is a test sentence.", sentence);
        Assert.False(acc.TryGetNextSentence(out _));
    }

    [Fact]
    public void MultipleSentences_ExtractedInOrder()
    {
        var acc = new SentenceAccumulator();
        acc.Append("First sentence here. Second sentence here. Third one here. ");

        Assert.True(acc.TryGetNextSentence(out var s1));
        Assert.Equal("First sentence here.", s1);

        Assert.True(acc.TryGetNextSentence(out var s2));
        Assert.Equal("Second sentence here.", s2);

        Assert.True(acc.TryGetNextSentence(out var s3));
        Assert.Equal("Third one here.", s3);

        Assert.False(acc.TryGetNextSentence(out _));
    }

    [Fact]
    public void ExclamationAndQuestion_AreSentenceBoundaries()
    {
        var acc = new SentenceAccumulator();
        acc.Append("What a great day! How are you doing? I am fine. ");

        Assert.True(acc.TryGetNextSentence(out var s1));
        Assert.Equal("What a great day!", s1);

        Assert.True(acc.TryGetNextSentence(out var s2));
        Assert.Equal("How are you doing?", s2);

        Assert.True(acc.TryGetNextSentence(out var s3));
        Assert.Equal("I am fine.", s3);
    }

    // ── Streaming (incremental) input ────────────────────────────────

    [Fact]
    public void IncrementalChunks_AccumulateUntilSentenceBoundary()
    {
        var acc = new SentenceAccumulator();

        acc.Append("Hello, ");
        Assert.False(acc.HasSentences);

        acc.Append("this is a ");
        Assert.False(acc.HasSentences);

        acc.Append("test sentence. ");
        Assert.True(acc.HasSentences);

        Assert.True(acc.TryGetNextSentence(out var sentence));
        Assert.Equal("Hello, this is a test sentence.", sentence);
    }

    [Fact]
    public void ChunkSplitMidSentence_DoesNotProducePrematureSentence()
    {
        var acc = new SentenceAccumulator();

        // Period arrives but no whitespace after it yet
        acc.Append("End of sentence.");
        Assert.False(acc.HasSentences); // Not confirmed yet — no trailing whitespace

        acc.Append(" Next sentence begins. ");
        Assert.True(acc.HasSentences);

        Assert.True(acc.TryGetNextSentence(out var s1));
        Assert.Equal("End of sentence.", s1);

        Assert.True(acc.TryGetNextSentence(out var s2));
        // The second sentence hasn't been terminated with whitespace after period at end...
        // Actually "Next sentence begins. " has a trailing space after the period, but
        // the period is at end-of-text with no following whitespace → won't extract.
        // Wait, "Next sentence begins. " has space after period, so it should be confirmed.
        Assert.Equal("Next sentence begins.", s2);
    }

    // ── Abbreviations ────────────────────────────────────────────────

    [Fact]
    public void Abbreviation_Dr_DoesNotSplitSentence()
    {
        var acc = new SentenceAccumulator();
        acc.Append("Dr. Smith went to the store. ");

        // "Dr." should NOT be a sentence boundary; only the final period should.
        Assert.True(acc.TryGetNextSentence(out var sentence));
        Assert.Equal("Dr. Smith went to the store.", sentence);
        Assert.False(acc.TryGetNextSentence(out _));
    }

    [Fact]
    public void Abbreviation_Mrs_DoesNotSplitSentence()
    {
        var acc = new SentenceAccumulator();
        acc.Append("Mrs. Jones said hello to everyone present today. ");

        Assert.True(acc.TryGetNextSentence(out var sentence));
        Assert.Equal("Mrs. Jones said hello to everyone present today.", sentence);
        Assert.False(acc.TryGetNextSentence(out _));
    }

    // ── Decimal numbers ──────────────────────────────────────────────

    [Fact]
    public void DecimalNumber_DoesNotSplitSentence()
    {
        var acc = new SentenceAccumulator();
        acc.Append("The value was 3.14 which is close enough to pi. ");

        Assert.True(acc.TryGetNextSentence(out var sentence));
        Assert.Equal("The value was 3.14 which is close enough to pi.", sentence);
        Assert.False(acc.TryGetNextSentence(out _));
    }

    // ── Ellipsis ─────────────────────────────────────────────────────

    [Fact]
    public void Ellipsis_DoesNotSplitSentence()
    {
        var acc = new SentenceAccumulator();
        acc.Append("I was thinking... maybe we should try something different. ");

        Assert.True(acc.TryGetNextSentence(out var sentence));
        Assert.Equal("I was thinking... maybe we should try something different.", sentence);
        Assert.False(acc.TryGetNextSentence(out _));
    }

    // ── Flush ────────────────────────────────────────────────────────

    [Fact]
    public void Flush_ReturnsRemainingPartialText()
    {
        var acc = new SentenceAccumulator();
        acc.Append("This is incomplete");

        Assert.False(acc.HasSentences);

        var remaining = acc.Flush();
        Assert.Equal("This is incomplete", remaining);
        Assert.False(acc.HasPending);
    }

    [Fact]
    public void Flush_ReturnsNullWhenEmpty()
    {
        var acc = new SentenceAccumulator();
        var remaining = acc.Flush();
        Assert.Null(remaining);
    }

    [Fact]
    public void Flush_AfterCompleteSentences_ReturnsOnlyResidual()
    {
        var acc = new SentenceAccumulator();
        acc.Append("First complete sentence. And some trailing text");

        Assert.True(acc.TryGetNextSentence(out var sentence));
        Assert.Equal("First complete sentence.", sentence);

        var remaining = acc.Flush();
        Assert.Equal("And some trailing text", remaining);
    }

    // ── Reset ────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsEverything()
    {
        var acc = new SentenceAccumulator();
        acc.Append("Some text that is a complete sentence. And more partial");

        Assert.True(acc.HasSentences);
        Assert.True(acc.HasPending);

        acc.Reset();

        Assert.False(acc.HasSentences);
        Assert.False(acc.HasPending);
        Assert.Null(acc.Flush());
    }

    // ── Short text not treated as sentence ───────────────────────────

    [Fact]
    public void ShortSegment_NotExtractedAsSentence()
    {
        var acc = new SentenceAccumulator();
        // "OK. " is only 3 chars (below MinSentenceLength of 8)
        acc.Append("OK. Sure thing, let me check that for you. ");

        // "OK." should NOT be extracted (too short), only the full sentence should be
        Assert.True(acc.TryGetNextSentence(out var sentence));
        Assert.Equal("OK. Sure thing, let me check that for you.", sentence);
        Assert.False(acc.TryGetNextSentence(out _));
    }

    // ── Quoted speech with closing quotes ────────────────────────────

    [Fact]
    public void QuotedSpeech_PeriodBeforeClosingQuote()
    {
        var acc = new SentenceAccumulator();
        acc.Append("She said \"I'll be there soon.\" Then she left the building quickly. ");

        Assert.True(acc.TryGetNextSentence(out var s1));
        Assert.Equal("She said \"I'll be there soon.\"", s1);

        Assert.True(acc.TryGetNextSentence(out var s2));
        Assert.Equal("Then she left the building quickly.", s2);
    }

    // ── Typical LLM streaming pattern ────────────────────────────────

    [Fact]
    public void TypicalLlmStreaming_SmallTokenChunks()
    {
        var acc = new SentenceAccumulator();

        // Simulating LLM streaming output token by token
        var tokens = new[]
        {
            "You", "'d", " probably", " want", " three", " to", " four",
            " days", " in", " Prague", ".", " Want", " me", " to", " go",
            " through", " the", " top", " things", " to", " see", "?"
        };

        var sentences = new List<string>();
        foreach (var token in tokens)
        {
            acc.Append(token);
            while (acc.TryGetNextSentence(out var s))
            {
                sentences.Add(s);
            }
        }

        // First sentence should be extracted after ". Want" arrives
        Assert.Single(sentences);
        Assert.Equal("You'd probably want three to four days in Prague.", sentences[0]);

        // Second sentence is still partial (ends with ? at end of text, no trailing whitespace)
        var remaining = acc.Flush();
        Assert.Equal("Want me to go through the top things to see?", remaining);
    }

    [Fact]
    public void TypicalLlmStreaming_MultipleSentencesExtracted()
    {
        var acc = new SentenceAccumulator();

        // Full response that arrives in chunks
        acc.Append("Sure, let me help with that. ");
        acc.Append("Prague is a beautiful city. ");
        acc.Append("You should definitely visit.");

        var sentences = new List<string>();
        while (acc.TryGetNextSentence(out var s))
        {
            sentences.Add(s);
        }

        Assert.Equal(2, sentences.Count);
        Assert.Equal("Sure, let me help with that.", sentences[0]);
        Assert.Equal("Prague is a beautiful city.", sentences[1]);

        // Last sentence is partial (no trailing whitespace after final period)
        var remaining = acc.Flush();
        Assert.Equal("You should definitely visit.", remaining);
    }

    // ── Newlines as sentence confirmers ──────────────────────────────

    [Fact]
    public void Newline_ConfirmsSentenceBoundary()
    {
        var acc = new SentenceAccumulator();
        acc.Append("First sentence here.\nSecond sentence here.\n");

        Assert.True(acc.TryGetNextSentence(out var s1));
        Assert.Equal("First sentence here.", s1);

        Assert.True(acc.TryGetNextSentence(out var s2));
        Assert.Equal("Second sentence here.", s2);
    }
}
