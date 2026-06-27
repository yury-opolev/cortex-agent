using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Llm.Providers;
using Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;
using Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm;

/// <summary>
/// LLM client that calls providers directly using credentials pushed from the Bridge.
/// Credentials are held in memory only — never persisted to disk.
/// Supports OpenAI-compatible (including GitHub Copilot) and Anthropic APIs.
/// </summary>
public sealed partial class DirectLlmClient : ILlmClient, IDisposable
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<DirectLlmClient> logger;

    /// <summary>
    /// Model → provider credential mapping. Updated when Bridge pushes credentials.
    /// Swapped atomically via <see cref="Interlocked.Exchange{T}(ref T, T)"/> so that
    /// concurrent readers always see a consistent snapshot — never a partially-populated dictionary.
    /// </summary>
    private volatile Dictionary<string, ProviderState> modelProviders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Map + ordered fallback chain published together as ONE immutable
    /// reference so <see cref="BuildAttemptOrder"/> always reads a consistent
    /// pair (a torn map/chain read could duplicate a provider or use a stale
    /// one). <c>modelProviders</c> is kept for the other (single-field,
    /// already-atomic) readers; this snapshot is the failover routing source.
    /// </summary>
    private sealed record Routing(
        Dictionary<string, ProviderState> Map,
        IReadOnlyList<(ProviderState Provider, string DefaultModel)> Chain);

    private volatile Routing routing = new(
        new(StringComparer.OrdinalIgnoreCase), []);

    /// <summary>
    /// Centralised provider-auth token lifecycle: Anthropic OAuth refresh state
    /// machine (single-flight locks, refresh/reload callbacks) and Copilot PAT
    /// exchange. Shared by both the complete and stream paths of every provider.
    /// </summary>
    private readonly OAuthTokenManager tokenManager;

    /// <summary>
    /// Per-protocol wire clients. The facade owns routing, failover, and
    /// credential state; these own request building, HTTP, and SSE parsing.
    /// </summary>
    private readonly OpenAiCompatibleApiClient openAiClient;
    private readonly AnthropicApiClient anthropicClient;

    private readonly Cortex.Contained.Agent.Host.Agent.AgentMetrics? metrics;

    public DirectLlmClient(
        IHttpClientFactory httpClientFactory,
        ILogger<DirectLlmClient> logger,
        Cortex.Contained.Agent.Host.Agent.AgentMetrics? metrics = null)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
        this.metrics = metrics;
        this.tokenManager = new OAuthTokenManager(logger, metrics);
        this.openAiClient = new OpenAiCompatibleApiClient(httpClientFactory, this.tokenManager, logger);
        this.anthropicClient = new AnthropicApiClient(httpClientFactory, this.tokenManager, logger);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.tokenManager.Dispose();
    }

    /// <summary>
    /// Sets the callback used to request a token refresh from the Bridge.
    /// Called by <see cref="AgentHub"/> each time the Bridge pushes credentials,
    /// capturing the current <c>Clients.Caller</c> for that connection.
    /// Pass <c>null</c> to clear (e.g., on disconnect).
    /// </summary>
    public void SetRequestTokenRefreshCallback(Func<string, Task<TokenRefreshResult>>? callback)
    {
        this.tokenManager.SetRequestTokenRefreshCallback(callback);
    }

    /// <summary>
    /// Sets the callback used to request a token reload from the Bridge.
    /// Used when the token is revoked (403) by another process (e.g. evals).
    /// </summary>
    public void SetRequestTokenReloadCallback(Func<string, Task<TokenRefreshResult>>? callback)
    {
        this.tokenManager.SetRequestTokenReloadCallback(callback);
    }

    /// <summary>Whether credentials have been received from the Bridge.</summary>
    public bool HasCredentials => this.modelProviders.Count > 0;

    /// <summary>Result of <see cref="ConfigureCredentials"/>.</summary>
    public sealed record CredentialConfigResult(
        string? DefaultModel,
        string? MemoryModel,
        int ContextWindow,
        int MaxOutputTokens);

    /// <summary>
    /// Configures provider credentials and model mappings. Returns the default model
    /// derived from the first provider (its <c>DefaultModel</c> or first model) along
    /// with its context window and max output tokens from the Bridge-supplied metadata.
    /// </summary>
    public CredentialConfigResult ConfigureCredentials(LlmCredentials credentials)
    {
        string? defaultModel = null;
        string? memoryModel = null;
        int contextWindow = 128_000;
        int maxOutputTokens = 8_192;

        var newMap = new Dictionary<string, ProviderState>(StringComparer.OrdinalIgnoreCase);
        var newChain = new List<(ProviderState Provider, string DefaultModel)>();

        foreach (var cred in credentials.Providers)
        {
            // Find an existing ProviderState for this provider name (if any)
            ProviderState? existing = null;
            foreach (var ps in this.modelProviders.Values)
            {
                if (string.Equals(ps.Credential.Name, cred.Name, StringComparison.OrdinalIgnoreCase))
                {
                    existing = ps;
                    break;
                }
            }

            ProviderState state;
            if (existing is not null
                && cred.Kind is CredentialKind.AnthropicOAuth or CredentialKind.GitHubCopilotBearer)
            {
                // Update token fields in-place — this also completes any pending refresh awaiter,
                // releasing the caller of EnsureFreshTokenAsync so it can retry. Applies to both
                // Anthropic OAuth and the Bridge-minted Copilot bearer (a re-push updates the
                // bearer in place, preserving the pending-refresh awaiter).
                existing.UpdateOAuthTokens(
                    cred.AccessToken ?? string.Empty,
                    cred.RefreshToken,
                    cred.AccessTokenExpiresAt);
                state = existing;
            }
            else
            {
                state = new ProviderState(cred);
            }

            // First provider's default model becomes the global default
            if (defaultModel is null)
            {
                defaultModel = cred.DefaultModel ?? (cred.Models.Count > 0 ? cred.Models[0] : null);
                memoryModel = cred.MemoryModel;

                // Look up metadata for the default model from this provider
                if (defaultModel is not null && cred.ModelMetadata is { Count: > 0 } metadata)
                {
                    var meta = metadata.FirstOrDefault(m =>
                        string.Equals(m.Id, defaultModel, StringComparison.OrdinalIgnoreCase));
                    if (meta is not null)
                    {
                        contextWindow = meta.ContextWindow;
                        maxOutputTokens = meta.MaxOutputTokens;
                    }
                }
            }

            foreach (var model in cred.Models)
            {
                newMap[model] = state;
            }

            var chainModel = cred.DefaultModel
                ?? (cred.Models.Count > 0 ? cred.Models[0] : null);
            if (chainModel is not null)
            {
                newChain.Add((state, chainModel));
            }
        }

        // Atomically swap the dictionary reference. Concurrent readers (CompleteAsync,
        // StreamCompleteAsync) will either see the old snapshot or the new one — never
        // an empty or partially-populated dictionary.
        // Publish the consistent (map, chain) pair as one reference FIRST so a
        // failover read can never see a torn pair, then swap the legacy map
        // field for the other single-field readers.
        this.routing = new Routing(newMap, newChain);
        Interlocked.Exchange(ref this.modelProviders, newMap);

        this.LogCredentialsConfigured(credentials.Providers.Count, newMap.Count);

        return new CredentialConfigResult(defaultModel, memoryModel, contextWindow, maxOutputTokens);
    }

    /// <inheritdoc />
    public async Task<LlmCompletionResult> CompleteAsync(
        LlmCompletionRequest request, CancellationToken cancellationToken)
    {
        var attempts = BuildAttemptOrder(request.Model);
        if (attempts.Count == 0)
        {
            return new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "LlmProviderUnavailable",
                ErrorMessage = $"No provider configured for model '{request.Model}'.",
            };
        }

        LlmCompletionResult? last = null;
        var tried = new List<string>();
        for (var i = 0; i < attempts.Count; i++)
        {
            var (prov, model) = attempts[i];
            var attemptReq = string.Equals(model, request.Model, StringComparison.Ordinal)
                ? request
                : request with { Model = model };

            var result = await CompleteWithRetryAsync(prov, attemptReq, cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                if (i > 0)
                {
                    this.LogProviderFailoverUsed(prov.Credential.Name, model);
                }

                return result;
            }

            last = result;
            tried.Add(prov.Credential.Name);

            var more = i < attempts.Count - 1;
            if (!more || !IsFailoverEligible(result))
            {
                break;
            }

            var (nextProv, nextModel) = attempts[i + 1];
            this.LogProviderFailover(
                prov.Credential.Name, model, nextProv.Credential.Name, nextModel,
                ProviderClientHelpers.Truncate(result.ErrorMessage));
        }

        if (last is null)
        {
            return new LlmCompletionResult
            {
                Success = false, ErrorCode = "LlmError", ErrorMessage = "No completion attempted.",
            };
        }

        return tried.Count > 1
            ? last with
            {
                ErrorCode = "LlmAllProvidersFailed",
                ErrorMessage = $"All providers failed ({string.Join(", ", tried)}): {last.ErrorMessage}",
            }
            : last;
    }

    private Task<LlmCompletionResult> CompleteOnceAsync(
        ProviderState provider, LlmCompletionRequest request, CancellationToken cancellationToken)
        => provider.Credential.Api switch
        {
            "openai-completions" or "github-copilot-api" =>
                this.openAiClient.CompleteAsync(provider, request, cancellationToken),
            "anthropic-messages" =>
                this.anthropicClient.CompleteAsync(provider, request, cancellationToken),
            _ => Task.FromResult(new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "LlmProviderUnavailable",
                ErrorMessage = $"Unsupported API type '{provider.Credential.Api}'.",
                ProviderId = provider.Credential.Name,
            }),
        };

    /// <summary>
    /// One provider attempt, retried on transient faults (5xx / timeout / 429) up to
    /// <see cref="MaxTransientRetries"/> times with backoff — so a one-off blip never
    /// reaches failover or the user. A permanent error returns on the first try.
    /// </summary>
    private async Task<LlmCompletionResult> CompleteWithRetryAsync(
        ProviderState provider, LlmCompletionRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            LlmCompletionResult result;
            try
            {
                result = await CompleteOnceAsync(provider, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new LlmCompletionResult
                {
                    Success = false,
                    ErrorCode = "LlmError",
                    ErrorMessage = ex.Message,
                    ProviderId = provider.Credential.Name,
                };
            }

            if (result.Success || attempt >= MaxTransientRetries || !IsErrorTransientRetryable(result.ErrorMessage))
            {
                return result;
            }

            this.LogTransientRetry(
                provider.Credential.Name, attempt + 1, MaxTransientRetries,
                ProviderClientHelpers.Truncate(result.ErrorMessage));
            await Task.Delay(RetryBackoff(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One streaming provider attempt, retried on a transient fault that occurs BEFORE any
    /// content reaches the caller (e.g. a 502 at the response-headers stage). Once any chunk
    /// has been yielded we are committed to the stream, so a later error is surfaced as-is.
    /// </summary>
    private async IAsyncEnumerable<LlmStreamChunk> StreamWithRetryAsync(
        ProviderState provider, LlmCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var emittedContent = false;
            LlmStreamChunk? preContentError = null;

            await foreach (var chunk in StreamOnceAsync(provider, request, cancellationToken).ConfigureAwait(false))
            {
                if (!emittedContent && chunk.IsComplete && chunk.ErrorMessage is not null)
                {
                    preContentError = chunk;
                    break;
                }

                emittedContent = true;
                yield return chunk;
            }

            if (emittedContent || preContentError is null)
            {
                yield break; // committed to this provider, or a clean (no-error) end
            }

            if (attempt < MaxTransientRetries && IsErrorTransientRetryable(preContentError.ErrorMessage))
            {
                this.LogTransientRetry(
                    provider.Credential.Name, attempt + 1, MaxTransientRetries,
                    ProviderClientHelpers.Truncate(preContentError.ErrorMessage));
                await Task.Delay(RetryBackoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            yield return preContentError; // give up — let the caller fail over or surface
            yield break;
        }
    }

    /// <summary>
    /// Ordered attempt list for a request: the requested model's provider
    /// first (with the requested model), then each OTHER provider in the
    /// fallback chain with its default model. If the model resolves to no
    /// provider, the whole chain is the attempt order. Each provider appears
    /// at most once.
    /// </summary>
    private List<(ProviderState Provider, string Model)> BuildAttemptOrder(string requestedModel)
    {
        var snapshot = this.routing; // one consistent (map, chain) pair
        var order = new List<(ProviderState, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (snapshot.Map.TryGetValue(requestedModel, out var primary))
        {
            order.Add((primary, requestedModel));
            seen.Add(primary.Credential.Name);
        }

        foreach (var (prov, model) in snapshot.Chain)
        {
            if (seen.Add(prov.Credential.Name))
            {
                order.Add((prov, model));
            }
        }

        return order;
    }

    /// <summary>Bridge a failed <see cref="LlmCompletionResult"/> to the pure
    /// <see cref="LlmFailoverPolicy"/>. ErrorMessage is formatted by this class
    /// as <c>"HTTP {status}: {body}"</c> for HTTP errors; anything else (a
    /// thrown transport/deserialize message) is treated as a transport
    /// failure.</summary>
    /// <summary>Terminal error codes that must never fail over (the request
    /// fails identically on every provider).</summary>
    private static readonly System.Collections.Generic.HashSet<string> TerminalErrorCodes =
        new(StringComparer.OrdinalIgnoreCase) { "LlmContextWindowExceeded" };

    private static bool IsFailoverEligible(LlmCompletionResult result)
        => !(result.ErrorCode is { } code && TerminalErrorCodes.Contains(code))
           && IsErrorFailoverEligible(result.ErrorMessage);

    /// <summary>Internal for unit tests: bridges the formatted ErrorMessage
    /// ("HTTP {status}: {body}" or a transport message) to
    /// <see cref="LlmFailoverPolicy"/>.</summary>
    internal static bool IsErrorFailoverEligible(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return false; // no signal → conservative: don't burn the chain
        }

        var msg = errorMessage;

        // Internal terminal results have no "HTTP {status}:" prefix, so they
        // would otherwise hit the transport branch and (wrongly) fail over.
        // A context-window-exceeded request fails identically on every
        // provider — failing over just N×-multiplies cost and masks the
        // over-context bug. Keep this list in sync with terminal ErrorCodes.
        if (msg.Contains("context window exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var m = HttpErrorMessageRegex().Match(msg);
        if (m.Success
            && int.TryParse(
                m.Groups[1].Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var status))
        {
            return LlmFailoverPolicy.ShouldFailover(status, m.Groups[2].Value, transportException: false);
        }

        return LlmFailoverPolicy.ShouldFailover(null, msg, transportException: true);
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^HTTP (\d+): ?(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex HttpErrorMessageRegex();

    /// <summary>Max same-provider retries for a transient fault (so up to 3 attempts total).</summary>
    private const int MaxTransientRetries = 2;

    /// <summary>Backoff before retry attempt N: ~0.5s, then ~2s.</summary>
    private static TimeSpan RetryBackoff(int attempt) =>
        TimeSpan.FromMilliseconds(500 * (attempt + 1) * (attempt + 1));

    /// <summary>Internal for unit tests: like <see cref="IsErrorFailoverEligible"/> but for
    /// retrying the SAME provider — bridges the formatted ErrorMessage to
    /// <see cref="LlmFailoverPolicy.ShouldRetrySameProvider"/>.</summary>
    internal static bool IsErrorTransientRetryable(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return false;
        }

        if (errorMessage.Contains("context window exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var m = HttpErrorMessageRegex().Match(errorMessage);
        if (m.Success
            && int.TryParse(
                m.Groups[1].Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var status))
        {
            return LlmFailoverPolicy.ShouldRetrySameProvider(status, m.Groups[2].Value, transportException: false);
        }

        return LlmFailoverPolicy.ShouldRetrySameProvider(null, errorMessage, transportException: true);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LlmStreamChunk> StreamCompleteAsync(
        LlmCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var attempts = BuildAttemptOrder(request.Model);
        if (attempts.Count == 0)
        {
            yield return new LlmStreamChunk
            {
                IsComplete = true,
                ErrorMessage = $"No provider configured for model '{request.Model}'.",
            };
            yield break;
        }

        var yieldedToCaller = false;
        for (var i = 0; i < attempts.Count; i++)
        {
            var (prov, model) = attempts[i];
            var attemptReq = string.Equals(model, request.Model, StringComparison.Ordinal)
                ? request
                : request with { Model = model };
            var more = i < attempts.Count - 1;

            var emittedAny = false;
            await foreach (var chunk in StreamWithRetryAsync(prov, attemptReq, cancellationToken)
                .ConfigureAwait(false))
            {
                // Pre-content failover only: a terminal error before ANY chunk
                // has reached the caller, with a healthy provider still to try,
                // is swallowed and the next provider is attempted. Once we've
                // yielded anything we are committed to this provider (cannot
                // un-stream), so a later error is surfaced.
                var isTerminalError = chunk.IsComplete && chunk.ErrorMessage is not null;

                // Pre-content failover: terminal error, nothing yielded yet,
                // a provider still to try, and the error is provider-side.
                if (!emittedAny && isTerminalError && more
                    && IsErrorFailoverEligible(chunk.ErrorMessage))
                {
                    var (nextProv, nextModel) = attempts[i + 1];
                    this.LogProviderFailover(
                        prov.Credential.Name, model, nextProv.Credential.Name, nextModel,
                        ProviderClientHelpers.Truncate(chunk.ErrorMessage));
                    break; // abandon this provider's stream, try the next
                }

                // Not failing over: surface the chunk. If this is a terminal
                // pre-content error and we already exhausted earlier providers,
                // enrich the message so the chain exhaustion is visible.
                if (isTerminalError && !emittedAny && i > 0)
                {
                    yield return chunk with
                    {
                        ErrorMessage = $"All providers failed: {ProviderClientHelpers.Truncate(chunk.ErrorMessage)}",
                    };
                    yield break;
                }

                emittedAny = true;
                yieldedToCaller = true;
                yield return chunk;
            }

            if (emittedAny)
            {
                yield break; // committed to this provider; done
            }

            // Stream ended with no chunks and no failover decision (rare) —
            // fall through to the next provider if any; otherwise loop ends.
        }

        // Closure guarantee: every attempt produced zero chunks and nothing
        // ever reached the caller. Never end a stream silently — the consumer
        // would stall waiting for a terminal chunk.
        if (!yieldedToCaller)
        {
            yield return new LlmStreamChunk
            {
                IsComplete = true,
                ErrorMessage = "All providers failed: no response produced.",
            };
        }
    }

    private IAsyncEnumerable<LlmStreamChunk> StreamOnceAsync(
        ProviderState provider, LlmCompletionRequest request, CancellationToken cancellationToken)
        => provider.Credential.Api switch
        {
            "openai-completions" or "github-copilot-api" =>
                this.openAiClient.StreamAsync(provider, request, cancellationToken),
            "anthropic-messages" =>
                this.anthropicClient.StreamAsync(provider, request, cancellationToken),
            _ => ProviderClientHelpers.ErrorStream($"Unsupported API type '{provider.Credential.Api}'."),
        };

    // ── LoggerMessage ────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM credentials configured: {ProviderCount} providers, {ModelCount} models")]
    private partial void LogCredentialsConfigured(int providerCount, int modelCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM failover: provider {FromProvider} (model {FromModel}) failed, trying {ToProvider} (model {ToModel}). reason: {Reason}")]
    private partial void LogProviderFailover(string fromProvider, string fromModel, string toProvider, string toModel, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM failover: served by fallback provider {Provider} (model {Model}) — answer is from a different model than originally requested")]
    private partial void LogProviderFailoverUsed(string provider, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM transient error from provider {Provider}: retry {Attempt}/{MaxRetries} after backoff. reason: {Reason}")]
    private partial void LogTransientRetry(string provider, int attempt, int maxRetries, string reason);

}