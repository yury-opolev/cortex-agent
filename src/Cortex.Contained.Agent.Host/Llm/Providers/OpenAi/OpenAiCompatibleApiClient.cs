using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// OpenAI-compatible chat-completions wire protocol: api.openai.com,
/// GitHub Copilot (OAuth and PAT header modes), and any custom-BaseUrl
/// OpenAI-compatible endpoint (e.g. Ollama).
/// </summary>
internal sealed partial class OpenAiCompatibleApiClient : IProviderApiClient
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly OAuthTokenManager tokenManager;
    private readonly ILogger logger;

    public OpenAiCompatibleApiClient(
        IHttpClientFactory httpClientFactory,
        OAuthTokenManager tokenManager,
        ILogger logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.tokenManager = tokenManager;
        this.logger = logger;
    }

    public async Task<LlmCompletionResult> CompleteAsync(
        ProviderState provider, LlmCompletionRequest request, CancellationToken cancellationToken)
    {
        this.LogCompletionStart(provider.Credential.Name, request.Model, request.RequestId);

        // Bridge-minted Copilot bearer: proactively refresh from the Bridge before sending
        // (same round-trip Anthropic uses). Legacy GitHubPat/GitHubOAuth use their token directly.
        if (provider.Credential.Kind == CredentialKind.GitHubCopilotBearer)
        {
            await this.tokenManager.EnsureFreshTokenAsync(provider, cancellationToken).ConfigureAwait(false);
        }

        using var httpClient = CreateHttpClient(provider);
        var endpoint = GetOpenAiEndpoint(provider);

        var body = BuildOpenAiRequestBody(request, stream: false, provider.Credential.Api);
        var json = JsonSerializer.Serialize(body, ProviderClientHelpers.JsonOptions);
        var requestUrl = $"{httpClient.BaseAddress}{endpoint}";
        this.LogLlmRequest(request.RequestId, requestUrl, json.Length, request.Messages.Count);

        // Diagnostic: log last message role in serialized body to trace "must end with user" errors
        if (body.Messages is { Count: > 0 })
        {
            var lastMsg = body.Messages[^1];
            var lastRole = lastMsg.Role ?? "null";
            var lastContentNull = lastMsg.Content is null;
            var lastContentType = lastMsg.Content?.GetType().Name ?? "null";
            this.LogLlmRequestTail(request.RequestId, body.Messages.Count, lastRole, lastContentNull, lastContentType);
        }
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            this.LogLlmErrorResponse(request.RequestId, (int)response.StatusCode, errorBody);
            this.LogHttpError(request.RequestId, (int)response.StatusCode, errorBody);

            // Bridge-minted Copilot bearer: retry once on 401 by requesting a fresh bearer
            // from the Bridge (same round-trip Anthropic uses). The PAT never enters the
            // container — the Bridge re-mints. Legacy GitHubPat/GitHubOAuth have no retry path.
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && provider.Credential.Kind == CredentialKind.GitHubCopilotBearer)
            {
                this.LogCopilotTokenRefresh(provider.Credential.Name, request.RequestId);
                try
                {
                    await this.tokenManager.ForceRefreshAsync(provider, cancellationToken).ConfigureAwait(false);

                    using var retryClient = CreateHttpClient(provider);
                    var retryJson = JsonSerializer.Serialize(body, ProviderClientHelpers.JsonOptions);
                    using var retryContent = new StringContent(retryJson, Encoding.UTF8, "application/json");
                    using var retryResponse = await retryClient.PostAsync(
                        endpoint, retryContent, cancellationToken).ConfigureAwait(false);

                    if (retryResponse.IsSuccessStatusCode)
                    {
                        var retryResponseJson = await retryResponse.Content
                            .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        var retryCompletion = JsonSerializer.Deserialize<OpenAiChatResponse>(
                            retryResponseJson, ProviderClientHelpers.JsonOptions);

                        if (retryCompletion is not null)
                        {
                            var retryChoice = retryCompletion.Choices?.FirstOrDefault();
                            return new LlmCompletionResult
                            {
                                Success = true,
                                Content = retryChoice?.Message?.Content?.ToString(),
                                ToolCalls = MapToolCalls(retryChoice?.Message?.ToolCalls),
                                FinishReason = retryChoice?.FinishReason,
                                Usage = MapUsage(retryCompletion.Usage),
                                ProviderId = provider.Credential.Name,
                            };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Re-mint (token exchange) or retry request failed — degrade to a
                    // clean error result below instead of bubbling a raw exception.
                    this.LogCopilotRetryFailed(provider.Credential.Name, request.RequestId, ex.Message);
                }
            }

            return new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "LlmError",
                ErrorMessage = $"HTTP {(int)response.StatusCode}: {ProviderClientHelpers.TruncateError(errorBody)}",
                ProviderId = provider.Credential.Name,
            };
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        this.LogLlmResponse(request.RequestId, (int)response.StatusCode, responseJson.Length);
        var completionResponse = JsonSerializer.Deserialize<OpenAiChatResponse>(responseJson, ProviderClientHelpers.JsonOptions);

        if (completionResponse is null)
        {
            return new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "LlmError",
                ErrorMessage = "Failed to deserialize response.",
                ProviderId = provider.Credential.Name,
            };
        }

        var choice = completionResponse.Choices?.FirstOrDefault();

        return new LlmCompletionResult
        {
            Success = true,
            Content = choice?.Message?.Content?.ToString(),
            ToolCalls = MapToolCalls(choice?.Message?.ToolCalls),
            FinishReason = choice?.FinishReason,
            Usage = MapUsage(completionResponse.Usage),
            ProviderId = provider.Credential.Name,
        };
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        ProviderState provider,
        LlmCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        this.LogStreamStart(provider.Credential.Name, request.Model, request.RequestId);

        // Bridge-minted Copilot bearer: proactively refresh from the Bridge before sending
        // (same round-trip Anthropic uses). Legacy GitHubPat/GitHubOAuth use their token directly.
        if (provider.Credential.Kind == CredentialKind.GitHubCopilotBearer)
        {
            await this.tokenManager.EnsureFreshTokenAsync(provider, cancellationToken).ConfigureAwait(false);
        }

        using var httpClient = CreateHttpClient(provider);
        var endpoint = GetOpenAiEndpoint(provider);

        var body = BuildOpenAiRequestBody(request, stream: true, provider.Credential.Api);
        body.StreamOptions = new OpenAiStreamOptions { IncludeUsage = true };

        var json = JsonSerializer.Serialize(body, ProviderClientHelpers.JsonOptions);
        var requestUrl = $"{httpClient.BaseAddress}{endpoint}";
        this.LogLlmRequest(request.RequestId, requestUrl, json.Length, request.Messages.Count);

        // Diagnostic: log last message role in serialized body
        if (body.Messages is { Count: > 0 })
        {
            var lastMsg = body.Messages[^1];
            var lastRole = lastMsg.Role ?? "null";
            var lastContentNull = lastMsg.Content is null;
            var lastContentType = lastMsg.Content?.GetType().Name ?? "null";
            this.LogLlmRequestTail(request.RequestId, body.Messages.Count, lastRole, lastContentNull, lastContentType);
        }

        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = httpContent,
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage? response = null;
        string? httpError = null;
        try
        {
            response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            this.LogHttpError(request.RequestId, 0, ex.Message);
            httpError = ex.Message;
        }

        if (httpError is not null)
        {
            yield return new LlmStreamChunk { IsComplete = true, ErrorMessage = httpError };
            yield break;
        }

        if (response is not null && !response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            this.LogHttpError(request.RequestId, (int)response.StatusCode, errorBody);

            // Bridge-minted Copilot bearer: retry once on 401 by requesting a fresh bearer
            // from the Bridge (same round-trip Anthropic uses). The PAT never enters the
            // container — the Bridge re-mints. Legacy GitHubPat/GitHubOAuth have no retry path.
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && provider.Credential.Kind == CredentialKind.GitHubCopilotBearer)
            {
                this.LogCopilotTokenRefresh(provider.Credential.Name, request.RequestId);
                var statusCode = (int)response.StatusCode;
                response.Dispose();

                HttpClient? retryClient = null;
                HttpResponseMessage? retryResponse = null;
                try
                {
                    await this.tokenManager.ForceRefreshAsync(provider, cancellationToken).ConfigureAwait(false);

                    retryClient = CreateHttpClient(provider);
                    var retryBody = BuildOpenAiRequestBody(request, stream: true, provider.Credential.Api);
                    retryBody.StreamOptions = new OpenAiStreamOptions { IncludeUsage = true };
                    var retryJson = JsonSerializer.Serialize(retryBody, ProviderClientHelpers.JsonOptions);
                    using var retryHttpContent = new StringContent(retryJson, Encoding.UTF8, "application/json");
                    using var retryRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = retryHttpContent,
                    };
                    retryRequest.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("text/event-stream"));

                    retryResponse = await retryClient.SendAsync(
                        retryRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    retryResponse?.Dispose();
                    retryClient?.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    // Re-mint (token exchange) or retry request failed — degrade to a
                    // clean error chunk below instead of bubbling a raw exception.
                    this.LogCopilotRetryFailed(provider.Credential.Name, request.RequestId, ex.Message);
                    retryResponse?.Dispose();
                    retryClient?.Dispose();
                    retryResponse = null;
                    retryClient = null;
                }

                if (retryResponse is not null && retryResponse.IsSuccessStatusCode)
                {
                    using (retryClient)
                    using (retryResponse)
                    {
                        using var retryStream = await retryResponse.Content
                            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        using var retryReader = new StreamReader(retryStream, Encoding.UTF8);

                        await foreach (var chunk in ParseOpenAiSseAsync(
                            retryReader, request.RequestId, cancellationToken).ConfigureAwait(false))
                        {
                            yield return chunk;
                        }
                    }

                    yield break;
                }

                retryResponse?.Dispose();
                retryClient?.Dispose();

                yield return new LlmStreamChunk
                {
                    IsComplete = true,
                    ErrorMessage = $"HTTP {statusCode}: {ProviderClientHelpers.TruncateError(errorBody)}",
                };
                yield break;
            }

            yield return new LlmStreamChunk
            {
                IsComplete = true,
                ErrorMessage = $"HTTP {(int)response.StatusCode}: {ProviderClientHelpers.TruncateError(errorBody)}",
            };
            response.Dispose();
            yield break;
        }

        // Parse SSE stream
        using var stream = await response!.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var chunk in ParseOpenAiSseAsync(reader, request.RequestId, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }

        response.Dispose();
    }

    private async IAsyncEnumerable<LlmStreamChunk> ParseOpenAiSseAsync(
        StreamReader reader,
        string requestId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(':'))
            {
                continue;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data: ".Length..];

            if (data == "[DONE]")
            {
                this.LogStreamDone(requestId);
                yield break;
            }

            OpenAiStreamResponse? streamResponse = null;
            try
            {
                streamResponse = JsonSerializer.Deserialize<OpenAiStreamResponse>(data, ProviderClientHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                this.LogSseParseError(requestId, ex.Message);
            }

            if (streamResponse is null)
            {
                continue;
            }

            var delta = streamResponse.Choices?.FirstOrDefault()?.Delta;
            var finishReason = streamResponse.Choices?.FirstOrDefault()?.FinishReason;
            var isComplete = finishReason is not null;

            yield return new LlmStreamChunk
            {
                ContentDelta = delta?.Content,
                ToolCallDeltas = MapToolCallDeltas(delta?.ToolCalls),
                IsComplete = isComplete,
                FinishReason = finishReason,
                Usage = MapUsage(streamResponse.Usage),
            };
        }
    }

    private HttpClient CreateHttpClient(ProviderState provider)
    {
        var client = this.httpClientFactory.CreateClient("llm-direct");

        var baseUrl = provider.Credential.BaseUrl?.TrimEnd('/')
            ?? provider.Credential.Api switch
            {
                "github-copilot-api" => "https://api.githubcopilot.com",
                "openai-completions" => "https://api.openai.com",
                _ => throw new InvalidOperationException($"No base URL for API type '{provider.Credential.Api}'."),
            };

        client.BaseAddress = new Uri(baseUrl + "/");

        if (provider.Credential.Api == "github-copilot-api")
        {
            if (provider.Credential.Kind is CredentialKind.GitHubOAuth or CredentialKind.GitHubCopilotBearer)
            {
                // OAuth token or Bridge-minted bearer: use directly as Bearer — no in-container
                // exchange. For GitHubCopilotBearer the live value is the current (possibly just
                // refreshed) bearer in CurrentAccessToken, falling back to the pushed AccessToken.
                // Uses OpenCode-style headers (no Editor-Version / Copilot-Integration-Id).
                var bearer = provider.CurrentAccessToken
                    ?? provider.Credential.AccessToken
                    ?? string.Empty;
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", bearer);
                client.DefaultRequestHeaders.Add("Openai-Intent", "conversation-edits");
                client.DefaultRequestHeaders.Add("x-initiator", "user");
                client.DefaultRequestHeaders.UserAgent.ParseAdd("cortex-agent/1.0.0");
            }
            else
            {
                // Legacy GitHubPat backward-compat: an older Bridge pushes the raw PAT as the
                // credential. In-container minting is gone (the Bridge now mints and pushes a
                // GitHubCopilotBearer), so this best-effort path sends the PAT directly with the
                // VS Code-style headers it was paired with.
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", provider.Credential.ApiKey ?? string.Empty);

                client.DefaultRequestHeaders.Add("Editor-Version", "vscode/1.100.0");
                client.DefaultRequestHeaders.Add("Editor-Plugin-Version", "cortex-agent/1.0.0");
                client.DefaultRequestHeaders.Add("Copilot-Integration-Id", "vscode-chat");
            }

            // GitHub Enterprise Copilot hosts (copilot-api.<ghe-host>) require an explicit API
            // version on /models and /chat/completions; harmless on the public host.
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-06-01");
        }
        else if (!string.IsNullOrEmpty(provider.Credential.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", provider.Credential.ApiKey);
        }

        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    private static string GetOpenAiEndpoint(ProviderState provider)
    {
        return provider.Credential.Api switch
        {
            "github-copilot-api" => "chat/completions",
            _ => "v1/chat/completions",
        };
    }

    internal static OpenAiChatRequest BuildOpenAiRequestBody(
        LlmCompletionRequest request, bool stream, string apiType)
    {
        var messages = request.Messages.Select(m =>
        {
            object? content;
            if (m.ContentBlocks is { Count: > 0 })
            {
                // Multimodal: build content parts array
                var parts = new List<OpenAiContentPart>();
                foreach (var block in m.ContentBlocks)
                {
                    if (block.Type == "image" && block.ImageData is not null && block.ImageMediaType is not null)
                    {
                        parts.Add(new OpenAiContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new OpenAiImageUrl
                            {
                                Url = $"data:{block.ImageMediaType};base64,{block.ImageData}",
                            },
                        });
                    }
                    else if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                    {
                        parts.Add(new OpenAiContentPart { Type = "text", Text = block.Text });
                    }
                }

                content = parts.Count > 0 ? parts : (object?)m.Content;
            }
            else
            {
                content = m.Content;
            }

            return new OpenAiMessage
            {
                Role = m.Role,
                Content = content,
                ToolCalls = m.ToolCalls?.Select(tc => new OpenAiToolCall
                {
                    Id = tc.Id,
                    Type = "function",
                    Function = new OpenAiFunction { Name = tc.Name, Arguments = tc.Arguments },
                }).ToList(),
                ToolCallId = m.ToolCallId,
            };
        }).ToList();

        // Sanitize: ensure every assistant message with tool_calls has matching
        // tool responses. The context-window trimmer (TrimToFit) keeps tool-call
        // groups atomic, but edge cases (seeding from DB, race conditions) can
        // leave orphaned tool_calls that cause OpenAI to return HTTP 400.
        messages = SanitizeToolCalls(messages);

        var result = new OpenAiChatRequest
        {
            Model = request.Model,
            Messages = messages,
            Stream = stream,
        };

        // NOTE: Temperature is intentionally omitted. Many modern models (gpt-5,
        // o-series reasoning models) reject non-default temperature values with HTTP 400.
        // Omitting it lets the API use the model's built-in default. Per-model temperature
        // configuration can be added later when we have model metadata.

        // Copilot API (api.githubcopilot.com) uses max_tokens for all models
        // including Claude. GitHub Models and OpenAI APIs use max_completion_tokens.
        if (apiType == "github-copilot-api")
        {
            result.MaxTokens = request.MaxTokens;
        }
        else
        {
            // Modern models (gpt-4o, gpt-5, etc.) require max_completion_tokens.
            // Older models used max_tokens, but the new parameter is backward-compatible
            // on both OpenAI and GitHub Models APIs.
            result.MaxCompletionTokens = request.MaxTokens;
        }

        if (request.Tools is { Count: > 0 })
        {
            result.Tools = request.Tools.Select(t => new OpenAiToolDefinition
            {
                Type = "function",
                Function = new OpenAiFunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersSchema),
                },
            }).ToList();
        }

        return result;
    }

    /// <summary>
    /// Ensures every assistant message with tool_calls has all required tool
    /// response messages following it. If any tool_call_id is missing a matching
    /// tool response, the assistant's tool_calls are stripped (converted to a
    /// plain text message) to prevent OpenAI HTTP 400 errors.
    /// </summary>
    private static List<OpenAiMessage> SanitizeToolCalls(List<OpenAiMessage> messages)
    {
        // Collect all tool_call_ids that have a matching tool response
        var respondedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            if (msg.Role == "tool" && msg.ToolCallId is not null)
            {
                respondedToolCallIds.Add(msg.ToolCallId);
            }
        }

        var result = new List<OpenAiMessage>(messages.Count);
        var indicesToRemove = new HashSet<int>();

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
            {
                // Check if ALL tool_call_ids have responses
                var allResponded = msg.ToolCalls.All(tc =>
                    tc.Id is not null && respondedToolCallIds.Contains(tc.Id));

                if (!allResponded)
                {
                    // Strip tool_calls — keep as plain assistant message
                    // Also mark orphaned tool responses for removal
                    var orphanedIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var tc in msg.ToolCalls)
                    {
                        if (tc.Id is not null)
                        {
                            orphanedIds.Add(tc.Id);
                        }
                    }

                    // Remove this assistant's matching tool responses too
                    // (they'd be orphaned without the tool_calls)
                    for (var j = i + 1; j < messages.Count; j++)
                    {
                        if (messages[j].Role == "tool" && messages[j].ToolCallId is { } tcId
                            && orphanedIds.Contains(tcId))
                        {
                            indicesToRemove.Add(j);
                        }
                        else if (messages[j].Role != "tool")
                        {
                            break;
                        }
                    }

                    // Convert to plain assistant message
                    result.Add(new OpenAiMessage
                    {
                        Role = "assistant",
                        Content = msg.Content ?? "[Tool calls removed due to missing responses]",
                    });
                    continue;
                }
            }

            if (!indicesToRemove.Contains(i))
            {
                result.Add(msg);
            }
        }

        return result;
    }

    private static LlmToolCall[]? MapToolCalls(List<OpenAiToolCall>? toolCalls)
    {
        if (toolCalls is null or { Count: 0 })
        {
            return null;
        }

        return toolCalls.Select(tc => new LlmToolCall
        {
            Id = tc.Id ?? string.Empty,
            Name = tc.Function?.Name ?? string.Empty,
            Arguments = tc.Function?.Arguments ?? "{}",
        }).ToArray();
    }

    private static LlmToolCallDelta[]? MapToolCallDeltas(List<OpenAiStreamToolCall>? toolCalls)
    {
        if (toolCalls is null || toolCalls.Count == 0)
        {
            return null;
        }

        return toolCalls.Select(tc => new LlmToolCallDelta
        {
            Index = tc.Index,
            Id = tc.Id,
            Name = tc.Function?.Name,
            ArgumentsDelta = tc.Function?.Arguments,
        }).ToArray();
    }

    private static LlmTokenUsage? MapUsage(OpenAiUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new LlmTokenUsage
        {
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            TotalTokens = usage.TotalTokens,
        };
    }

    // ── LoggerMessage ────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM completion start: provider={Provider}, model={Model}, requestId={RequestId}")]
    private partial void LogCompletionStart(string provider, string model, string requestId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM stream start: provider={Provider}, model={Model}, requestId={RequestId}")]
    private partial void LogStreamStart(string provider, string model, string requestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM request: requestId={RequestId}, url={Url}, bodyLength={BodyLength}, messageCount={MessageCount}")]
    private partial void LogLlmRequest(string requestId, string url, int bodyLength, int messageCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM request tail: requestId={RequestId}, serializedMsgCount={SerializedCount}, lastRole={LastRole}, contentNull={ContentNull}, contentType={ContentType}")]
    private partial void LogLlmRequestTail(string requestId, int serializedCount, string lastRole, bool contentNull, string contentType);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM response: requestId={RequestId}, status={StatusCode}, bodyLength={BodyLength}")]
    private partial void LogLlmResponse(string requestId, int statusCode, int bodyLength);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM error response: requestId={RequestId}, status={StatusCode}, body={ResponseBody}")]
    private partial void LogLlmErrorResponse(string requestId, int statusCode, string responseBody);

    [LoggerMessage(Level = LogLevel.Error, Message = "LLM HTTP error: requestId={RequestId}, status={StatusCode}, body={ErrorBody}")]
    private partial void LogHttpError(string requestId, int statusCode, string errorBody);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM stream done: requestId={RequestId}")]
    private partial void LogStreamDone(string requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM SSE parse error in requestId={RequestId}: {ParseError}")]
    private partial void LogSseParseError(string requestId, string parseError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Copilot API 401 — refreshing token: provider={Provider}, requestId={RequestId}")]
    private partial void LogCopilotTokenRefresh(string provider, string requestId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Copilot API 401 token re-mint/retry failed: provider={Provider}, requestId={RequestId}, error={Error}")]
    private partial void LogCopilotRetryFailed(string provider, string requestId, string error);
}
