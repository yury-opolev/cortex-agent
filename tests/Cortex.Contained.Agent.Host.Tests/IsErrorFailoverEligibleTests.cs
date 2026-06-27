using Cortex.Contained.Agent.Host.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// The glue between a failed <c>LlmCompletionResult.ErrorMessage</c> (formatted
/// by DirectLlmClient as "HTTP {status}: {body}", or a raw transport/deserialize
/// message) and the pure <see cref="LlmFailoverPolicy"/>. This is the riskiest
/// part of the failover wiring (the parse), so it's tested directly.
/// </summary>
public class IsErrorFailoverEligibleTests
{
    [Fact]
    public void ObservedOutage_HttpFormatted_500_PermissionDenied_FailsOver()
        => Assert.True(DirectLlmClient.IsErrorFailoverEligible(
            "HTTP 500: can't get copilot user by id: error getting copilot user " +
            "details: twirp error permission_denied: Error from intermediary with " +
            "HTTP status code 403 \"Forbidden\""));

    [Theory]
    [InlineData("HTTP 503: upstream unavailable")]
    [InlineData("HTTP 401: {\"error\":\"invalid token\"}")]
    [InlineData("HTTP 403: forbidden")]
    [InlineData("HTTP 429: rate limited")]
    public void HttpFormatted_ProviderSide_FailsOver(string msg)
        => Assert.True(DirectLlmClient.IsErrorFailoverEligible(msg));

    [Theory]
    [InlineData("HTTP 400: invalid_request_error")]
    [InlineData("HTTP 422: unprocessable")]
    [InlineData("HTTP 400: This model's maximum context length is 200000 tokens")]
    public void HttpFormatted_RequestFault_DoesNotFailOver(string msg)
        => Assert.False(DirectLlmClient.IsErrorFailoverEligible(msg));

    [Theory]
    [InlineData("Connection refused (api.githubcopilot.com:443)")]
    [InlineData("The operation was canceled.")]
    [InlineData("Failed to deserialize response.")]
    public void NonHttpFormatted_TreatedAsTransport_FailsOver(string msg)
        => Assert.True(DirectLlmClient.IsErrorFailoverEligible(msg));

    [Fact]
    public void ContextWindowExceeded_IsTerminal_DoesNotFailOver()
        => Assert.False(DirectLlmClient.IsErrorFailoverEligible(
            "Context window exceeded: no conversation messages fit within the " +
            "token budget after reserving space for the response."));

    [Fact]
    public void Null_DoesNotFailOver()
        => Assert.False(DirectLlmClient.IsErrorFailoverEligible(null));

    [Fact]
    public void HttpFormatted_UnknownClient404_DoesNotFailOver()
        => Assert.False(DirectLlmClient.IsErrorFailoverEligible("HTTP 404: not found"));
}
