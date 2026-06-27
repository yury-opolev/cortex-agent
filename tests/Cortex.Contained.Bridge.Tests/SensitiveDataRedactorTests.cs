using Cortex.Contained.Bridge.Security;

namespace Cortex.Contained.Bridge.Tests;

public class SensitiveDataRedactorTests
{
    // ── Null / Empty ──────────────────────────────────────────────

    [Fact]
    public void Redact_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SensitiveDataRedactor.Redact(null));
    }

    [Fact]
    public void Redact_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SensitiveDataRedactor.Redact(string.Empty));
    }

    [Fact]
    public void Redact_NoSensitiveData_ReturnsUnchanged()
    {
        const string input = "Hello, this is a normal log message with no secrets.";
        Assert.Equal(input, SensitiveDataRedactor.Redact(input));
    }

    // ── Key-value patterns ────────────────────────────────────────

    [Theory]
    [InlineData("api_key=sk-12345abcdef", "api_key=[REDACTED]")]
    [InlineData("api-key=sk-12345abcdef", "api-key=[REDACTED]")]
    [InlineData("apikey=sk-12345abcdef", "apikey=[REDACTED]")]
    [InlineData("token=my-secret-token", "token=[REDACTED]")]
    [InlineData("secret: my-big-secret", "secret: [REDACTED]")]
    [InlineData("password=hunter2", "password=[REDACTED]")]
    [InlineData("credential = xyz123", "credential = [REDACTED]")]
    public void Redact_KeyValuePatterns_Redacted(string input, string expected)
    {
        Assert.Equal(expected, SensitiveDataRedactor.Redact(input));
    }

    [Fact]
    public void Redact_KeyValuePattern_CaseInsensitive()
    {
        const string input = "API_KEY=sk-something SECRET=hidden";

        var result = SensitiveDataRedactor.Redact(input);

        Assert.DoesNotContain("sk-something", result);
        Assert.DoesNotContain("hidden", result);
    }

    [Fact]
    public void Redact_KeyValuePattern_PreservesContext()
    {
        const string input = "Connecting with token=abc123 to host";

        var result = SensitiveDataRedactor.Redact(input);

        Assert.Contains("Connecting with", result);
        Assert.Contains("to host", result);
        Assert.DoesNotContain("abc123", result);
    }

    // ── API key prefix patterns ───────────────────────────────────

    [Theory]
    [InlineData("Using key sk-proj-abcdefghijklmnopqrstuvwx for OpenAI")]
    [InlineData("Anthropic key: sk-ant-abcdefghijklmnopqrstuvwx")]
    public void Redact_ApiKeyPrefixes_Redacted(string input)
    {
        var result = SensitiveDataRedactor.Redact(input);

        Assert.DoesNotContain("sk-proj-", result);
        Assert.DoesNotContain("sk-ant-", result);
    }

    // ── Phone number patterns ─────────────────────────────────────

    [Theory]
    [InlineData("Call from +15551234567", "+15551234567")]
    [InlineData("Number: 15551234567890", "15551234567890")]
    public void Redact_PhoneNumbers_Redacted(string input, string phone)
    {
        var result = SensitiveDataRedactor.Redact(input);

        Assert.DoesNotContain(phone, result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_ShortNumbers_NotRedacted()
    {
        // Numbers shorter than 10 digits should NOT be redacted (port numbers, IDs, etc.)
        const string input = "Listening on port 5080, ID 12345";

        var result = SensitiveDataRedactor.Redact(input);

        Assert.Contains("5080", result);
        Assert.Contains("12345", result);
    }

    // ── Long Base64 patterns ──────────────────────────────────────

    [Fact]
    public void Redact_LongBase64Token_Redacted()
    {
        var token = Convert.ToBase64String(new byte[32]); // 44-char Base64 string
        var input = $"Auth token: {token}";

        var result = SensitiveDataRedactor.Redact(input);

        Assert.DoesNotContain(token, result);
    }

    [Fact]
    public void Redact_ShortBase64_NotRedacted()
    {
        // Short Base64 strings (under 40 chars) should not be redacted
        const string input = "Session ID: abc123def456";

        Assert.Equal(input, SensitiveDataRedactor.Redact(input));
    }

    // ── Multiple patterns in one string ───────────────────────────

    [Fact]
    public void Redact_MultipleSensitivePatterns_AllRedacted()
    {
        const string input = "User +15551234567 sent api_key=sk-test123";

        var result = SensitiveDataRedactor.Redact(input);

        Assert.DoesNotContain("+15551234567", result);
        Assert.DoesNotContain("sk-test123", result);
    }

    // ── Real-world log message patterns ───────────────────────────

    [Fact]
    public void Redact_RealisticLogMessage_PreservesStructure()
    {
        const string input = "LLM request to openai completed in 1.2s, model=gpt-4o";

        // No sensitive data — should pass through unchanged
        Assert.Equal(input, SensitiveDataRedactor.Redact(input));
    }

    [Fact]
    public void Redact_ConnectionLog_RedactsToken()
    {
        const string input = "Connected to hub with token=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";

        var result = SensitiveDataRedactor.Redact(input);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", result);
        Assert.Contains("Connected to hub with", result);
    }
}
