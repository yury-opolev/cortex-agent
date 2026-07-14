using System.Text.Json;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Contracts.Tests.Observability;

public class ObservabilityDtoTests
{
    // ── SubagentWorkerSnapshot: no sensitive content, round-trips ──────────

    [Fact]
    public void WorkerSnapshot_RoundTripsThroughSystemTextJson()
    {
        var snapshot = new SubagentWorkerSnapshot
        {
            TaskId = "sa-1",
            ParentConversationId = "conv-1",
            ParentChannelId = "webchat-default",
            Description = "Summarize the PR",
            State = "running",
            CreatedAt = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            StartedAt = new DateTimeOffset(2026, 7, 14, 12, 0, 1, TimeSpan.Zero),
            LastProgressAt = new DateTimeOffset(2026, 7, 14, 12, 0, 5, TimeSpan.Zero),
            CompletedAt = null,
            DurationMs = 5000,
            StalenessMs = 1000,
            RestartCount = 1,
            Rounds = 3,
            IsStale = false,
        };

        var json = JsonSerializer.Serialize(snapshot);
        var roundTripped = JsonSerializer.Deserialize<SubagentWorkerSnapshot>(json);

        Assert.Equal(snapshot, roundTripped);
    }

    [Fact]
    public void WorkerSnapshot_JsonNeverContainsSensitiveContentKeys()
    {
        // Closed-field proof: the serialized JSON must never carry prompt/message/result/eval
        // keys, regardless of what future fields are added elsewhere in the wire model.
        var snapshot = new SubagentWorkerSnapshot
        {
            TaskId = "sa-1",
            ParentConversationId = "conv-1",
            ParentChannelId = "webchat-default",
            Description = "Summarize the PR",
            State = "completed",
            CreatedAt = DateTimeOffset.UtcNow,
            LastProgressAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("result", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eval", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerSnapshot_ExposesExactlyTheSpecifiedFields()
    {
        // Guards against silent field creep: the closed field list is the entire point of
        // this DTO (no prompt/messages/result/eval content ever).
        var expected = new[]
        {
            "TaskId", "ParentConversationId", "ParentChannelId", "Description", "State",
            "CreatedAt", "StartedAt", "LastProgressAt", "CompletedAt", "DurationMs",
            "StalenessMs", "RestartCount", "Rounds", "IsStale",
        };

        var actual = typeof(SubagentWorkerSnapshot)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), actual.OrderBy(n => n, StringComparer.Ordinal));
    }

    // ── SubagentAggregateSnapshot / SubagentObservabilitySnapshot ──────────

    [Fact]
    public void AggregateSnapshot_RoundTripsThroughSystemTextJson()
    {
        var aggregate = new SubagentAggregateSnapshot
        {
            CountsByState = new Dictionary<string, int> { ["queued"] = 2, ["running"] = 1 },
            QueueDepth = 2,
            ActiveCount = 1,
            MaxConcurrency = 5,
            StaleActiveCount = 0,
            OldestQueuedAgeMs = 1500,
            LongestActiveDurationMs = 3000,
            RestartCount = 0,
        };

        var json = JsonSerializer.Serialize(aggregate);
        var roundTripped = JsonSerializer.Deserialize<SubagentAggregateSnapshot>(json);

        // Dictionary-valued members compare by reference under record equality, so assert
        // field-by-field (xUnit's Assert.Equal does deep-compare the dictionary contents).
        Assert.Equal(aggregate.CountsByState, roundTripped!.CountsByState);
        Assert.Equal(aggregate.QueueDepth, roundTripped.QueueDepth);
        Assert.Equal(aggregate.ActiveCount, roundTripped.ActiveCount);
        Assert.Equal(aggregate.MaxConcurrency, roundTripped.MaxConcurrency);
        Assert.Equal(aggregate.StaleActiveCount, roundTripped.StaleActiveCount);
        Assert.Equal(aggregate.OldestQueuedAgeMs, roundTripped.OldestQueuedAgeMs);
        Assert.Equal(aggregate.LongestActiveDurationMs, roundTripped.LongestActiveDurationMs);
        Assert.Equal(aggregate.RestartCount, roundTripped.RestartCount);
    }

    [Fact]
    public void AggregateSnapshot_NullableAges_RoundTripAsNull_WhenPoolIsEmpty()
    {
        var aggregate = new SubagentAggregateSnapshot
        {
            CountsByState = new Dictionary<string, int>(),
            QueueDepth = 0,
            ActiveCount = 0,
            MaxConcurrency = 5,
            StaleActiveCount = 0,
            OldestQueuedAgeMs = null,
            LongestActiveDurationMs = null,
            RestartCount = 0,
        };

        var json = JsonSerializer.Serialize(aggregate);
        var roundTripped = JsonSerializer.Deserialize<SubagentAggregateSnapshot>(json);

        Assert.Null(roundTripped!.OldestQueuedAgeMs);
        Assert.Null(roundTripped.LongestActiveDurationMs);
    }

    [Fact]
    public void ObservabilitySnapshot_RoundTripsThroughSystemTextJson()
    {
        var snapshot = new SubagentObservabilitySnapshot
        {
            Workers =
            [
                new SubagentWorkerSnapshot
                {
                    TaskId = "sa-1",
                    ParentConversationId = "conv-1",
                    ParentChannelId = "webchat-default",
                    Description = "Task",
                    State = "queued",
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastProgressAt = DateTimeOffset.UtcNow,
                },
            ],
            Aggregate = new SubagentAggregateSnapshot
            {
                CountsByState = new Dictionary<string, int> { ["queued"] = 1 },
                QueueDepth = 1,
                ActiveCount = 0,
                MaxConcurrency = 5,
                StaleActiveCount = 0,
                RestartCount = 0,
            },
        };

        var json = JsonSerializer.Serialize(snapshot);
        var roundTripped = JsonSerializer.Deserialize<SubagentObservabilitySnapshot>(json);

        Assert.Single(roundTripped!.Workers);
        Assert.Equal(snapshot.Workers[0], roundTripped.Workers[0]);
        Assert.Equal(snapshot.Aggregate.QueueDepth, roundTripped.Aggregate.QueueDepth);
        Assert.Equal(snapshot.Aggregate.CountsByState, roundTripped.Aggregate.CountsByState);
    }

    // ── SubagentSnapshotQuery defaults ──────────────────────────────────────

    [Fact]
    public void SnapshotQuery_Defaults_MatchDocumentedEndpointDefaults()
    {
        var query = new SubagentSnapshotQuery();

        Assert.Equal(100, query.Limit);
        Assert.True(query.IncludeTerminal);
        Assert.Equal(600, query.StaleAfterSeconds);
    }

    // ── AgentMetricsSnapshot.Subagents additive field ───────────────────────

    [Fact]
    public void AgentMetricsSnapshot_Subagents_DefaultsToNull()
    {
        var snapshot = new AgentMetricsSnapshot(0, 0, 0, 0, 0, 0, 0, 0);

        Assert.Null(snapshot.Subagents);
    }

    [Fact]
    public void AgentMetricsSnapshot_Subagents_RoundTripsThroughSystemTextJson()
    {
        var snapshot = new AgentMetricsSnapshot(
            TotalMessagesProcessed: 1,
            ActiveConversations: 1,
            InboundQueueDepth: 0,
            InboundQueuePeak: 0,
            ExtractionQueueDepth: 0,
            ExtractionQueuePeak: 0,
            TokenRefreshSuccesses: 0,
            TokenRefreshFailures: 0,
            Subagents: new SubagentAggregateSnapshot
            {
                CountsByState = new Dictionary<string, int> { ["running"] = 1 },
                QueueDepth = 0,
                ActiveCount = 1,
                MaxConcurrency = 5,
                StaleActiveCount = 0,
                RestartCount = 0,
            });

        var json = JsonSerializer.Serialize(snapshot);
        var roundTripped = JsonSerializer.Deserialize<AgentMetricsSnapshot>(json);

        Assert.NotNull(roundTripped!.Subagents);
        Assert.Equal(1, roundTripped.Subagents!.ActiveCount);
    }

    // ── McpActionAggregateSnapshot / HealthInfo.McpActions ──────────────────

    [Fact]
    public void McpActionAggregateSnapshot_RoundTripsThroughSystemTextJson()
    {
        var aggregate = new McpActionAggregateSnapshot
        {
            CountsByState = new Dictionary<string, int> { ["proposed"] = 1, ["succeeded"] = 2 },
            TotalCount = 3,
        };

        var json = JsonSerializer.Serialize(aggregate);
        var roundTripped = JsonSerializer.Deserialize<McpActionAggregateSnapshot>(json);

        Assert.Equal(aggregate.CountsByState, roundTripped!.CountsByState);
        Assert.Equal(aggregate.TotalCount, roundTripped.TotalCount);
    }

    [Fact]
    public void McpActionAggregateSnapshot_JsonNeverContainsArgumentsOrResultKeys()
    {
        var aggregate = new McpActionAggregateSnapshot
        {
            CountsByState = new Dictionary<string, int> { ["proposed"] = 1 },
            TotalCount = 1,
        };

        var json = JsonSerializer.Serialize(aggregate);

        Assert.DoesNotContain("argument", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("result", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthInfo_McpActions_DefaultsToNull()
    {
        var health = new HealthInfo
        {
            Healthy = true,
            Timestamp = DateTimeOffset.UtcNow,
            Version = "1.0.0",
        };

        Assert.Null(health.McpActions);
    }

    [Fact]
    public void HealthInfo_McpActions_RoundTripsThroughSystemTextJson()
    {
        var health = new HealthInfo
        {
            Healthy = true,
            Timestamp = DateTimeOffset.UtcNow,
            Version = "1.0.0",
            McpActions = new McpActionAggregateSnapshot
            {
                CountsByState = new Dictionary<string, int> { ["proposed"] = 1 },
                TotalCount = 1,
            },
        };

        var json = JsonSerializer.Serialize(health);
        var roundTripped = JsonSerializer.Deserialize<HealthInfo>(json);

        Assert.NotNull(roundTripped!.McpActions);
        Assert.Equal(1, roundTripped.McpActions!.TotalCount);
    }
}
