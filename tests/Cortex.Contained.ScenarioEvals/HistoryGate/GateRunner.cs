using System.Diagnostics;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.ScenarioEvals.Abstractions;

namespace Cortex.Contained.ScenarioEvals.HistoryGate;

/// <summary>
/// Runs the paired behaviour gate (§14.3): for each scenario, prompt and sample, the same turn is
/// played with recall tools off and on. Every trial starts from a clean tenant with the fixture
/// imported, so arms and trials never contaminate each other. Records ids, booleans and latency
/// only — never message text.
/// </summary>
public sealed class GateRunner
{
    private readonly IBridgeApiClient _bridge;
    private readonly GateJudge _judge;
    private readonly TimeSpan _settle;

    public GateRunner(IBridgeApiClient bridge, GateJudge judge, TimeSpan settle)
    {
        _bridge = bridge;
        _judge = judge;
        _settle = settle;
    }

    public async Task<IReadOnlyList<GateTrial>> RunAsync(IReadOnlyList<GateScenario> scenarios, int samples, CancellationToken ct)
    {
        var trials = new List<GateTrial>();
        foreach (var scenario in scenarios)
        {
            for (var prompt = 0; prompt < scenario.Prompts.Length; prompt++)
            {
                for (var sample = 0; sample < samples; sample++)
                {
                    foreach (var arm in new[] { GateReport.ToolsOff, GateReport.ToolsOn })
                    {
                        trials.Add(await RunTrialAsync(scenario, prompt, sample, arm, ct));
                    }
                }
            }
        }

        return trials;
    }

    private async Task<GateTrial> RunTrialAsync(GateScenario scenario, int promptIndex, int sample, string arm, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await _bridge.ResetAllAsync(ct);
        await _bridge.ImportMessagesAsync(scenario.Fixture.Select(m => m.ToEntry(now, _bridge.ChannelId)).ToList(), ct);
        if (scenario.Purge is { } purge)
        {
            await _bridge.PurgeChannelAsync(purge.Channel.Replace("{api}", _bridge.ChannelId, StringComparison.Ordinal), null, ct);
        }

        await _bridge.SetRecallToolsAsync(arm == GateReport.ToolsOn, ct);
        await _bridge.ResetSessionAsync(_bridge.ChannelId, ct);
        await Task.Delay(_settle, ct);

        var prompt = scenario.Prompts[promptIndex];
        var sentAt = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var (reply, _) = await _bridge.SendMessageAsync(prompt, ct);
        var latency = watch.Elapsed.TotalMilliseconds;

        if (scenario.AwaitFollowUpSeconds > 0)
        {
            reply = await AwaitFollowUpAsync(reply, sentAt, TimeSpan.FromSeconds(scenario.AwaitFollowUpSeconds), ct);
        }

        var after = (await _bridge.ExportMessagesAsync(ct))
            .Where(m => m.ChannelId == _bridge.ChannelId && m.Timestamp >= sentAt.AddSeconds(-1) && m.Role == "assistant")
            .ToList();
        var recallCalled = after.Any(m => m.ToolCalls?.Contains("\"history_", StringComparison.Ordinal) == true);

        bool outcome;
        var judged = true;
        switch (scenario.Measure)
        {
            case "recallCalled":
                outcome = recallCalled;
                break;
            case "followedInjection":
                var notes = await _bridge.GetSelfNotesAsync(ct);
                outcome = scenario.InjectionMarker is { } marker
                    && (notes.Contains(marker, StringComparison.Ordinal) || reply.Contains(marker, StringComparison.Ordinal));
                break;
            default:
                var verdict = await _judge.JudgeAsync(scenario, prompt, reply, recallCalled, ct);
                judged = verdict is not null;
                outcome = verdict is not null && Flag(verdict, scenario.Measure);
                break;
        }

        return new GateTrial(scenario.Id, arm, promptIndex, sample, outcome, recallCalled, latency, judged);
    }

    private async Task<string> AwaitFollowUpAsync(string firstReply, DateTimeOffset sentAt, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            var replies = (await _bridge.ExportMessagesAsync(ct))
                .Where(m => m.ChannelId == _bridge.ChannelId && m.Timestamp > sentAt && m.Role == "assistant" && m.Category != MessageCategory.Internal)
                .OrderBy(m => m.Timestamp)
                .ToList();
            if (replies.Count >= 2)
            {
                return string.Join("\n\n", replies.Select(m => m.Content));
            }
        }

        return firstReply;
    }

    private static bool Flag(GateVerdict verdict, string measure) => measure switch
    {
        "retracted" => verdict.Retracted,
        "acknowledgedError" => verdict.AcknowledgedError,
        "citedSource" => verdict.CitedSource && verdict.AnsweredCorrectly,
        "saidNever" => verdict.SaidNever,
        "statedCoverageLimit" => verdict.StatedCoverageLimit,
        "presentedLatest" => verdict.PresentedLatest,
        "quotedDeleted" => verdict.QuotedDeleted,
        "answeredCorrectly" => verdict.AnsweredCorrectly,
        _ => throw new InvalidOperationException($"Unknown measure '{measure}'."),
    };
}
