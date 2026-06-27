using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// <see cref="IImageDescriber"/> backed by <see cref="ILlmClient"/>. Uses
/// <see cref="IModelProvider.MemoryModel"/> (the cheap-model slot) so image
/// descriptions don't tax the primary chat model. Returns null on any failure.
/// </summary>
public sealed partial class LlmImageDescriber : IImageDescriber
{
    private readonly ILlmClient llmClient;
    private readonly IModelProvider modelProvider;
    private readonly ILogger<LlmImageDescriber> logger;

    private static readonly TimeSpan DescribeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DescribeVerboseTimeout = TimeSpan.FromSeconds(40);

    private const string BriefSystemPrompt =
        "Describe this image in 1-3 sentences. Focus on content (what's shown, " +
        "any text visible, relevant details). No preamble.";

    private const string VerboseSystemPrompt =
        "Describe this image in detail so a reader who cannot see the image " +
        "could still answer questions about it. Cover: main subject, any " +
        "visible text (transcribe it), countable items and their quantities, " +
        "colors, spatial layout, and anything distinctive that a user might " +
        "reasonably ask about later. Write 5-8 sentences. No preamble, no " +
        "hedging like \"the image appears to show\".";

    public LlmImageDescriber(ILlmClient llmClient, IModelProvider modelProvider, ILogger<LlmImageDescriber> logger)
    {
        this.llmClient = llmClient;
        this.modelProvider = modelProvider;
        this.logger = logger;
    }

    public ValueTask<string?> DescribeAsync(byte[] imageData, string mediaType, CancellationToken ct)
        => DescribeCoreAsync(imageData, mediaType, BriefSystemPrompt, TokenLimits.Small, DescribeTimeout, ct);

    public ValueTask<string?> DescribeVerboseAsync(byte[] imageData, string mediaType, CancellationToken ct)
        => DescribeCoreAsync(imageData, mediaType, VerboseSystemPrompt, TokenLimits.Medium, DescribeVerboseTimeout, ct);

    private async ValueTask<string?> DescribeCoreAsync(
        byte[] imageData,
        string mediaType,
        string systemPrompt,
        int maxTokens,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var request = new LlmCompletionRequest
            {
                Model = this.modelProvider.MemoryModel,
                Temperature = 0.2,
                MaxTokens = maxTokens,
                RequestId = Guid.NewGuid().ToString("N"),
                ConversationId = "image-describer",
                Messages =
                [
                    new LlmMessage { Role = "system", Content = systemPrompt },
                    new LlmMessage
                    {
                        Role = "user",
                        ContentBlocks =
                        [
                            LlmContentBlock.ImageBlock(Convert.ToBase64String(imageData), mediaType),
                        ],
                    },
                ],
            };

            var result = await this.llmClient.CompleteAsync(request, cts.Token).ConfigureAwait(false);

            if (!result.Success)
            {
                this.LogDescribeFailed(mediaType, result.ErrorMessage ?? "(no error message)");
                return null;
            }

            var text = result.Content?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                this.LogDescribeEmpty(mediaType);
                return null;
            }

            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            this.LogDescribeTimeout(mediaType);
            return null;
        }
        catch (Exception ex)
        {
            this.LogDescribeException(ex, mediaType);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image describe failed ({MediaType}): {ErrorMessage}")]
    private partial void LogDescribeFailed(string mediaType, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image describe returned empty content ({MediaType})")]
    private partial void LogDescribeEmpty(string mediaType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image describe timed out ({MediaType})")]
    private partial void LogDescribeTimeout(string mediaType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image describe exception ({MediaType})")]
    private partial void LogDescribeException(Exception ex, string mediaType);
}
