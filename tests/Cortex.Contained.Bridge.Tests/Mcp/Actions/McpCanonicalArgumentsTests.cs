using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Bridge.Mcp.Actions;

namespace Cortex.Contained.Bridge.Tests.Mcp.Actions;

/// <summary>
/// The canonical-argument hash is the binding identity for human approval of mutating MCP
/// calls: an approval binds to EXACTLY these bytes. Determinism (same call → same hash) and
/// precision (any semantic difference → different hash) are security properties, so every
/// edge is pinned here.
/// </summary>
public sealed class McpCanonicalArgumentsTests
{
    [Fact]
    public void Canonicalize_PropertyOrder_DoesNotChangeHash()
    {
        var a = McpCanonicalArguments.Canonicalize("""{"b":2,"a":1}""");
        var b = McpCanonicalArguments.Canonicalize("""{"a":1,"b":2}""");

        Assert.Equal(a.Json, b.Json);
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal("""{"a":1,"b":2}""", a.Json);
    }

    [Fact]
    public void Canonicalize_WhitespaceDifferences_DoNotChangeHash()
    {
        var compact = McpCanonicalArguments.Canonicalize("""{"a":1,"b":[2,3]}""");
        var spaced = McpCanonicalArguments.Canonicalize("{ \"a\" : 1 ,\n  \"b\" : [ 2, 3 ] }");

        Assert.Equal(compact.Json, spaced.Json);
        Assert.Equal(compact.Sha256, spaced.Sha256);
    }

    [Fact]
    public void Canonicalize_NestedObjects_AreSorted()
    {
        var result = McpCanonicalArguments.Canonicalize("""{"outer":{"z":1,"a":{"y":2,"b":3}}}""");

        Assert.Equal("""{"outer":{"a":{"b":3,"y":2},"z":1}}""", result.Json);

        var reordered = McpCanonicalArguments.Canonicalize("""{"outer":{"a":{"b":3,"y":2},"z":1}}""");
        Assert.Equal(result.Sha256, reordered.Sha256);
    }

    [Fact]
    public void Canonicalize_ArrayOrder_IsPreserved()
    {
        var original = McpCanonicalArguments.Canonicalize("""{"items":[3,1,2]}""");
        var reordered = McpCanonicalArguments.Canonicalize("""{"items":[1,2,3]}""");

        Assert.Equal("""{"items":[3,1,2]}""", original.Json);
        Assert.NotEqual(original.Sha256, reordered.Sha256);
    }

    [Fact]
    public void Canonicalize_ObjectsInsideArrays_KeepArrayPositionButSortTheirKeys()
    {
        var result = McpCanonicalArguments.Canonicalize("""{"steps":[{"b":1,"a":2},{"d":3,"c":4}]}""");

        Assert.Equal("""{"steps":[{"a":2,"b":1},{"c":4,"d":3}]}""", result.Json);
    }

    [Fact]
    public void Canonicalize_ChangedValue_ChangesHash()
    {
        var a = McpCanonicalArguments.Canonicalize("""{"a":1}""");
        var b = McpCanonicalArguments.Canonicalize("""{"a":2}""");

        Assert.NotEqual(a.Sha256, b.Sha256);
    }

    [Fact]
    public void Canonicalize_OneAndOnePointZero_AreDifferent()
    {
        // Numeric lexical representation is preserved: "1" and "1.0" are DIFFERENT approvals.
        var integerForm = McpCanonicalArguments.Canonicalize("""{"a":1}""");
        var decimalForm = McpCanonicalArguments.Canonicalize("""{"a":1.0}""");

        Assert.Equal("""{"a":1}""", integerForm.Json);
        Assert.Equal("""{"a":1.0}""", decimalForm.Json);
        Assert.NotEqual(integerForm.Sha256, decimalForm.Sha256);
    }

    [Fact]
    public void Canonicalize_ExponentAndBigNumberLexicalForms_ArePreservedVerbatim()
    {
        var result = McpCanonicalArguments.Canonicalize(
            """{"exp":1e5,"big":123456789012345678901234567890,"neg":-0.5}""");

        Assert.Equal("""{"big":123456789012345678901234567890,"exp":1e5,"neg":-0.5}""", result.Json);
    }

    [Fact]
    public void Canonicalize_DuplicateProperty_Throws()
    {
        Assert.Throws<ArgumentException>(() => McpCanonicalArguments.Canonicalize("""{"a":1,"a":2}"""));
    }

    [Fact]
    public void Canonicalize_DuplicatePropertyAtDepth_Throws()
    {
        // Duplicates are rejected at EVERY depth — including objects nested inside arrays.
        Assert.Throws<ArgumentException>(
            () => McpCanonicalArguments.Canonicalize("""{"outer":{"list":[{"x":1,"x":2}]}}"""));
    }

    [Fact]
    public void Canonicalize_NonObjectRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => McpCanonicalArguments.Canonicalize("[1,2,3]"));
        Assert.Throws<ArgumentException>(() => McpCanonicalArguments.Canonicalize("42"));
        Assert.Throws<ArgumentException>(() => McpCanonicalArguments.Canonicalize("\"text\""));
        Assert.Throws<ArgumentException>(() => McpCanonicalArguments.Canonicalize("null"));
        Assert.Throws<ArgumentException>(() => McpCanonicalArguments.Canonicalize("true"));
    }

    [Fact]
    public void Canonicalize_OversizedInput_Throws()
    {
        // 256 KiB + a handful of structural bytes: over the limit, rejected before parsing.
        var oversized = $$"""{"a":"{{new string('x', 256 * 1024)}}"}""";

        Assert.Throws<ArgumentException>(() => McpCanonicalArguments.Canonicalize(oversized));
    }

    [Fact]
    public void Canonicalize_InputExactlyAtSizeLimit_Succeeds()
    {
        // {"a":"<padding>"} totals exactly 256 KiB of UTF-8 — "over 256 KiB" must not reject it.
        var padding = new string('x', (256 * 1024) - 8);
        var atLimit = $$"""{"a":"{{padding}}"}""";
        Assert.Equal(256 * 1024, Encoding.UTF8.GetByteCount(atLimit));

        var result = McpCanonicalArguments.Canonicalize(atLimit);

        Assert.StartsWith("sha256:", result.Sha256, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonicalize_MalformedJson_Throws()
    {
        // ThrowsAny: the parser surfaces the derived (internal) JsonReaderException.
        Assert.ThrowsAny<JsonException>(() => McpCanonicalArguments.Canonicalize("{ not json"));
    }

    [Fact]
    public void Canonicalize_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => McpCanonicalArguments.Canonicalize(null!));
    }

    [Fact]
    public void Canonicalize_UnicodeKeys_SortOrdinallyAndHashDeterministically()
    {
        // Ordinal order: 'a' (U+0061) < 'z' (U+007A) < 'é' (U+00E9).
        var a = McpCanonicalArguments.Canonicalize("{\"é\":1,\"a\":2,\"z\":3}");
        var b = McpCanonicalArguments.Canonicalize("{\"z\":3,\"a\":2,\"é\":1}");

        Assert.Equal(a.Json, b.Json);
        Assert.Equal(a.Sha256, b.Sha256);

        using var document = JsonDocument.Parse(a.Json);
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["a", "z", "é"], keys);
    }

    [Fact]
    public void Canonicalize_EscapedAndLiteralStringForms_ProduceSameHash()
    {
        // An escaped "A" and a literal "A" are the same string VALUE — one approval, one hash.
        var escaped = McpCanonicalArguments.Canonicalize("{\"k\":\"\\u0041\"}");
        var literal = McpCanonicalArguments.Canonicalize("""{"k":"A"}""");

        Assert.Equal(escaped.Json, literal.Json);
        Assert.Equal(escaped.Sha256, literal.Sha256);
    }

    [Fact]
    public void Canonicalize_EmptyObject_YieldsCompactJsonAndHashOfCanonicalBytes()
    {
        var result = McpCanonicalArguments.Canonicalize("{}");

        Assert.Equal("{}", result.Json);

        // The hash is SHA-256 over the canonical UTF-8 bytes, formatted sha256:<lowercase hex>.
        var expected = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.Json)));
        Assert.Equal(expected, result.Sha256);
    }

    [Fact]
    public void Canonicalize_HashFormat_IsSha256PrefixedLowercaseHex()
    {
        var result = McpCanonicalArguments.Canonicalize("""{"a":true,"b":null,"c":"s"}""");

        Assert.StartsWith("sha256:", result.Sha256, StringComparison.Ordinal);
        var hex = result.Sha256["sha256:".Length..];
        Assert.Equal(64, hex.Length);
        Assert.All(hex, c => Assert.True(c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
    }

    [Fact]
    public void Canonicalize_IsDeterministicAcrossRepeatedCalls()
    {
        const string input = """{"z":[{"b":1,"a":[true,null,"s"]}],"a":{"nested":1.50}}""";

        var first = McpCanonicalArguments.Canonicalize(input);
        var second = McpCanonicalArguments.Canonicalize(input);

        Assert.Equal(first, second);
    }
}
