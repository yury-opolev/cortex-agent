using System.Text.Json;
using Cortex.Contained.ScenarioEvals.Abstractions;
using Cortex.Contained.ScenarioEvals.Model;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.ScenarioEvals.Results;

/// <summary>
/// Persists eval run results to a local SQLite database for A/B comparison.
/// Database location: eval-results/scenario-evals.db
/// </summary>
public sealed class SqliteResultStore : IResultStore
{
    private readonly SqliteConnection _connection;

    private SqliteResultStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static async Task<SqliteResultStore> CreateAsync(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        var store = new SqliteResultStore(connection);
        await store.InitializeSchemaAsync();
        return store;
    }

    private async Task InitializeSchemaAsync()
    {
        var sql = """
            CREATE TABLE IF NOT EXISTS runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                label TEXT NOT NULL,
                git_commit TEXT,
                agent_model TEXT,
                eval_model TEXT,
                started_at TEXT NOT NULL DEFAULT (datetime('now')),
                finished_at TEXT
            );

            CREATE TABLE IF NOT EXISTS scenario_results (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL REFERENCES runs(id),
                scenario_id TEXT NOT NULL,
                passed INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                total_exchanges INTEGER NOT NULL,
                final_memory_count INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS phase_results (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scenario_result_id INTEGER NOT NULL REFERENCES scenario_results(id),
                phase_name TEXT NOT NULL,
                duration_ms INTEGER NOT NULL,
                exchanges_json TEXT,
                memories_after_json TEXT
            );

            CREATE TABLE IF NOT EXISTS scores (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                phase_result_id INTEGER NOT NULL REFERENCES phase_results(id),
                dimension TEXT NOT NULL,
                label TEXT,
                value REAL NOT NULL,
                details TEXT
            );

            CREATE TABLE IF NOT EXISTS token_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL REFERENCES runs(id),
                scenario_id TEXT NOT NULL,
                phase_name TEXT NOT NULL,
                role TEXT NOT NULL,
                tokens_in INTEGER NOT NULL,
                tokens_out INTEGER NOT NULL,
                tokens_total INTEGER NOT NULL
            );
            """;

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> CreateRunAsync(string label, string? gitCommit, string? agentModel, string? evalModel)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runs (label, git_commit, agent_model, eval_model)
            VALUES ($label, $gitCommit, $agentModel, $evalModel)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$label", label);
        cmd.Parameters.AddWithValue("$gitCommit", (object?)gitCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agentModel", (object?)agentModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$evalModel", (object?)evalModel ?? DBNull.Value);

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<long> RecordScenarioAsync(long runId, ScenarioResult result)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scenario_results (run_id, scenario_id, passed, duration_ms, total_exchanges, final_memory_count)
            VALUES ($runId, $scenarioId, $passed, $durationMs, $totalExchanges, $finalMemoryCount)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        cmd.Parameters.AddWithValue("$scenarioId", result.ScenarioId);
        cmd.Parameters.AddWithValue("$passed", result.Passed ? 1 : 0);
        cmd.Parameters.AddWithValue("$durationMs", result.DurationMs);
        cmd.Parameters.AddWithValue("$totalExchanges", result.TotalExchanges);
        cmd.Parameters.AddWithValue("$finalMemoryCount", result.FinalMemoryCount);

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<long> RecordPhaseAsync(long scenarioResultId, PhaseResult phase)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO phase_results (scenario_result_id, phase_name, duration_ms, exchanges_json, memories_after_json)
            VALUES ($scenarioResultId, $phaseName, $durationMs, $exchangesJson, $memoriesJson)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$scenarioResultId", scenarioResultId);
        cmd.Parameters.AddWithValue("$phaseName", phase.PhaseName);
        cmd.Parameters.AddWithValue("$durationMs", phase.DurationMs);
        cmd.Parameters.AddWithValue("$exchangesJson", JsonSerializer.Serialize(phase.Exchanges));
        cmd.Parameters.AddWithValue("$memoriesJson", JsonSerializer.Serialize(phase.MemoriesAfter));

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task RecordScoresAsync(long phaseResultId, IReadOnlyList<ScoreResult> scores)
    {
        foreach (var score in scores)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO scores (phase_result_id, dimension, label, value, details)
                VALUES ($phaseResultId, $dimension, $label, $value, $details)
                """;
            cmd.Parameters.AddWithValue("$phaseResultId", phaseResultId);
            cmd.Parameters.AddWithValue("$dimension", score.Dimension);
            cmd.Parameters.AddWithValue("$label", (object?)score.Label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$value", score.Value);
            cmd.Parameters.AddWithValue("$details", (object?)score.Details ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task RecordTokenUsageAsync(long runId, TokenUsageSummary usage)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO token_usage (run_id, scenario_id, phase_name, role, tokens_in, tokens_out, tokens_total)
            VALUES ($runId, $scenarioId, $phaseName, $role, $tokensIn, $tokensOut, $tokensTotal)
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        cmd.Parameters.AddWithValue("$scenarioId", usage.ScenarioId);
        cmd.Parameters.AddWithValue("$phaseName", usage.PhaseName);
        cmd.Parameters.AddWithValue("$role", usage.Role);
        cmd.Parameters.AddWithValue("$tokensIn", usage.TokensIn);
        cmd.Parameters.AddWithValue("$tokensOut", usage.TokensOut);
        cmd.Parameters.AddWithValue("$tokensTotal", usage.TokensTotal);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task FinishRunAsync(long runId)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE runs SET finished_at = datetime('now') WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
