using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

/// <summary>
/// Anthropic Messages API wire protocol (direct api.anthropic.com or custom
/// BaseUrl), including OAuth bearer + api-key header modes and the 401/403
/// token-refresh / reload retry flows.
/// </summary>
internal sealed partial class AnthropicApiClient : IProviderApiClient
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly OAuthTokenManager tokenManager;
    private readonly ILogger logger;

    public AnthropicApiClient(
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

        using var httpClient = this.httpClientFactory.CreateClient("llm-direct");
        httpClient.BaseAddress = new Uri(GetAnthropicBaseUrl(provider));

        var (system, messages) = BuildAnthropicMessages(request.Messages);

        // Guard: Anthropic requires at least one non-system message.
        if (messages.Count == 0)
        {
            return new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "LlmContextWindowExceeded",
                ErrorMessage = "Context window exceeded: no conversation messages fit within the token budget after reserving space for the response.",
            };
        }

        var body = new AnthropicMessagesRequest
        {
            Model     = request.Model,
            Messages  = messages,
            System    = BuildAnthropicSystemBlocks(system),
            MaxTokens = request.MaxTokens > 0 ? request.MaxTokens : 32000,
        };

        if (request.Tools is { Count: > 0 })
        {
            body.Tools = BuildAnthropicTools(request.Tools);
        }

        // Ensure the access token is fresh before building the request headers
        await this.tokenManager.EnsureFreshTokenAsync(provider, cancellationToken).ConfigureAwait(false);

        var requestJson = JsonSerializer.Serialize(body, ProviderClientHelpers.JsonOptions);
        var messagesPath = GetAnthropicMessagesUri(provider);
        var requestUrl = $"{httpClient.BaseAddress}{messagesPath}";
        this.LogLlmRequest(request.RequestId, requestUrl, requestJson.Length, request.Messages.Count);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, messagesPath)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        AddAnthropicHeaders(httpRequest, provider);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            this.LogLlmErrorResponse(request.RequestId, (int)response.StatusCode, errorBody);
            this.LogHttpError(request.RequestId, (int)response.StatusCode, errorBody);

            // On error, log masked headers, HTTP version, and truncated request body
            var maskedHeaders = string.Join(", ", httpRequest.Headers
                .Select(h => h.Key == "x-api-key"
                    ? $"{h.Key}={ProviderClientHelpers.MaskToken(h.Value.FirstOrDefault() ?? "")}"
                    : $"{h.Key}={string.Join(",", h.Value)}"));
            var httpVer = response.Version.Major == 2 ? "2.0" : "1.1";
            var bodyPreview = requestJson.Length > 500
                ? requestJson[..500] + "..."
                : requestJson;
            this.LogLlmErrorContext(request.RequestId, httpVer, maskedHeaders, bodyPreview);

            // Anthropic OAuth: retry once on 401 (expired) or 403 (revoked).
            // 401 → refresh the token via Bridge (normal expiry)
            // 403 → reload from secrets.json via Bridge (another process rotated the token)
            if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                 || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                && provider.Credential.Kind == CredentialKind.AnthropicOAuth)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // Token revoked — ask Bridge to re-read secrets.json
                    await this.tokenManager.RequestTokenReloadAsync(provider, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Token expired — normal refresh flow
                    await this.tokenManager.ForceRefreshAsync(provider, cancellationToken).ConfigureAwait(false);
                }

                using var retryHttpClient = this.httpClientFactory.CreateClient("llm-direct");
                retryHttpClient.BaseAddress = new Uri(GetAnthropicBaseUrl(provider));
                using var retryRequest = new HttpRequestMessage(HttpMethod.Post, messagesPath)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(body, ProviderClientHelpers.JsonOptions), Encoding.UTF8, "application/json"),
                };
                AddAnthropicHeaders(retryRequest, provider);

                using var retryResponse = await retryHttpClient.SendAsync(
                    retryRequest, cancellationToken).ConfigureAwait(false);

                if (retryResponse.IsSuccessStatusCode)
                {
                    return ParseAnthropicCompletionResponse(
                        await retryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                        provider);
                }

                // Retry also failed — fall through to return the original error
                var retryError = await retryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                this.LogHttpError(request.RequestId, (int)retryResponse.StatusCode, retryError);
            }

            return new LlmCompletionResult
            {
                Success      = false,
                ErrorCode    = "LlmError",
                ErrorMessage = $"HTTP {(int)response.StatusCode}: {ProviderClientHelpers.TruncateError(errorBody)}",
                ProviderId   = provider.Credential.Name,
            };
        }

        var anthropicResponseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        this.LogLlmResponse(request.RequestId, (int)response.StatusCode, anthropicResponseJson.Length);
        return ParseAnthropicCompletionResponse(anthropicResponseJson, provider);
    }

    /// <summary>Parse a successful Anthropic messages API response.</summary>
    private static LlmCompletionResult ParseAnthropicCompletionResponse(string responseJson, ProviderState provider)
    {
        var anthropicResponse = JsonSerializer.Deserialize<AnthropicMessagesResponse>(responseJson, ProviderClientHelpers.JsonOptions);

        if (anthropicResponse is null)
        {
            return new LlmCompletionResult
            {
                Success      = false,
                ErrorCode    = "LlmError",
                ErrorMessage = "Failed to deserialize Anthropic response.",
                ProviderId   = provider.Credential.Name,
            };
        }

        var textContent = anthropicResponse.Content?
            .Where(c => string.Equals(c.Type, "text", StringComparison.Ordinal))
            .Select(c => c.Text)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));

        LlmToolCall[]? toolCalls = null;
        var toolUseBlocks = anthropicResponse.Content?
            .Where(c => string.Equals(c.Type, "tool_use", StringComparison.Ordinal))
            .ToList();

        if (toolUseBlocks is { Count: > 0 })
        {
            toolCalls = toolUseBlocks.Select(t => new LlmToolCall
            {
                Id        = t.Id   ?? string.Empty,
                Name      = t.Name ?? string.Empty,
                Arguments = t.Input is not null ? t.Input.Value.GetRawText() : "{}",
            }).ToArray();
        }

        return new LlmCompletionResult
        {
            Success      = true,
            Content      = textContent,
            ToolCalls    = toolCalls,
            FinishReason = anthropicResponse.StopReason,
            Usage        = MapAnthropicUsage(anthropicResponse.Usage),
            ProviderId   = provider.Credential.Name,
        };
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        ProviderState provider,
        LlmCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        this.LogStreamStart(provider.Credential.Name, request.Model, request.RequestId);

        using var httpClient = this.httpClientFactory.CreateClient("llm-direct");
        httpClient.BaseAddress = new Uri(GetAnthropicBaseUrl(provider));

        var (system, messages) = BuildAnthropicMessages(request.Messages);

        // Guard: Anthropic requires at least one non-system message.
        if (messages.Count == 0)
        {
            yield return new LlmStreamChunk
            {
                IsComplete = true,
                ErrorMessage = "Context window exceeded: no conversation messages fit within the token budget after reserving space for the response.",
            };
            yield break;
        }

        var body = new AnthropicMessagesRequest
        {
            Model     = request.Model,
            Messages  = messages,
            System    = BuildAnthropicSystemBlocks(system),
            MaxTokens = request.MaxTokens > 0 ? request.MaxTokens : 32000,
            Stream    = true,
        };

        if (request.Tools is { Count: > 0 })
        {
            body.Tools = BuildAnthropicTools(request.Tools);
        }

        // Ensure the access token is fresh before building the request headers
        await this.tokenManager.EnsureFreshTokenAsync(provider, cancellationToken).ConfigureAwait(false);

        var requestJson = JsonSerializer.Serialize(body, ProviderClientHelpers.JsonOptions);
        var messagesPath = GetAnthropicMessagesUri(provider);
        var requestUrl = $"{httpClient.BaseAddress}{messagesPath}";
        this.LogLlmRequest(request.RequestId, requestUrl, requestJson.Length, request.Messages.Count);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, messagesPath)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        AddAnthropicHeaders(httpRequest, provider);

        HttpResponseMessage? response = null;
        string? httpError = null;
        try
        {
            response = await httpClient.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
            var statusCode = (int)response.StatusCode;
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            this.LogHttpError(request.RequestId, statusCode, errorBody);

            // On error, log masked headers, HTTP version, and truncated request body
            var maskedHeaders = string.Join(", ", httpRequest.Headers
                .Select(h => h.Key == "x-api-key"
                    ? $"{h.Key}={ProviderClientHelpers.MaskToken(h.Value.FirstOrDefault() ?? "")}"
                    : $"{h.Key}={string.Join(",", h.Value)}"));
            var httpVer = response.Version.Major == 2 ? "2.0" : "1.1";
            var bodyPreview = requestJson.Length > 500
                ? requestJson[..500] + "..."
                : requestJson;
            this.LogLlmErrorContext(request.RequestId, httpVer, maskedHeaders, bodyPreview);

            // Anthropic OAuth: retry once on 401 (expired) or 403 (revoked).
            if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                 || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                && provider.Credential.Kind == CredentialKind.AnthropicOAuth)
            {
                var wasRevoked = response.StatusCode == System.Net.HttpStatusCode.Forbidden;
                response.Dispose();
                response = null;

                if (wasRevoked)
                {
                    await this.tokenManager.RequestTokenReloadAsync(provider, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await this.tokenManager.ForceRefreshAsync(provider, cancellationToken).ConfigureAwait(false);
                }

                using var retryHttpClient = this.httpClientFactory.CreateClient("llm-direct");
                retryHttpClient.BaseAddress = new Uri(GetAnthropicBaseUrl(provider));
                using var retryRequest = new HttpRequestMessage(HttpMethod.Post, messagesPath)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(body, ProviderClientHelpers.JsonOptions), Encoding.UTF8, "application/json"),
                };
                AddAnthropicHeaders(retryRequest, provider);
                retryRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                HttpResponseMessage? retryResponse = null;
                string? retryHttpError = null;
                try
                {
                    retryResponse = await retryHttpClient.SendAsync(
                        retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    retryHttpError = ex.Message;
                }

                if (retryHttpError is not null)
                {
                    yield return new LlmStreamChunk { IsComplete = true, ErrorMessage = retryHttpError };
                    yield break;
                }

                if (retryResponse!.IsSuccessStatusCode)
                {
                    // Stream from the retry response
                    using var retryStream = await retryResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var retryReader = new StreamReader(retryStream, Encoding.UTF8);
                    await foreach (var chunk in ParseAnthropicSseAsync(retryReader, request.RequestId, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        yield return chunk;
                    }
                    retryResponse.Dispose();
                    yield break;
                }

                // Retry also failed — use the retry error details
                statusCode = (int)retryResponse.StatusCode;
                errorBody = await retryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                this.LogHttpError(request.RequestId, statusCode, errorBody);
                retryResponse.Dispose();
            }
            else
            {
                response.Dispose();
            }

            yield return new LlmStreamChunk
            {
                IsComplete   = true,
                ErrorMessage = $"HTTP {statusCode}: {ProviderClientHelpers.TruncateError(errorBody)}",
            };
            yield break;
        }

        using var stream = await response!.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var chunk in ParseAnthropicSseAsync(reader, request.RequestId, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }

        response.Dispose();
    }

    private async IAsyncEnumerable<LlmStreamChunk> ParseAnthropicSseAsync(
        StreamReader reader,
        string requestId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? currentToolId   = null;
        string? currentToolName = null;
        int currentToolIndex    = -1;
        int toolIndexCounter    = 0;
        int inputTokens = 0;
        int cacheWriteTokens = 0;
        int cacheReadTokens = 0;

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

            AnthropicSseEvent? evt = null;
            try
            {
                evt = JsonSerializer.Deserialize<AnthropicSseEvent>(data, ProviderClientHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                this.LogSseParseError(requestId, ex.Message);
                continue;
            }

            if (evt is null)
            {
                continue;
            }

            switch (evt.Type)
            {
                case "message_start":
                    inputTokens = evt.Message?.Usage?.InputTokens ?? 0;
                    cacheWriteTokens = evt.Message?.Usage?.CacheCreationInputTokens ?? 0;
                    cacheReadTokens = evt.Message?.Usage?.CacheReadInputTokens ?? 0;
                    break;

                case "content_block_start":
                    var startBlock = evt.ContentBlock;
                    if (startBlock is not null && string.Equals(startBlock.Type, "tool_use", StringComparison.Ordinal))
                    {
                        currentToolId    = startBlock.Id;
                        currentToolName  = startBlock.Name;
                        currentToolIndex = toolIndexCounter++;
                        yield return new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta
                                {
                                    Index = currentToolIndex,
                                    Id    = currentToolId ?? string.Empty,
                                    Name  = currentToolName,
                                },
                            ],
                        };
                    }
                    break;

                case "content_block_delta":
                    if (evt.Delta is null)
                    {
                        break;
                    }
                    if (string.Equals(evt.Delta.Type, "text_delta", StringComparison.Ordinal)
                        && evt.Delta.Text is not null)
                    {
                        yield return new LlmStreamChunk { ContentDelta = evt.Delta.Text };
                    }
                    else if (string.Equals(evt.Delta.Type, "input_json_delta", StringComparison.Ordinal)
                        && evt.Delta.PartialJson is not null)
                    {
                        yield return new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta
                                {
                                    Index          = currentToolIndex,
                                    Id             = null,
                                    ArgumentsDelta = evt.Delta.PartialJson,
                                },
                            ],
                        };
                    }
                    break;

                case "content_block_stop":
                    currentToolId    = null;
                    currentToolName  = null;
                    currentToolIndex = -1;
                    break;

                case "message_delta":
                    yield return new LlmStreamChunk
                    {
                        IsComplete   = true,
                        FinishReason = evt.Delta?.StopReason,
                        Usage = evt.Usage is not null ? new LlmTokenUsage
                        {
                            PromptTokens     = inputTokens,
                            CompletionTokens = evt.Usage.OutputTokens,
                            TotalTokens      = inputTokens + evt.Usage.OutputTokens
                                               + cacheWriteTokens + cacheReadTokens,
                            CacheWriteTokens = cacheWriteTokens,
                            CacheReadTokens  = cacheReadTokens,
                        } : null,
                    };
                    yield break;

                case "message_stop":
                    yield return new LlmStreamChunk { IsComplete = true };
                    yield break;

                case "error":
                    var streamErrorMessage = evt.Error?.Message ?? "Unknown Anthropic stream error.";
                    this.LogLlmStreamError(requestId, streamErrorMessage);
                    yield return new LlmStreamChunk
                    {
                        IsComplete   = true,
                        ErrorMessage = streamErrorMessage,
                    };
                    yield break;
            }
        }
    }

    /// <summary>
    /// Builds Anthropic system prompt as content blocks with cache_control on the last block.
    /// Returns null if system is empty (Anthropic requires non-empty system).
    /// </summary>
    private static List<AnthropicSystemBlock>? BuildAnthropicSystemBlocks(string? system)
    {
        if (string.IsNullOrEmpty(system))
        {
            return null;
        }

        return
        [
            new AnthropicSystemBlock
            {
                Text = system,
                CacheControl = new AnthropicCacheControl(),
            },
        ];
    }

    /// <summary>
    /// Builds Anthropic tool definitions with cache_control on the last tool.
    /// This ensures the system prompt + all tool schemas form a cacheable prefix.
    /// </summary>
    private static List<AnthropicTool> BuildAnthropicTools(IReadOnlyList<LlmToolDefinition> tools)
    {
        var result = new List<AnthropicTool>(tools.Count);
        for (var i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            var tool = new AnthropicTool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = JsonSerializer.Deserialize<JsonElement>(t.ParametersSchema),
            };

            // Mark the last tool with cache_control so the entire prefix
            // (system + all tools) is cached as a unit
            if (i == tools.Count - 1)
            {
                tool.CacheControl = new AnthropicCacheControl();
            }

            result.Add(tool);
        }

        return result;
    }

    /// <summary>
    /// Returns the base URL for Anthropic API requests.
    /// </summary>
    private static string GetAnthropicBaseUrl(ProviderState provider)
    {
        return (provider.Credential.BaseUrl?.TrimEnd('/') ?? "https://api.anthropic.com") + "/";
    }

    /// <summary>
    /// Returns the messages API path string, with ?beta=true appended for OAuth tokens.
    /// </summary>
    private static string GetAnthropicMessagesUri(ProviderState provider)
    {
        return provider.Credential.Kind is CredentialKind.AnthropicOAuth or CredentialKind.AnthropicSetupToken
            ? "v1/messages?beta=true"
            : "v1/messages";
    }

    private static void AddAnthropicHeaders(HttpRequestMessage request, ProviderState provider)
    {
        // Anthropic OAuth requires HTTP/2 for Claude 4 models
        request.Version = System.Net.HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;

        request.Headers.Add("anthropic-version", "2023-06-01");

        if (provider.Credential.Kind is CredentialKind.AnthropicOAuth or CredentialKind.AnthropicSetupToken)
        {
            // OAuth tokens (both PKCE and setup tokens from `claude setup-token`):
            // - Authorization: Bearer (NOT x-api-key)
            // - anthropic-beta must include "oauth-2025-04-20"
            // - URL must have ?beta=true appended
            // - user-agent set to claude-cli format
            // Matches opencode's opencode-anthropic-auth plugin behavior.
            var token = provider.CurrentAccessToken ?? string.Empty;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta",
                "oauth-2025-04-20,interleaved-thinking-2025-05-14,claude-code-20250219,fine-grained-tool-streaming-2025-05-14");
            request.Headers.Add("user-agent", "claude-cli/2.1.2 (external, cli)");

            // NOTE: ?beta=true is appended at request construction site, not here.
            // See GetAnthropicMessagesUri().
        }
        else
        {
            // Static API key: standard x-api-key header
            request.Headers.Add("anthropic-beta",
                "claude-code-20250219,interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14");
            request.Headers.Add("x-api-key", provider.Credential.ApiKey ?? string.Empty);
        }
    }

    internal static (string? System, List<AnthropicMessage> Messages) BuildAnthropicMessages(
        IReadOnlyList<LlmMessage> messages)
    {
        string? system = null;
        var result = new List<AnthropicMessage>();

        foreach (var msg in messages)
        {
            if (string.Equals(msg.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                system = string.IsNullOrEmpty(system) ? msg.Content : system + "\n\n" + msg.Content;
                continue;
            }

            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                var block = new AnthropicContentBlock
                {
                    Type      = "tool_result",
                    ToolUseId = msg.ToolCallId,
                    Content   = msg.Content ?? string.Empty,
                };
                // Merge consecutive tool results into one user turn (Anthropic requires alternating roles)
                if (result.Count > 0
                    && result[^1].Role == "user"
                    && result[^1].Content.Count > 0
                    && result[^1].Content[^1].Type == "tool_result")
                {
                    result[^1].Content.Add(block);
                }
                else
                {
                    result.Add(new AnthropicMessage { Role = "user", Content = [block] });
                }
                continue;
            }

            if (msg.ToolCalls is { Count: > 0 })
            {
                var blocks = new List<AnthropicContentBlock>();
                if (!string.IsNullOrEmpty(msg.Content))
                {
                    blocks.Add(new AnthropicContentBlock { Type = "text", Text = msg.Content });
                }

                foreach (var tc in msg.ToolCalls)
                {
                    JsonElement input;
                    try   { input = JsonSerializer.Deserialize<JsonElement>(tc.Arguments); }
                    catch { input = JsonSerializer.Deserialize<JsonElement>("{}"); }

                    blocks.Add(new AnthropicContentBlock
                    {
                        Type  = "tool_use",
                        Id    = tc.Id,
                        Name  = tc.Name,
                        Input = input,
                    });
                }
                result.Add(new AnthropicMessage { Role = "assistant", Content = blocks });
                continue;
            }

            // Build content blocks — use multimodal ContentBlocks if present, else fall back to text
            if (msg.ContentBlocks is { Count: > 0 })
            {
                var blocks = new List<AnthropicContentBlock>();
                foreach (var block in msg.ContentBlocks)
                {
                    if (block.Type == "image" && block.ImageData is not null && block.ImageMediaType is not null)
                    {
                        blocks.Add(new AnthropicContentBlock
                        {
                            Type = "image",
                            Source = new AnthropicImageSource
                            {
                                Type = "base64",
                                MediaType = block.ImageMediaType,
                                Data = block.ImageData,
                            },
                        });
                    }
                    else if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                    {
                        blocks.Add(new AnthropicContentBlock { Type = "text", Text = block.Text });
                    }
                }

                if (blocks.Count > 0)
                {
                    // Merge consecutive same-role messages
                    if (result.Count > 0
                        && string.Equals(result[^1].Role, msg.Role, StringComparison.OrdinalIgnoreCase))
                    {
                        result[^1].Content.AddRange(blocks);
                    }
                    else
                    {
                        result.Add(new AnthropicMessage { Role = msg.Role, Content = blocks });
                    }
                    continue;
                }
            }

            // Anthropic rejects empty text content blocks ("text content blocks must be non-empty").
            // For assistant messages with no content, skip entirely — they are artifacts of
            // failed generations. For user messages, use a placeholder to maintain the required
            // alternating user/assistant turn structure.
            if (string.IsNullOrEmpty(msg.Content))
            {
                if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // User messages must not be skipped — Anthropic requires the conversation
                // to end with a user message. Use a minimal placeholder.
                // Merge into previous if same role.
                if (result.Count > 0
                    && string.Equals(result[^1].Role, msg.Role, StringComparison.OrdinalIgnoreCase))
                {
                    result[^1].Content.Add(new AnthropicContentBlock { Type = "text", Text = "..." });
                }
                else
                {
                    result.Add(new AnthropicMessage
                    {
                        Role    = msg.Role,
                        Content = [new AnthropicContentBlock { Type = "text", Text = "..." }],
                    });
                }
                continue;
            }

            // Merge consecutive same-role messages into one turn.
            // Anthropic requires strictly alternating user/assistant roles.
            // In Discord, a user can send multiple messages before the agent responds.
            if (result.Count > 0
                && string.Equals(result[^1].Role, msg.Role, StringComparison.OrdinalIgnoreCase))
            {
                var textBlock = new AnthropicContentBlock { Type = "text", Text = msg.Content };
                result[^1].Content.Add(textBlock);
                continue;
            }

            result.Add(new AnthropicMessage
            {
                Role    = msg.Role,
                Content = [new AnthropicContentBlock { Type = "text", Text = msg.Content }],
            });
        }

        return (system, result);
    }

    private static LlmTokenUsage? MapAnthropicUsage(AnthropicUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new LlmTokenUsage
        {
            PromptTokens     = usage.InputTokens,
            CompletionTokens = usage.OutputTokens,
            TotalTokens      = usage.InputTokens + usage.OutputTokens
                               + usage.CacheCreationInputTokens + usage.CacheReadInputTokens,
            CacheWriteTokens = usage.CacheCreationInputTokens,
            CacheReadTokens  = usage.CacheReadInputTokens,
        };
    }

    // ── LoggerMessage ────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM completion start: provider={Provider}, model={Model}, requestId={RequestId}")]
    private partial void LogCompletionStart(string provider, string model, string requestId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM stream start: provider={Provider}, model={Model}, requestId={RequestId}")]
    private partial void LogStreamStart(string provider, string model, string requestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM request: requestId={RequestId}, url={Url}, bodyLength={BodyLength}, messageCount={MessageCount}")]
    private partial void LogLlmRequest(string requestId, string url, int bodyLength, int messageCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM response: requestId={RequestId}, status={StatusCode}, bodyLength={BodyLength}")]
    private partial void LogLlmResponse(string requestId, int statusCode, int bodyLength);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM error response: requestId={RequestId}, status={StatusCode}, body={ResponseBody}")]
    private partial void LogLlmErrorResponse(string requestId, int statusCode, string responseBody);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM stream error: requestId={RequestId}, errorMessage={StreamError}")]
    private partial void LogLlmStreamError(string requestId, string streamError);

    [LoggerMessage(Level = LogLevel.Error, Message = "LLM HTTP error: requestId={RequestId}, status={StatusCode}, body={ErrorBody}")]
    private partial void LogHttpError(string requestId, int statusCode, string errorBody);

    [LoggerMessage(Level = LogLevel.Error, Message = "LLM error context: requestId={RequestId}, httpVersion={HttpVersion}, headers=[{Headers}], bodyPreview={BodyPreview}")]
    private partial void LogLlmErrorContext(string requestId, string httpVersion, string headers, string bodyPreview);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM SSE parse error in requestId={RequestId}: {ParseError}")]
    private partial void LogSseParseError(string requestId, string parseError);
}
