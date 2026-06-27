using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers;

/// <summary>
/// One provider wire-protocol implementation (request building, HTTP, SSE parsing)
/// behind the <see cref="DirectLlmClient"/> facade. Implementations receive the
/// <see cref="ProviderState"/> per call so all credential/token state stays owned
/// by the facade.
/// </summary>
internal interface IProviderApiClient
{
    Task<LlmCompletionResult> CompleteAsync(
        ProviderState provider, LlmCompletionRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        ProviderState provider, LlmCompletionRequest request, CancellationToken cancellationToken);
}
