using System.Globalization;
using System.Text;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Agent.Autonomy;

/// <summary>
/// Asks the run's model whether an autonomous goal is actually complete.
/// <para>
/// The judge fails open by design: every model-call failure or ambiguous reply becomes a
/// continuation so a broken judge cannot silently declare success.
/// </para>
/// </summary>
internal sealed partial class CompletionJudge
{
    private const string FailOpenRemaining =
        "The completion judge did not return an unambiguous DONE verdict. Continue working toward the goal.";

    private const string SystemPrompt = """
        You judge whether an autonomous subagent has genuinely completed its assigned goal.

        Compare the transcript against the goal. Reply with exactly one of:
        DONE
        CONTINUE: <what is still missing>

        Say DONE only when the goal is actually satisfied by the transcript. If anything material
        is missing, unclear, blocked, unverified, or merely claimed without evidence, say CONTINUE
        and describe the missing work.
        """;

    private readonly ILlmClient llmClient;
    private readonly IModelProvider modelProvider;
    private readonly ILogger<CompletionJudge> logger;

    public CompletionJudge(
        ILlmClient llmClient,
        IModelProvider modelProvider,
        ILogger<CompletionJudge> logger)
    {
        this.llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        this.modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static IReadOnlyList<LlmMessage> BuildMessages(
        string goal,
        IReadOnlyList<LlmMessage> transcript)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(transcript);

        var user = new StringBuilder();
        user.AppendLine("Goal:");
        user.AppendLine(goal);
        user.AppendLine();
        user.AppendLine("Transcript:");

        foreach (var message in transcript)
        {
            user.AppendLine(
                CultureInfo.InvariantCulture,
                $"{message.Role}: {message.Content}");
        }

        return
        [
            new LlmMessage { Role = "system", Content = SystemPrompt },
            new LlmMessage { Role = "user", Content = user.ToString() },
        ];
    }

    public static GoalVerdict ParseReply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return GoalVerdict.Continue(FailOpenRemaining);
        }

        var sawDone = false;
        var doneCount = 0;
        var continueCount = 0;
        string? remaining = null;

        foreach (var rawLine in reply.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(line, "DONE", StringComparison.OrdinalIgnoreCase))
            {
                sawDone = true;
                doneCount++;
                continue;
            }

            const string ContinuePrefix = "CONTINUE:";
            if (line.StartsWith(ContinuePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continueCount++;
                remaining ??= line[ContinuePrefix.Length..].Trim();
            }
        }

        if (sawDone && doneCount == 1 && continueCount == 0)
        {
            return GoalVerdict.Stop(GoalOutcome.Met, "Completion judge replied DONE.");
        }

        if (continueCount > 0 && !string.IsNullOrWhiteSpace(remaining))
        {
            return GoalVerdict.Continue(remaining);
        }

        return GoalVerdict.Continue(FailOpenRemaining);
    }

    public async Task<GoalVerdict> EvaluateAsync(
        string goal,
        IReadOnlyList<LlmMessage> transcript,
        CancellationToken cancellationToken)
    {
        var request = new LlmCompletionRequest
        {
            Model = this.modelProvider.DefaultModel,
            Messages = BuildMessages(goal, transcript),
            Temperature = 0.0,
            MaxTokens = TokenLimits.Small,
            RequestId = Guid.NewGuid().ToString("N"),
            ConversationId = "autonomy-completion-judge",
        };

        try
        {
            var result = await this.llmClient.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
            {
                this.LogJudgeFailedOpen(result.ErrorMessage ?? "empty response");
                return GoalVerdict.Continue(FailOpenRemaining);
            }

            return ParseReply(result.Content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Fail-open judge: any model-call failure must continue the run.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogJudgeException(ex.Message);
            return GoalVerdict.Continue(FailOpenRemaining);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Completion judge failed open: {Reason}")]
    private partial void LogJudgeFailedOpen(string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Completion judge threw and failed open: {Reason}")]
    private partial void LogJudgeException(string reason);
}
