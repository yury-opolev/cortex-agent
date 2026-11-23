using System.Runtime.CompilerServices;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

/// <summary>
/// A scripted <see cref="ILlmClient"/> for labelling tests (never calls a provider): answers come from
/// <see cref="Respond"/> per request, every request is recorded, and the answering provider is <c>copilot</c>.
/// </summary>
internal sealed class FakeLabellingLlm : ILlmClient
{
    public const string Provider = "copilot";

    private readonly Lock sync = new();

    /// <summary>Answer for a request (model, system prompt, user prompt) → content; null = failed call.</summary>
    public Func<string, string, string, string?> Respond { get; set; } = (_, _, _) => null;

    public List<(string Model, string System, string User)> Requests { get; } = [];

    /// <summary>The requests as sent (flags included).</summary>
    public List<LlmCompletionRequest> Sent { get; } = [];

    /// <summary>The provider id put on answers (another value simulates a failover).</summary>
    public string AnsweringProvider { get; set; } = Provider;

    /// <summary>Provider lookup for <c>LabellingLlm</c>: every model is served by <see cref="Provider"/>.</summary>
    public static string? ServesAll(string model) => Provider;

    public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken)
    {
        var system = request.Messages[0].Content ?? string.Empty;
        var user = request.Messages[^1].Content ?? string.Empty;
        string? content;
        lock (this.sync)
        {
            this.Requests.Add((request.Model, system, user));
            this.Sent.Add(request);
            content = this.Respond(request.Model, system, user);
        }

        return Task.FromResult(content is null
            ? new LlmCompletionResult { Success = false, ErrorMessage = "HTTP 503: unavailable", ProviderId = this.AnsweringProvider }
            : new LlmCompletionResult { Success = true, Content = content, ProviderId = this.AnsweringProvider });
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamCompleteAsync(LlmCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
