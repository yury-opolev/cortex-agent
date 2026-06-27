using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Tests;

public class ConversationPreprocessorTests
{
    // ── StripBase64DataUris ────────────────────────────────────────

    [Fact]
    public void StripBase64DataUris_RemovesInlineImageData()
    {
        const string input = "Here is an image: data:image/png;base64,iVBORw0KGgoAAAANSUhEUg== end.";
        var result = ConversationPreprocessor.StripBase64DataUris(input);
        Assert.DoesNotContain("base64,", result);
        Assert.Contains("[base64 image removed]", result);
        Assert.Contains("Here is an image:", result);
        Assert.Contains("end.", result);
    }

    [Fact]
    public void StripBase64DataUris_PreservesNonDataUriText()
    {
        const string input = "data: analysis and data: science";
        var result = ConversationPreprocessor.StripBase64DataUris(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void StripBase64DataUris_HandlesMultipleUris()
    {
        const string input = "img1: data:image/png;base64,abc123== img2: data:image/jpeg;base64,xyz789== end";
        var result = ConversationPreprocessor.StripBase64DataUris(input);
        Assert.DoesNotContain("base64,", result);
        Assert.Equal(2, CountOccurrences(result, "[base64 image removed]"));
    }

    [Fact]
    public void StripBase64DataUris_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ConversationPreprocessor.StripBase64DataUris(string.Empty));
    }

    // ── TruncateLongCodeBlocks ─────────────────────────────────────

    [Fact]
    public void TruncateLongCodeBlocks_ShortBlockPreserved()
    {
        const string input = """
            ```csharp
            var x = 1;
            ```
            """;
        var result = ConversationPreprocessor.TruncateLongCodeBlocks(input);
        Assert.Contains("var x = 1;", result);
        Assert.DoesNotContain("(truncated)", result);
    }

    [Fact]
    public void TruncateLongCodeBlocks_LongBlockTruncated()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"var line{i} = {i};");
        var codeContent = string.Join("\n", lines);
        var input = $"```csharp\n{codeContent}\n```";

        var result = ConversationPreprocessor.TruncateLongCodeBlocks(input);
        Assert.Contains("... (truncated) ...", result);
    }

    [Fact]
    public void TruncateLongCodeBlocks_TextOutsideBlockPreserved()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"var line{i} = {i};");
        var codeContent = string.Join("\n", lines);
        var input = $"before\n```csharp\n{codeContent}\n```\nafter";

        var result = ConversationPreprocessor.TruncateLongCodeBlocks(input);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    // ── SanitizeForLlm ─────────────────────────────────────────────

    [Fact]
    public void SanitizeForLlm_CombinesPipeline()
    {
        const string input = "Some text data:image/png;base64,iVBORw0KGgoAAAANSUhEUg== more text";
        var result = ConversationPreprocessor.SanitizeForLlm(input);
        Assert.DoesNotContain("base64,", result);
        Assert.Contains("[base64 image removed]", result);
        Assert.Contains("Some text", result);
        Assert.Contains("more text", result);
    }

    [Fact]
    public void SanitizeForLlm_TruncatesLongMessages()
    {
        var input = new string('x', 5000);
        var result = ConversationPreprocessor.SanitizeForLlm(input);
        Assert.True(result.Length <= 3000);
        Assert.Contains("... (truncated) ...", result);
    }

    [Fact]
    public void SanitizeForLlm_ShortMessageUnchanged()
    {
        const string input = "Hello, this is a short message.";
        var result = ConversationPreprocessor.SanitizeForLlm(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void SanitizeForLlm_NullReturnsNull()
    {
        Assert.Null(ConversationPreprocessor.SanitizeForLlm(null!));
    }

    [Fact]
    public void SanitizeForLlm_EmptyReturnsEmpty()
    {
        Assert.Equal(string.Empty, ConversationPreprocessor.SanitizeForLlm(string.Empty));
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }
}
