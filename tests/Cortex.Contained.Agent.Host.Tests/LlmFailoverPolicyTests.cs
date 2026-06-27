using Cortex.Contained.Agent.Host.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Pure policy: which provider failures are eligible to fail over to the next
/// configured provider. The headline case is the 2026-05-19 outage —
/// status 500 wrapping a Copilot permission_denied / 403.
/// </summary>
public class LlmFailoverPolicyTests
{
    [Fact]
    public void ObservedOutage_500_PermissionDenied_FailsOver()
        => Assert.True(LlmFailoverPolicy.ShouldFailover(
            500,
            "can't get copilot user by id: error getting copilot user details: " +
            "twirp error permission_denied: Error from intermediary with HTTP status code 403 \"Forbidden\"",
            transportException: false));

    [Fact]
    public void TransportException_FailsOver()
        => Assert.True(LlmFailoverPolicy.ShouldFailover(null, null, transportException: true));

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public void ProviderSideStatuses_FailOver(int status)
        => Assert.True(LlmFailoverPolicy.ShouldFailover(status, null, false));

    [Theory]
    [InlineData("forbidden")]
    [InlineData("Unauthorized")]
    [InlineData("invalid_api_key")]
    [InlineData("PERMISSION_DENIED")]
    public void AuthKeywordsInBody_FailOver(string body)
        => Assert.True(LlmFailoverPolicy.ShouldFailover(200, body, false));

    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    public void RequestFaultStatuses_DoNotFailOver(int status)
        => Assert.False(LlmFailoverPolicy.ShouldFailover(status, null, false));

    [Theory]
    [InlineData("This model's maximum context length is 200000 tokens")]
    [InlineData("context_length_exceeded")]
    [InlineData("Context window exceeded: no conversation messages fit")]
    [InlineData("content_policy violation")]
    [InlineData("invalid_request_error: messages: ...")]
    public void TerminalRequestBodies_DoNotFailOver(string body)
        => Assert.False(LlmFailoverPolicy.ShouldFailover(400, body, false));

    [Theory]
    [InlineData(404)]
    [InlineData(409)]
    public void UnknownClientStatuses_AreConservative_NoFailover(int status)
        => Assert.False(LlmFailoverPolicy.ShouldFailover(status, null, false));

    [Fact]
    public void Success_DoesNotFailOver()
        => Assert.False(LlmFailoverPolicy.ShouldFailover(200, "{\"ok\":true}", false));

    // ── ShouldRetrySameProvider: which faults are worth retrying the SAME provider ──
    // (a transient blip — 5xx / timeout / rate-limit — clears on retry; bad creds or a
    // bad request do not, so those are left to failover/surfacing.)

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(429)]
    public void TransientStatuses_RetrySameProvider(int status)
        => Assert.True(LlmFailoverPolicy.ShouldRetrySameProvider(status, null, false));

    [Fact]
    public void Timeout_Transport_RetriesSameProvider()
        => Assert.True(LlmFailoverPolicy.ShouldRetrySameProvider(null, null, transportException: true));

    [Theory]
    [InlineData(400)]
    [InlineData(401)] // bad creds won't change on retry — let failover handle it
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void NonTransientStatuses_DoNotRetrySameProvider(int status)
        => Assert.False(LlmFailoverPolicy.ShouldRetrySameProvider(status, null, false));

    [Theory]
    [InlineData("context_length_exceeded")]
    [InlineData("content_policy violation")]
    public void TerminalRequestBodies_DoNotRetrySameProvider(string body)
        => Assert.False(LlmFailoverPolicy.ShouldRetrySameProvider(500, body, false));

    [Fact]
    public void Success_DoesNotRetry()
        => Assert.False(LlmFailoverPolicy.ShouldRetrySameProvider(200, "{\"ok\":true}", false));
}
