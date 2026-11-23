using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>Thrown when a label job reaches <see cref="LabellingOptions.MaxCalls"/>; its checkpoint lets the next run continue.</summary>
public sealed class LabellingBudgetExceededException : Exception
{
    public LabellingBudgetExceededException()
    {
    }

    public LabellingBudgetExceededException(string message)
        : base(message)
    {
    }

    public LabellingBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when no answer could be obtained from a model because the provider kept failing (HTTP errors, rate limits,
/// a failover rejection): not the model's fault, so nothing is marked failed and the job stops, resumable. The
/// message holds the status only, never text.
/// </summary>
public sealed class LabellingTransportException : Exception
{
    public LabellingTransportException()
    {
    }

    public LabellingTransportException(string message)
        : base(message)
    {
    }

    public LabellingTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The labelling job's only way to an LLM: one pinned model per call through the configured provider, strict JSON
/// answers (asked again when unparsable or invalid), exponential backoff on failures, a minimum interval between
/// calls and a hard cap on the number of calls. Calls are pinned (<c>LlmCompletionRequest.PinnedProvider</c>: no
/// failover, no hidden retries, so every attempt is counted); an answer from another provider than the one that lists
/// the model still does not count as an answer: the model family would no longer be the configured one (§4.2).
/// A provider that keeps failing is a <see cref="LabellingTransportException"/>, not "no answer".
/// Prompts and answers are never logged.
/// </summary>
public sealed partial class LabellingLlm
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILlmClient llm;
    private readonly Func<string, string?> providerFor;
    private readonly LabellingOptions options;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeProvider time;
    private readonly Lock sync = new();
    private DateTimeOffset nextCall = DateTimeOffset.MinValue;
    private int calls;

    /// <param name="llm">The agent's LLM client.</param>
    /// <param name="providerFor">Provider that lists a model, or null (<c>DirectLlmClient.ProviderFor</c>).</param>
    /// <param name="options">Caps and pacing.</param>
    /// <param name="callsSoFar">Calls an earlier, interrupted run of the same job already made (they count against the cap).</param>
    /// <param name="delay">Waits (tests pass a recorder).</param>
    /// <param name="time">Clock for pacing.</param>
    public LabellingLlm(
        ILlmClient llm,
        Func<string, string?> providerFor,
        LabellingOptions options,
        int callsSoFar = 0,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? time = null)
    {
        this.llm = llm;
        this.providerFor = providerFor;
        this.options = options;
        this.calls = callsSoFar;
        this.delay = delay ?? Task.Delay;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Calls made so far, including those of earlier runs.</summary>
    public int Calls
    {
        get
        {
            lock (this.sync)
            {
                return this.calls;
            }
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> unless every model in <paramref name="models"/> is listed by a provider.</summary>
    public void EnsureServed(params string[] models)
    {
        ArgumentNullException.ThrowIfNull(models);
        foreach (var model in models)
        {
            if (this.providerFor(model) is null)
            {
                throw new InvalidOperationException(
                    $"No configured LLM provider lists the model '{model}': add it to the provider's models in the Bridge (cortex.yml llmProviders) or change Evaluation:Labelling.");
            }
        }
    }

    /// <summary>
    /// Asks <paramref name="model"/> and parses its answer as <typeparamref name="T"/>; null when the model gave no
    /// valid answer within <see cref="LabellingOptions.MaxAttempts"/> attempts. Throws
    /// <see cref="LabellingTransportException"/> when no valid answer came and an attempt failed in transport (the
    /// provider, not the model, is the problem) and <see cref="LabellingBudgetExceededException"/> at the cap.
    /// </summary>
    public async Task<T?> CompleteJsonAsync<T>(string model, string system, string user, int maxTokens, Func<T, bool> valid, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(valid);
        var expectedProvider = this.providerFor(model);
        string? transportError = null;
        for (var attempt = 0; attempt < this.options.MaxAttempts; attempt++)
        {
            var result = await this.CallAsync(model, system, user, maxTokens, cancellationToken).ConfigureAwait(false);
            if (!result.Success || (result.ProviderId is { } provider && !string.Equals(provider, expectedProvider, StringComparison.Ordinal)))
            {
                transportError = result.Success ? "answered by another provider" : Status(result.ErrorMessage);
                await this.delay(Backoff(attempt, result.ErrorMessage), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (Parse<T>(result.Content) is { } parsed && valid(parsed))
            {
                return parsed;
            }
        }

        return transportError is null
            ? null
            : throw new LabellingTransportException($"The LLM provider kept failing for model '{model}' ({transportError}).");
    }

    // "HTTP 429" for an HTTP error, otherwise a generic label: provider messages may carry body text.
    private static string Status(string? error) =>
        error is not null && HttpStatus().Match(error) is { Success: true } status ? "HTTP " + status.Groups[1].Value : "transport error";

    [GeneratedRegex(@"^HTTP (\d{3})\b")]
    private static partial Regex HttpStatus();

    /// <summary>Waits between failed attempts: 2 s, 4 s, 8 s … (×4 for rate limiting), at most 2 minutes.</summary>
    public static TimeSpan Backoff(int attempt, string? error)
    {
        var seconds = Math.Min(120, 2 * Math.Pow(2, attempt) * (error?.Contains("429", StringComparison.Ordinal) == true ? 4 : 1));
        return TimeSpan.FromSeconds(seconds);
    }

    private static T? Parse<T>(string? content)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(MemoryConsolidationService.StripToJson(content), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<LlmCompletionResult> CallAsync(string model, string system, string user, int maxTokens, CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (this.sync)
        {
            if (this.calls >= this.options.MaxCalls)
            {
                throw new LabellingBudgetExceededException(string.Create(CultureInfo.InvariantCulture,
                    $"The label job reached its cap of {this.options.MaxCalls} LLM calls (Evaluation:Labelling:MaxCalls)."));
            }

            // Reserve the next slot: calls never start closer together than MinCallIntervalMs.
            var now = this.time.GetUtcNow();
            var slot = this.nextCall > now ? this.nextCall : now;
            this.nextCall = slot + TimeSpan.FromMilliseconds(this.options.MinCallIntervalMs);
            wait = slot - now;
            this.calls++;
        }

        if (wait > TimeSpan.Zero)
        {
            await this.delay(wait, cancellationToken).ConfigureAwait(false);
        }

        return await this.llm.CompleteAsync(
            new LlmCompletionRequest
            {
                Model = model,
                Messages = [new LlmMessage { Role = "system", Content = system }, new LlmMessage { Role = "user", Content = user }],
                Temperature = 0,
                MaxTokens = maxTokens,
                RequestId = "eval-label-" + Guid.NewGuid().ToString("N"),
                ConversationId = "eval-labelling",
                PinnedProvider = true,
            },
            cancellationToken).ConfigureAwait(false);
    }
}
