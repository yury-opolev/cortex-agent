# Automated Labelling and One-Click Evaluation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hand-written queries, hand grading and hand-run command sequence of `docs/evaluation-runbook.md` with one "Run evaluation" button: the agent samples history and memories (plus real past recall requests), a GPT-family model writes the queries and oracle filters, a Claude-family model judges pooled candidates 0–2, the owner grades 75 items blind for κ, and a resumable state machine runs validate → guard → κ gate → pre-registered plan → dev ablation → rule-based selection → register → holdout → (Gate R) → dictionary → freeze → sanitised holdout → comparison → export → results — keeping every §4.2 commitment.

**Architecture:**
- **Labelling** (`Evaluation/Labelling`): `history-eval label` / `memory-eval label` are evaluation jobs inside the agent (they need the storage key). A generic `Labeller` pipeline over an `ILabelSource` (history: `EvalCorpus` copy; memory: `MemoryEvalCorpus` copy) runs `LabelSeeds` → `QueryDrafter` (writer model, copy check, paraphrase guard) → `MainModelQueryWriter` → pooling across the ablation grid → `RelevanceJudge` (batched strict JSON) → `DatasetFinaliser` → sealed `dataset.jsonl` + sealed `kappa-sample.json`, checkpointing to a sealed `label-state.json`. All LLM calls go through `LabellingLlm` (pinned model, no failover, cap, pacing, backoff).
- **Orchestration** (`Evaluation/Orchestration`): `EvaluationRunService` (hosted) persists `eval/run/run.json`, runs each command as a job of the existing `EvaluationJobService`, performs the internal steps (κ gate via `GradingService`, `AnalysisPlanWriter`, `ConfigSelector`/`RerankCellSelector`, summary) and waits for the owner's three inputs. `EvaluationRunApi` maps it to five new `IEvaluationHub` methods.
- **Bridge and UI**: `EvaluationRunEndpoints` relay run commands (password again) and grading items (session-bound `GradingGrants`, own rate limit, `no-store`); `eval-run.js` adds the Evaluation card to the Recall page.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, SignalR, Microsoft.Data.Sqlite + SQLite3 Multiple Ciphers, memory-mcp evaluation (`ParaphraseGuard`, `EvalStatistics`, `EvalSplits`, `AblationReporting`), System.Text.Json, xUnit + NSubstitute + `Microsoft.Extensions.TimeProvider.Testing`, Alpine.js + Bootstrap.

**Design:** [docs/superpowers/specs/2026-11-23-automated-labelling-design.md](../specs/2026-11-23-automated-labelling-design.md) (decisions L1–L16). Builds on [encrypted evaluation and the sanitised corpus](2026-11-23-encrypted-eval-sanitised-corpus.md).

---

## Ground rules

- Branch `feat/auto-labelling` is checked out (from `main` at `c04d2b5`). No submodule change.
- Commit after every task with the trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`. **Never stage `version.json`**: every commit step lists explicit paths. Do not push.
- Public repository: no real names, places or host paths. Test data uses `PersonA`, `TownB`, `zq…` markers, `example.test` hosts.
- No live LLM, Ollama or reranker calls in tests: `FakeLabellingLlm` (Task 2), `FakeHistoryEmbedder`, `FakeMemoryEmbedder`, fake evaluation commands.
- Every code task was replayed in a scratch worktree from `c04d2b5`, one commit per task: each commit builds its test project with `0 Warning(s) 0 Error(s)` and the task's tests pass with the counts given. Modified files are shown as unified diffs against the state after the previous task (context lines unchanged); new files in full.

## Gotchas (read before Task 1)

1. **Warnings are errors** (`TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`). Analyzers that bit while replaying: **CA1859** (private helpers take `List<T>`, not `IReadOnlyList<T>`), **CA1826** (index an `IReadOnlyList` instead of `FirstOrDefault()`), **CA1875** (`Regex.Count` instead of `Matches(...).Count`, also in tests), **CA1861** (no constant array arguments), **CA1822** (static helpers), **CA2016** (forward the `CancellationToken` to `File.WriteAllTextAsync` in tests). A `[]` in a conditional assigned to `var` has no natural type: declare the type.
2. **`QueryIntent.HasExactIntent` treats camelCase tokens as identifiers** (`PersonA` matches `\p{Ll}\p{Lu}`): test text meant to be identifier-free must not contain such tokens.
3. **`DirectLlmClient` fails over** to the next provider's *default* model on provider errors. `LabellingLlm` treats an answer whose `ProviderId` differs from `DirectLlmClient.ProviderFor(model)` as a failed attempt, so the judge is never silently another family.
4. **Encrypted protection needs an encrypted source:** with `EvalProtection.Encrypted(ring)` the live `messages.db` is opened with the ring's password — tests create it with `new MessageStore(path, logger, new MessageStoreEncryption(keys, TimeSpan.FromSeconds(5)))` from the same ring.
5. **`api-helper.js` redirects to the login page on 401.** An expired grading grant therefore answers **403** with `grantExpired: true`; the grading calls use `fetch` directly (they need the `X-Grading-Grant` header).
6. **`Preregistration.Register` never overwrites:** after a restart the run skips a `register` job whose `preregistration.json` exists instead of failing.
7. **Records with list properties are not structurally equal:** compare seeds as JSON in tests.
8. **Cohen's κ per tool needs ≥ 2 items** (`null` otherwise); the gate uses κ over the whole sample.
9. **`MemoryLabelSource.ParaphraseOk`** needs the memories loaded (any `SampleAsync`/`ReadAsync` first) — the pipeline always reads material before drafting.
10. **.NET file APIs resolve relative paths against the process directory, not the PowerShell location.** Scripts that edit files must use absolute paths.
11. **Worktrees and submodules:** `git -c protocol.file.allow=always submodule update --init --recursive`.
12. **Files are CRLF.** Use the editor's replace tools or apply the diffs; new files may be written with LF.
13. **Pre-existing load flakes:** `SubAgentSetGoalToolTests`, `MaintenanceStoreTests.PurgeOldRuns_RemovesOldCompletedRuns` and `MessageStoreJournalTests.Mutations_WithConcurrentReaders_NeverFail` can fail when the whole Agent.Host project runs; all pass alone (seen once each while replaying). This plan touches none of them.

## File structure

| Path | New/Mod | Responsibility |
|---|---|---|
| `src/Cortex.Contained.Agent.Host/Llm/DirectLlmClient.cs` | mod | `ProviderFor(model)`: the provider that lists a model |
| `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs` | mod | static `ToolDescription` / `ToolSchema` |
| `src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs` | mod | `OpenCopy()`: read-only connection to the private copy |
| `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs`, `Memory/Evaluation/MemoryEvalCli.cs` | mod | public `AgentEmbedder` factories |
| `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabellingOptions.cs` | new | `Evaluation:Labelling` options, `ModelFamily` |
| `…/Labelling/LabellingLlm.cs` | new | pinned JSON client: cap, pacing, backoff, failover check |
| `…/Labelling/LabellingState.cs` | new | `LabelSeed`, `DraftedQuery`, `LabellingState`, sealed store |
| `…/Labelling/LabelSeeds.cs` | new | stratified history and memory seeds, real-request share |
| `…/Labelling/RecallRequestMiner.cs` | new | real requests from `tool_call` events; request material |
| `…/Labelling/QueryDrafter.cs` | new | writer prompts, `DraftChecks` (copy, guard, filters) |
| `…/Labelling/MainModelQueryWriter.cs` | new | the main model's search call; recorded-args parsing |
| `…/Labelling/RelevanceJudge.cs` | new | batched 0–2 judge with aliases and splitting |
| `…/Labelling/DatasetFinaliser.cs` | new | finalisation rules, dataset lines, `KappaSampler` |
| `…/Labelling/KappaSampleFile.cs` | new | sealed κ sample file |
| `…/Labelling/Labeller.cs` | new | `ILabelSource`, the resumable pipeline, estimate |
| `…/Labelling/HistoryLabelSource.cs`, `MemoryLabelSource.cs` | new | sources over the corpus copies |
| `…/Labelling/LabelCli.cs` | new | `label` command (copy, run, delete copy) |
| `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs`, `Program.cs` | mod | `label` routing, options binding, run/grading DI |
| `src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/GradingService.cs` | new | blind grading, agreement files, κ |
| `…/Orchestration/AnalysisPlanWriter.cs` | new | pre-registered plan; `ConfigSelector`, `RerankCellSelector` |
| `…/Orchestration/EvaluationRunPlan.cs` | new | phases, jobs per phase, state + store, output parsing |
| `…/Orchestration/EvaluationRunService.cs` | new | hosted run state machine |
| `…/Orchestration/EvaluationRunApi.cs` | new | DTO mapping and command dispatch for the hub |
| `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.EvaluationRun.cs`, `AgentHub.cs` | new/mod | hub methods |
| `src/Cortex.Contained.Contracts/Hub/EvaluationRunTypes.cs`, `IEvaluationHub.cs` | new/mod | DTOs, five hub methods |
| `src/Cortex.Contained.Bridge/Hub/HubClient.EvaluationRun.cs`, `Evaluation/IEvaluationAgentClient.cs` | new/mod | relay |
| `src/Cortex.Contained.Bridge/Evaluation/GradingGrants.cs`, `EvaluationRunEndpoints.cs`, `Program.cs` | new/mod | grants, rate limit, endpoints |
| `src/Cortex.Contained.Bridge/wwwroot/js/components/eval-run.js`, `wwwroot/app.html` | new/mod | Evaluation card on the Recall page |
| `docs/evaluation-runbook.md`, `docs/api-reference.md`, `docs/architecture.md` | mod | owner docs |
| `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/*`, `…/Evaluation/Orchestration/*`, `tests/Cortex.Contained.Bridge.Tests/Evaluation/EvaluationRunEndpointsTests.cs` | new | tests |

## Task overview

| # | Task | Commit message | Project(s) |
|---|---|---|---|
| 1 | Labelling options, model families, provider lookup | `feat(eval): labelling options, model families and provider lookup` | Agent |
| 2 | Pinned-model JSON client (`LabellingLlm`) | `feat(eval): pinned-model JSON client with caps, pacing and backoff` | Agent |
| 3 | Sealed, resumable labelling state | `feat(eval): sealed, resumable labelling state` | Agent |
| 4 | Stratified seed sampling | `feat(eval): stratified seed sampling for history and memory` | Agent |
| 5 | Real recall requests from the event log | `feat(eval): mine real recall requests from the event log` | Agent |
| 6 | Query writer with copy check and paraphrase guard | `feat(eval): query writer with copy check, paraphrase guard and retries` | Agent |
| 7 | Model-written queries | `feat(eval): model-written queries from the main model or the recorded call` | Agent |
| 8 | Batched relevance judge | `feat(eval): batched 0-2 relevance judge with strict JSON and batch splitting` | Agent |
| 9 | Finalisation rules and the κ sample | `feat(eval): pre-declared finalisation rules and the grade-stratified kappa sample` | Agent |
| 10 | The labelling pipeline | `feat(eval): resumable labelling pipeline (draft, pool, judge, finalise, kappa sample)` | Agent |
| 11 | History label source | `feat(eval): history label source over the embedded corpus copy` | Agent |
| 12 | Memory label source | `feat(eval): memory label source and shared real-request material` | Agent |
| 13 | `history-eval label` and `memory-eval label` jobs | `feat(eval): history-eval label and memory-eval label jobs` | Agent |
| 14 | Blind hand grading | `feat(eval): blind hand grading of the kappa sample` | Agent |
| 15 | Analysis plan and selection rules | `feat(eval): automated analysis plan with the pre-declared selection rules` | Agent |
| 16 | Run phases and persisted state | `feat(eval): evaluation run phases, persisted state and output parsing` | Agent |
| 17 | The evaluation run service | `feat(eval): resumable evaluation run service chaining the protocol` | Agent |
| 18 | Hub surface and DI | `feat(eval): evaluation run and grading on the agent hub` | Agent, Contracts |
| 19 | Bridge endpoints and grading grants | `feat(bridge): evaluation run endpoints and session-bound grading grants` | Bridge |
| 20 | Recall page: Run evaluation, grading, dictionary, export, results | `feat(ui): Run evaluation, grading, dictionary approval, export and results on the Recall page` | Bridge UI |
| 21 | Documentation | `docs: one-button evaluation in the runbook, API reference and architecture` | docs |
| 22 | Final verification | — | all |

---

### Task 1: Labelling options, model families, provider lookup

The labelling job needs its configuration (`Evaluation:Labelling`), a way to compare model *families* (§4.2 forbids the same family for writing and judging) and a way to know which provider serves a model: `DirectLlmClient` silently falls back to another provider's default model, which would change the family (L1).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabellingOptions.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Llm/DirectLlmClient.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellingOptionsTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellingOptionsTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class LabellingOptionsTests
{
    [Theory]
    [InlineData("gpt-6.1-sol", "gpt")]
    [InlineData("claude-opus-5.5", "claude")]
    [InlineData("o4-mini", "gpt")]
    [InlineData("vendor/Gemini-3-pro", "gemini")]
    [InlineData("acme-large-2", "acme")]
    public void Of_ModelId_ReturnsFamily(string model, string family) => Assert.Equal(family, ModelFamily.Of(model));

    [Fact]
    public void Validate_Defaults_AreUsable()
    {
        var options = new LabellingOptions();

        Assert.Empty(options.Validate());
        Assert.Equal("gpt-6.1-sol", options.QueryModel);
        Assert.Equal("claude-opus-5.5", options.JudgeModel);
        Assert.Equal(75, options.KappaSample);
    }

    [Fact]
    public void Validate_SameFamily_Refused()
    {
        var options = new LabellingOptions { QueryModel = "claude-sonnet-5", JudgeModel = "claude-opus-5.5" };

        Assert.Contains(options.Validate(), e => e.Contains("claude family", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderFor_ListedModel_NamesItsProviderAndUnknownIsNull()
    {
        using var client = new DirectLlmClient(Substitute.For<IHttpClientFactory>(), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(new LlmCredentials
        {
            Providers =
            [
                new LlmProviderCredential
                {
                    Name = "copilot", Api = "github-copilot-api", BaseUrl = "https://api.example.test", Kind = CredentialKind.ApiKey,
                    ApiKey = "test-key", Models = ["gpt-6.1-sol", "claude-opus-5.5"], DefaultModel = "gpt-6.1-sol",
                },
            ],
        });

        Assert.Equal("copilot", client.ProviderFor("claude-opus-5.5"));
        Assert.Null(client.ProviderFor("gemini-3-pro"));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `LabellingOptions` / `ModelFamily` not found and CS1061 `DirectLlmClient` has no `ProviderFor`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabellingOptions.cs` (new):

```csharp
using System.Globalization;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// Automated labelling of the evaluation datasets (section <c>Evaluation:Labelling</c>): which models write the
/// queries and judge relevance (different families, §4.2), how many queries, the owner's κ sample and the hard
/// caps on LLM calls. Both models go through the configured provider (D5); nothing else sees the text.
/// </summary>
public sealed class LabellingOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Evaluation:Labelling";

    /// <summary>Writes the queries and gold filters from sampled snippets.</summary>
    public string QueryModel { get; set; } = "gpt-6.1-sol";

    /// <summary>Grades pooled candidates 0–2; must be another model family than <see cref="QueryModel"/>.</summary>
    public string JudgeModel { get; set; } = "claude-opus-5.5";

    /// <summary>History queries to draft (some are dropped after judging; §4.2 plans ~150 dev / 350 test).</summary>
    public int HistoryQueries { get; set; } = 520;

    /// <summary>Memory queries to draft (Gate H for memory needs at least 250 after judging).</summary>
    public int MemoryQueries { get; set; } = 270;

    /// <summary>Largest share of real recall requests (co-primary stratum) among the drafted queries.</summary>
    public double RealShare { get; set; } = 0.3;

    /// <summary>Items the owner grades by hand for the LLM-vs-human κ (split between the tools by query count).</summary>
    public int KappaSample { get; set; } = 75;

    /// <summary>Top-k of every pooled configuration that the judge grades (judged@10).</summary>
    public int PoolDepth { get; set; } = 10;

    /// <summary>Most candidates graded per query.</summary>
    public int MaxCandidatesPerQuery { get; set; } = 60;

    /// <summary>Candidates graded per judge call.</summary>
    public int JudgeBatchSize { get; set; } = 10;

    /// <summary>Drafts per seed before it is given up (paraphrase guard, copy check, filters).</summary>
    public int MaxDraftAttempts { get; set; } = 3;

    /// <summary>Hard cap on LLM calls of one label job (all models); the job stops, resumable, when reached.</summary>
    public int MaxCalls { get; set; } = 5000;

    /// <summary>Minimum time between two LLM calls (provider rate limits).</summary>
    public int MinCallIntervalMs { get; set; } = 500;

    /// <summary>Attempts per call (failures back off exponentially; unparsable answers are asked again).</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Characters of one snippet or candidate shown to a model.</summary>
    public int MaxSnippetChars { get; set; } = 1500;

    /// <summary>Problems that make a labelling run impossible (no text).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(this.QueryModel) || string.IsNullOrWhiteSpace(this.JudgeModel))
        {
            errors.Add("Evaluation:Labelling:QueryModel and JudgeModel are required.");
        }
        else if (string.Equals(ModelFamily.Of(this.QueryModel), ModelFamily.Of(this.JudgeModel), StringComparison.Ordinal))
        {
            errors.Add($"QueryModel and JudgeModel are both of the {ModelFamily.Of(this.QueryModel)} family; §4.2 needs different families.");
        }

        if (this.HistoryQueries < 1 || this.MemoryQueries < 1 || this.KappaSample < 3 || this.PoolDepth < 1 || this.JudgeBatchSize < 1
            || this.MaxCandidatesPerQuery < this.PoolDepth || this.MaxDraftAttempts < 1 || this.MaxCalls < 1 || this.MaxAttempts < 1)
        {
            errors.Add("Evaluation:Labelling counts must be positive (KappaSample ≥ 3, MaxCandidatesPerQuery ≥ PoolDepth).");
        }

        if (this.RealShare is < 0 or > 1)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"RealShare {this.RealShare} is not between 0 and 1."));
        }

        return errors;
    }
}

/// <summary>The model family of a model id (the generator/judge separation of §4.2 compares families, not ids).</summary>
public static class ModelFamily
{
    private static readonly (string Prefix, string Family)[] Known =
    [
        ("claude", "claude"), ("gpt", "gpt"), ("o1", "gpt"), ("o3", "gpt"), ("o4", "gpt"), ("chatgpt", "gpt"),
        ("gemini", "gemini"), ("grok", "grok"), ("llama", "llama"), ("mistral", "mistral"), ("qwen", "qwen"),
    ];

    /// <summary><c>claude</c> for <c>claude-opus-5.5</c>, <c>gpt</c> for <c>gpt-6.1-sol</c>; otherwise the id up to its first dash.</summary>
    public static string Of(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var id = model.Trim().ToLowerInvariant();
        var slash = id.LastIndexOf('/');
        if (slash >= 0)
        {
            id = id[(slash + 1)..];
        }

        foreach (var (prefix, family) in Known)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                return family;
            }
        }

        var dash = id.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? id[..dash] : id;
    }
}
```

`src/Cortex.Contained.Agent.Host/Llm/DirectLlmClient.cs` — apply:

```diff
@@ -124,6 +124,13 @@ public sealed partial class DirectLlmClient : ILlmClient, IDisposable
     /// <summary>Whether credentials have been received from the Bridge.</summary>
     public bool HasCredentials => this.modelProviders.Count > 0;
 
+    /// <summary>
+    /// Name of the provider that serves <paramref name="model"/> directly, or null when no provider lists it (a
+    /// request for it would go to the fallback chain's default model instead).
+    /// </summary>
+    public string? ProviderFor(string model) =>
+        this.routing.Map.TryGetValue(model, out var provider) ? provider.Credential.Name : null;
+
     /// <summary>Result of <see cref="ConfigureCredentials"/>.</summary>
     public sealed record CredentialConfigResult(
         string? DefaultModel,
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~LabellingOptionsTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 8`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabellingOptions.cs src/Cortex.Contained.Agent.Host/Llm/DirectLlmClient.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellingOptionsTests.cs
git commit -m "feat(eval): labelling options, model families and provider lookup" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: Pinned-model JSON client (`LabellingLlm`)

Every LLM call of the job goes through one client: strict JSON parsing (asked again when invalid), exponential backoff on failures (×4 on HTTP 429, at most 2 min), a minimum interval between calls, a hard cap on calls (resumable), and a failover check — an answer from another provider than the one that lists the model is not an answer (L1, L7, L9). `FakeLabellingLlm` is the scripted client every later test uses.

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabellingLlm.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/FakeLabellingLlm.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellingLlmTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/FakeLabellingLlm.cs` (new):

```csharp
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
```

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellingLlmTests.cs` (new):

````csharp
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class LabellingLlmTests
{
    private readonly FakeLabellingLlm fake = new();
    private readonly List<TimeSpan> waits = [];

    private LabellingLlm Create(LabellingOptions? options = null, int callsSoFar = 0) =>
        new(this.fake, FakeLabellingLlm.ServesAll, options ?? new LabellingOptions { MinCallIntervalMs = 0 }, callsSoFar, (wait, _) =>
        {
            this.waits.Add(wait);
            return Task.CompletedTask;
        });

    private sealed record Answer(string? Text);

    [Fact]
    public async Task CompleteJsonAsync_FencedJson_Parsed()
    {
        this.fake.Respond = (_, _, _) => "```json\n{\"text\":\"zqanswer\"}\n```";

        var answer = await this.Create().CompleteJsonAsync<Answer>("gpt-6.1-sol", "s", "u", 100, a => a.Text is not null, CancellationToken.None);

        Assert.Equal("zqanswer", answer!.Text);
        Assert.Equal(("gpt-6.1-sol", "s", "u"), Assert.Single(this.fake.Requests));
    }

    [Fact]
    public async Task CompleteJsonAsync_InvalidThenValid_AsksAgainWithoutBackoff()
    {
        var replies = new Queue<string>(["not json", "{\"text\":null}", "{\"text\":\"ok\"}"]);
        this.fake.Respond = (_, _, _) => replies.Dequeue();
        var llm = this.Create();

        var answer = await llm.CompleteJsonAsync<Answer>("m", "s", "u", 100, a => a.Text is not null, CancellationToken.None);

        Assert.Equal("ok", answer!.Text);
        Assert.Equal(3, llm.Calls);
        Assert.Empty(this.waits);
    }

    [Fact]
    public async Task CompleteJsonAsync_FailuresAndFailover_BackOffThenGiveUp()
    {
        this.fake.Respond = (_, _, _) => "{\"text\":\"from another family\"}";
        this.fake.AnsweringProvider = "other-provider";

        var answer = await this.Create(new LabellingOptions { MinCallIntervalMs = 0, MaxAttempts = 3 })
            .CompleteJsonAsync<Answer>("m", "s", "u", 100, _ => true, CancellationToken.None);

        Assert.Null(answer);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], this.waits);
    }

    [Fact]
    public void Backoff_RateLimited_WaitsLongerAndIsCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), LabellingLlm.Backoff(0, "HTTP 429: slow down"));
        Assert.Equal(TimeSpan.FromSeconds(120), LabellingLlm.Backoff(9, null));
    }

    [Fact]
    public async Task CompleteJsonAsync_AtTheCap_ThrowsWithoutCalling()
    {
        this.fake.Respond = (_, _, _) => "{\"text\":\"x\"}";
        var llm = this.Create(new LabellingOptions { MaxCalls = 5, MinCallIntervalMs = 0 }, callsSoFar: 5);

        await Assert.ThrowsAsync<LabellingBudgetExceededException>(() =>
            llm.CompleteJsonAsync<Answer>("m", "s", "u", 100, _ => true, CancellationToken.None));
        Assert.Empty(this.fake.Requests);
    }

    [Fact]
    public async Task CompleteJsonAsync_TwoCalls_SecondWaitsForTheInterval()
    {
        this.fake.Respond = (_, _, _) => "{\"text\":\"x\"}";
        var llm = this.Create(new LabellingOptions { MinCallIntervalMs = 60_000 });

        await llm.CompleteJsonAsync<Answer>("m", "s", "u", 100, _ => true, CancellationToken.None);
        await llm.CompleteJsonAsync<Answer>("m", "s", "u", 100, _ => true, CancellationToken.None);

        var wait = Assert.Single(this.waits);
        Assert.InRange(wait, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void EnsureServed_UnlistedModel_NamesIt()
    {
        var llm = new LabellingLlm(this.fake, model => model == "gpt-6.1-sol" ? "copilot" : null, new LabellingOptions());

        var error = Assert.Throws<InvalidOperationException>(() => llm.EnsureServed("gpt-6.1-sol", "claude-opus-5.5"));

        Assert.Contains("claude-opus-5.5", error.Message, StringComparison.Ordinal);
    }
}
````

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `LabellingLlm` / `LabellingBudgetExceededException` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabellingLlm.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json;
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
/// The labelling job's only way to an LLM: one pinned model per call through the configured provider, strict JSON
/// answers (asked again when unparsable or invalid), exponential backoff on failures, a minimum interval between
/// calls and a hard cap on the number of calls. A call answered by another provider than the one that lists the
/// model (a failover) does not count as an answer: the model family would no longer be the configured one (§4.2).
/// Prompts and answers are never logged.
/// </summary>
public sealed class LabellingLlm
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
    /// Asks <paramref name="model"/> and parses its answer as <typeparamref name="T"/>; null when no valid answer came
    /// within <see cref="LabellingOptions.MaxAttempts"/> attempts. Throws <see cref="LabellingBudgetExceededException"/> at the cap.
    /// </summary>
    public async Task<T?> CompleteJsonAsync<T>(string model, string system, string user, int maxTokens, Func<T, bool> valid, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(valid);
        var expectedProvider = this.providerFor(model);
        for (var attempt = 0; attempt < this.options.MaxAttempts; attempt++)
        {
            var result = await this.CallAsync(model, system, user, maxTokens, cancellationToken).ConfigureAwait(false);
            if (!result.Success || (result.ProviderId is { } provider && !string.Equals(provider, expectedProvider, StringComparison.Ordinal)))
            {
                await this.delay(Backoff(attempt, result.ErrorMessage), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (Parse<T>(result.Content) is { } parsed && valid(parsed))
            {
                return parsed;
            }
        }

        return null;
    }

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
            },
            cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~LabellingLlmTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 7`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabellingLlm.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/FakeLabellingLlm.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellingLlmTests.cs
git commit -m "feat(eval): pinned-model JSON client with caps, pacing and backoff" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: Sealed, resumable labelling state

The job's progress (seeds by id, drafts, pools, grades, cumulative calls) is saved sealed after small units of work so a crash, cancel or the call cap loses almost nothing (L9). Seeds hold ids only; drafts hold query text, hence sealed.

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabellingState.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellingStateStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellingStateStoreTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class LabellingStateStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"label-state-{Guid.NewGuid():N}");

    public LabellingStateStoreTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    [Fact]
    public void Load_NothingSaved_Null() => Assert.Null(new LabellingStateStore(this.root, EvalProtection.Plaintext).Load());

    [Fact]
    public void Save_Encrypted_SealedAndRoundTrips()
    {
        var store = new LabellingStateStore(this.root, EvalProtection.Encrypted(EvalTestKeys.Ring()));
        var state = new LabellingState { Tool = "history-eval", Calls = 7 };
        state.Seeds.Add(new LabelSeed { SeedId = "s0001", Category = "temporal", SourceIds = ["42"] });
        state.Drafts["s0001"] = new DraftedQuery
        {
            Id = "h0001", SeedId = "s0001", Text = "zqsecret when was the ferry booked", Category = "temporal",
            GoldFilters = new EvalFilters { Since = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) },
        };
        state.Grades["h0001"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["42"] = 2 };

        store.Save(state);
        var loaded = store.Load()!;

        Assert.True(EvalProtection.IsSealed(store.Path));
        Assert.DoesNotContain("zqsecret", File.ReadAllText(store.Path), StringComparison.Ordinal);
        Assert.Equal(7, loaded.Calls);
        Assert.Equal("zqsecret when was the ferry booked", loaded.Drafts["s0001"].Text);
        Assert.Equal(2, loaded.Grades["h0001"]["42"]);
        Assert.Equal(["42"], loaded.Seeds[0].SourceIds);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `LabellingStateStore` / `LabellingState` / `LabelSeed` / `DraftedQuery` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabellingState.cs` (new):

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// One sampled seed: the material a query is written from (ids only; the text is read again from the corpus copy).
/// <see cref="Category"/> is the planned category, or <c>real</c> for a real recall request whose category the
/// writer classifies.
/// </summary>
public sealed record LabelSeed
{
    public required string SeedId { get; init; }

    public required string Category { get; init; }

    /// <summary><c>synthetic</c> or <c>real</c>.</summary>
    public string Stratum { get; init; } = "synthetic";

    /// <summary>Messages or memories shown to the writer.</summary>
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>False for abstention seeds: the source is only a topic hint, not an answer.</summary>
    public bool SourceIsGold { get; init; } = true;

    /// <summary>Real requests: the user message that asked.</summary>
    public string? RequestMessageId { get; init; }

    /// <summary>Real requests: when the agent searched (candidates must be older).</summary>
    public DateTimeOffset? RequestTime { get; init; }

    /// <summary>Real requests: the search arguments the agent wrote (JSON), i.e. the model-written query.</summary>
    public string? ModelArgs { get; init; }
}

/// <summary>A query drafted from a seed, before judging (oracle text, filters and the main model's version).</summary>
public sealed record DraftedQuery
{
    public required string Id { get; init; }

    public required string SeedId { get; init; }

    public required string Text { get; init; }

    public required string Category { get; init; }

    public string Stratum { get; init; } = "synthetic";

    /// <summary>Sources that answer the query (always pooled).</summary>
    public IReadOnlyList<string> GoldSourceIds { get; init; } = [];

    /// <summary>History: oracle filters.</summary>
    public EvalFilters? GoldFilters { get; init; }

    /// <summary>Memory: oracle tag filter.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    public string? ModelText { get; init; }

    public EvalFilters? ModelFilters { get; init; }

    public IReadOnlyList<string>? ModelTags { get; init; }

    /// <summary>Real requests: candidates at or after this time are not pooled (they post-date the question).</summary>
    public DateTimeOffset? NotAfter { get; init; }
}

/// <summary>The resumable progress of one label job (sealed: it holds query text).</summary>
public sealed class LabellingState
{
    public string Tool { get; set; } = string.Empty;

    public List<LabelSeed> Seeds { get; set; } = [];

    /// <summary>Drafts by seed id.</summary>
    public Dictionary<string, DraftedQuery> Drafts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Seeds the writer could not turn into a usable query.</summary>
    public List<string> FailedSeeds { get; set; } = [];

    /// <summary>Pooled candidate ids by query id.</summary>
    public Dictionary<string, List<string>> Pools { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Judge grades by query id, then candidate id.</summary>
    public Dictionary<string, Dictionary<string, int>> Grades { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Queries the judge could not grade completely.</summary>
    public List<string> FailedJudging { get; set; } = [];

    /// <summary>LLM calls made so far by every run of this job.</summary>
    public int Calls { get; set; }
}

/// <summary>Loads and saves <see cref="LabellingState"/> through <see cref="EvalProtection"/> (atomic, sealed on an encrypted install).</summary>
public sealed class LabellingStateStore
{
    /// <summary>File name in the workspace.</summary>
    public const string FileName = "label-state.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly EvalProtection protection;

    public LabellingStateStore(string directory, EvalProtection protection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.Path = System.IO.Path.Combine(directory, FileName);
        this.protection = protection;
    }

    public string Path { get; }

    /// <summary>The saved state, or null when there is none.</summary>
    public LabellingState? Load() =>
        File.Exists(this.Path) ? JsonSerializer.Deserialize<LabellingState>(this.protection.ReadText(this.Path), Json) : null;

    public void Save(LabellingState state) => this.protection.WriteText(this.Path, JsonSerializer.Serialize(state, Json));

    public void Delete() => File.Delete(this.Path);
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~LabellingStateStoreTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 2`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabellingState.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellingStateStoreTests.cs
git commit -m "feat(eval): sealed, resumable labelling state" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: Stratified seed sampling

Deterministic, stratified seeds (channel × month × length; category quotas incl. temporal, identifier, enumeration and abstention; memory by tag with related memories for `multi`), with real requests taking their share first, spread over months (L3).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabelSeeds.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabelSeedsTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabelSeedsTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Memory.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class LabelSeedsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private static List<SourceMessage> Messages()
    {
        var messages = new List<SourceMessage>();
        for (var i = 0; i < 240; i++)
        {
            var channel = i % 2 == 0 ? "webchat-default" : "discord-dm";
            var text = i % 9 == 0
                ? $"the invoice INV-{2000 + i} for the boat repair in TownB was paid by transfer"
                : $"we talked about topic number {i} and what my friend thinks of the plan for the weekend";
            messages.Add(new SourceMessage(i + 1, channel, i % 3 == 0 ? "assistant" : "user", T0.AddHours(i * 9), text));
        }

        messages.Add(new SourceMessage(1000, "webchat-default", "user", T0, "too short"));
        messages.Add(new SourceMessage(1001, "webchat-default", "tool", T0, "a tool result that is long enough to be eligible otherwise"));
        return messages;
    }

    [Fact]
    public void History_SyntheticOnly_FillsTheQuotasWithFittingDistinctSources()
    {
        var messages = Messages();
        var byId = messages.ToDictionary(m => m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var seeds = LabelSeeds.History(messages, [], 50, realShare: 0.3);

        Assert.Equal(50, seeds.Count);
        var counts = seeds.GroupBy(s => s.Category).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(11, counts["lookup"]);
        Assert.Equal(10, counts["paraphrase"]);
        Assert.Equal(5, counts["cross_language"]);
        Assert.Equal(7, counts["temporal"]);
        Assert.Equal(5, counts["identifier"]);
        Assert.Equal(7, counts["enumeration"]);
        Assert.Equal(5, counts["abstention"]);
        Assert.All(seeds.Where(s => s.Category == "identifier"), s => Assert.Contains("INV-", byId[s.SourceIds[0]].Content, StringComparison.Ordinal));
        Assert.All(seeds.Where(s => s.Category == "enumeration"), s =>
        {
            Assert.InRange(s.SourceIds.Count, 3, 5);
            Assert.Single(s.SourceIds.Select(id => byId[id].ChannelId).Distinct());
        });
        Assert.All(seeds, s => Assert.Equal(s.Category != "abstention", s.SourceIsGold));
        var sources = seeds.SelectMany(s => s.SourceIds).ToList();
        Assert.Equal(sources.Count, sources.Distinct().Count());
        Assert.DoesNotContain("1000", sources);
        Assert.DoesNotContain("1001", sources);
        Assert.Equal("s0001", seeds[0].SeedId);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(seeds),
            System.Text.Json.JsonSerializer.Serialize(LabelSeeds.History(messages, [], 50, realShare: 0.3)));
    }

    [Fact]
    public void History_RealRequests_TakeTheirShareFirstAcrossMonths()
    {
        var real = Enumerable.Range(0, 10)
            .Select(i => new LabelSeed { SeedId = string.Empty, Category = "real", RequestMessageId = $"{500 + i}", RequestTime = T0.AddDays(i < 8 ? 0 : 40) })
            .ToList();

        var seeds = LabelSeeds.History(Messages(), real, 20, realShare: 0.3);

        var chosen = seeds.Take(6).ToList();
        Assert.All(chosen, s => Assert.Equal(("real", "real"), (s.Category, s.Stratum)));
        Assert.Equal(2, chosen.Count(s => s.RequestTime > T0.AddDays(30)));
        Assert.Equal(14, seeds.Count(s => s.Stratum == "synthetic"));
    }

    [Fact]
    public void Memory_MultiGetsRelatedMemoriesAndAbstentionOnlyAHint()
    {
        var memories = Enumerable.Range(0, 40)
            .Select(i => new MemorySeedSource($"mem-{i:00}", null, [i % 2 == 0 ? "travel" : "finance"], $"memory {i} about item INV-{3000 + i}"))
            .ToList();

        var seeds = LabelSeeds.Memory(memories, [], 30, realShare: 0.3);

        Assert.Equal(30, seeds.Count);
        var multi = seeds.Where(s => s.Category == "multi").ToList();
        Assert.NotEmpty(multi);
        Assert.All(multi, s => Assert.True(s.SourceIds.Count > 1));
        var abstention = seeds.Where(s => s.Category == "abstention").ToList();
        Assert.NotEmpty(abstention);
        Assert.All(abstention, s => Assert.False(s.SourceIsGold));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `LabelSeeds` / `SourceMessage` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabelSeeds.cs` (new):

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Memory.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>A stored message offered to the history seed sampler (raw text; never written out).</summary>
public sealed record SourceMessage(long Id, string ChannelId, string Role, DateTimeOffset Timestamp, string Content);

/// <summary>
/// Deterministic, stratified seed samples for the query writer (§4.2). History: strata of channel × month × length,
/// round-robin across strata in hash order, each message assigned the next category of the quota that fits it
/// (identifier only for identifier-like text, enumeration only with ≥ 2 nearby messages of the same channel).
/// Memory: <see cref="MemoryQuerySeeds"/> (stratified by tag), with extra memories for <c>multi</c> and a topic hint
/// for abstention. Real recall requests (the co-primary stratum) take up to <c>realShare</c> of the seeds, spread
/// over months.
/// </summary>
public static class LabelSeeds
{
    /// <summary>Shortest message (characters) a history query is written from.</summary>
    public const int MinContentChars = 40;

    /// <summary>History category shares of the synthetic seeds (the rest goes to <c>lookup</c>).</summary>
    public static IReadOnlyList<(string Category, double Share)> HistoryQuotas { get; } =
    [
        ("lookup", 0.20), ("paraphrase", 0.20), ("cross_language", 0.10), ("temporal", 0.15),
        ("identifier", 0.10), ("enumeration", 0.15), ("abstention", 0.10),
    ];

    private static readonly TimeSpan EnumerationWindow = TimeSpan.FromDays(3);

    /// <summary>History seeds: <paramref name="count"/> in total, real requests first (up to the share), then synthetic.</summary>
    public static IReadOnlyList<LabelSeed> History(
        IReadOnlyList<SourceMessage> messages, IReadOnlyList<LabelSeed> real, int count, double realShare, int seed = 20261121)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(real);
        var chosenReal = TakeReal(real, (int)Math.Floor(count * realShare), seed);
        var synthetic = count - chosenReal.Count;
        var remaining = HistoryQuotas.ToDictionary(q => q.Category, q => (int)Math.Floor(q.Share * synthetic), StringComparer.Ordinal);
        remaining["lookup"] += synthetic - remaining.Values.Sum();

        var eligible = messages.Where(IsEligible).ToList();
        var byChannel = eligible.GroupBy(m => m.ChannelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Timestamp).ThenBy(m => m.Id).ToList(), StringComparer.Ordinal);
        var used = new HashSet<long>();
        var seeds = new List<LabelSeed>(chosenReal);
        var index = 0;
        foreach (var message in RoundRobin(eligible, seed))
        {
            if (remaining.Values.Sum() == 0)
            {
                break;
            }

            if (used.Contains(message.Id))
            {
                continue;
            }

            var window = Window(byChannel[message.ChannelId], message, used);
            var category = HistoryQuotas.Select((q, i) => HistoryQuotas[(i + index) % HistoryQuotas.Count].Category)
                .FirstOrDefault(c => remaining[c] > 0 && c switch
                {
                    "identifier" => QueryIntent.HasExactIntent(message.Content),
                    "enumeration" => window.Count >= 3,
                    _ => true,
                });
            if (category is null)
            {
                continue;
            }

            var sources = category == "enumeration" ? window : [message];
            foreach (var source in sources)
            {
                used.Add(source.Id);
            }

            remaining[category]--;
            index++;
            seeds.Add(new LabelSeed
            {
                SeedId = string.Empty,
                Category = category,
                SourceIds = sources.Select(s => s.Id.ToString(CultureInfo.InvariantCulture)).ToList(),
                SourceIsGold = category != "abstention",
            });
        }

        return Number(seeds);
    }

    /// <summary>Memory seeds: real requests first (up to the share), then <see cref="MemoryQuerySeeds"/> for the rest.</summary>
    public static IReadOnlyList<LabelSeed> Memory(
        IReadOnlyList<MemorySeedSource> memories, IReadOnlyList<LabelSeed> real, int count, double realShare, int seed = 20261121)
    {
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentNullException.ThrowIfNull(real);
        var chosenReal = TakeReal(real, (int)Math.Floor(count * realShare), seed);
        var seeds = new List<LabelSeed>(chosenReal);
        var synthetic = count - chosenReal.Count;
        if (synthetic > 0 && memories.Count > 0)
        {
            var hints = memories.OrderBy(m => Rank(seed + 1, m.MemoryId), StringComparer.Ordinal).ToList();
            var hint = 0;
            foreach (var picked in MemoryQuerySeeds.Select(memories, synthetic, seed))
            {
                IReadOnlyList<string> sources = picked.Category switch
                {
                    "abstention" => [hints[hint++ % hints.Count].MemoryId],
                    "multi" => Related(memories, picked, seed),
                    _ => [picked.MemoryId!],
                };
                seeds.Add(new LabelSeed { SeedId = string.Empty, Category = picked.Category, SourceIds = sources, SourceIsGold = picked.Category != "abstention" });
            }
        }

        return Number(seeds);
    }

    /// <summary>Up to <paramref name="max"/> real requests, round-robin across months in hash order.</summary>
    public static IReadOnlyList<LabelSeed> TakeReal(IReadOnlyList<LabelSeed> real, int max, int seed = 20261121)
    {
        ArgumentNullException.ThrowIfNull(real);
        var months = real
            .GroupBy(r => r.RequestTime?.ToString("yyyy-MM", CultureInfo.InvariantCulture) ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Queue<LabelSeed>(g.OrderBy(r => Rank(seed, r.RequestMessageId ?? string.Empty), StringComparer.Ordinal)))
            .ToList();
        var chosen = new List<LabelSeed>();
        while (chosen.Count < max && months.Any(m => m.Count > 0))
        {
            foreach (var month in months.Where(m => m.Count > 0))
            {
                if (chosen.Count < max)
                {
                    chosen.Add(month.Dequeue() with { Category = "real", Stratum = "real" });
                }
            }
        }

        return chosen;
    }

    /// <summary>True for user and assistant messages long enough to write a query from.</summary>
    public static bool IsEligible(SourceMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Role is "user" or "assistant" && message.Content.Trim().Length >= MinContentChars;
    }

    private static List<SourceMessage> RoundRobin(List<SourceMessage> messages, int seed)
    {
        var strata = messages
            .GroupBy(m => string.Create(CultureInfo.InvariantCulture, $"{m.ChannelId}|{m.Timestamp:yyyy-MM}|{LengthBucket(m.Content)}"), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Queue<SourceMessage>(g.OrderBy(m => Rank(seed, m.Id.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal)))
            .ToList();
        var ordered = new List<SourceMessage>(messages.Count);
        while (strata.Any(s => s.Count > 0))
        {
            foreach (var stratum in strata.Where(s => s.Count > 0))
            {
                ordered.Add(stratum.Dequeue());
            }
        }

        return ordered;
    }

    private static int LengthBucket(string content) => content.Length switch
    {
        < 200 => 0,
        < 800 => 1,
        _ => 2,
    };

    // The message and up to four unused messages of its channel within three days, nearest first, in time order.
    private static List<SourceMessage> Window(List<SourceMessage> channel, SourceMessage message, HashSet<long> used) =>
        channel
            .Where(m => m.Id != message.Id && !used.Contains(m.Id) && (m.Timestamp - message.Timestamp).Duration() <= EnumerationWindow)
            .OrderBy(m => (m.Timestamp - message.Timestamp).Duration())
            .Take(4)
            .Append(message)
            .OrderBy(m => m.Timestamp)
            .ThenBy(m => m.Id)
            .ToList();

    private static List<string> Related(IReadOnlyList<MemorySeedSource> memories, MemoryQuerySeed picked, int seed)
    {
        var tag = picked.Tags.Count > 0 ? picked.Tags[0] : null;
        return memories
            .Where(m => m.MemoryId != picked.MemoryId && (tag is null || m.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(m => Rank(seed, picked.MemoryId + "|" + m.MemoryId), StringComparer.Ordinal)
            .Take(3)
            .Select(m => m.MemoryId)
            .Prepend(picked.MemoryId!)
            .ToList();
    }

    private static List<LabelSeed> Number(List<LabelSeed> seeds)
    {
        var width = Math.Max(4, seeds.Count.ToString(CultureInfo.InvariantCulture).Length);
        return seeds.Select((s, i) => s with { SeedId = "s" + (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') }).ToList();
    }

    private static string Rank(int seed, string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString(CultureInfo.InvariantCulture) + ":" + id)));
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~LabelSeedsTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 3`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabelSeeds.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabelSeedsTests.cs
git commit -m "feat(eval): stratified seed sampling for history and memory" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: Real recall requests from the event log

The co-primary stratum: every recorded `history_search`/`memory_search` call with arguments, paired with the owner's preceding message in the channel (internal rows such as scheduled instructions excluded), once per message; the recorded arguments are the model-written query (L3).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RecallRequestMiner.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/RecallRequestMinerTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/RecallRequestMinerTests.cs` (new):

```csharp
using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class RecallRequestMinerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"miner-{Guid.NewGuid():N}");
    private readonly string db;

    public RecallRequestMinerTests()
    {
        Directory.CreateDirectory(this.root);
        this.db = Path.Combine(this.root, "messages.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(this.root, recursive: true);
    }

    private static Task<string> CallAsync(MessageStore store, string tool, string channel, DateTimeOffset ts, string? args, bool redacted = false)
    {
        var payload = new JsonObject { ["tool"] = tool, ["toolCallId"] = Guid.NewGuid().ToString("N") };
        if (args is not null)
        {
            payload["args"] = args;
        }

        if (redacted)
        {
            payload["redaction"] = "unavailable";
        }

        return store.AppendAsync(new EventDraft(EventTypes.ToolCall, AuthorKinds.Main) { ChannelId = channel, Ts = ts, Payload = payload }, CancellationToken.None);
    }

    [Fact]
    public async Task Read_PairsEachSearchWithTheOwnersPrecedingMessageOncePerMessage()
    {
        long asked;
        await using (var store = new MessageStore(this.db, NullLogger<MessageStore>.Instance))
        {
            await store.SaveMessageAsync("u", "ch", "user", "an older question", T0.AddMinutes(-30));
            asked = await store.SaveMessageAsync("u", "ch", "user", "when did we book the ferry", T0);
            await store.SaveMessageAsync("u", "ch", "user", "scheduled instruction", T0.AddMinutes(2), category: MessageCategory.Internal);
            await store.SaveMessageAsync("u", "other", "user", "unrelated channel", T0.AddMinutes(1));
            await CallAsync(store, "history_search", "ch", T0.AddSeconds(5), """{"query":"ferry booking","channel":"ch"}""");
            await CallAsync(store, "history_search", "ch", T0.AddSeconds(9), """{"query":"ferry"}""");
            await CallAsync(store, "memory_search", "ch", T0.AddSeconds(7), """{"query":"ferry"}""");
            await CallAsync(store, "history_search", "ch", T0.AddMinutes(3), "{\"query\":\"cut off", redacted: false);
            await store.SaveMessageAsync("u", "ch", "user", "a later question searched without recorded arguments", T0.AddMinutes(3.5));
            await CallAsync(store, "history_search", "ch", T0.AddMinutes(4), """{"query":"secret"}""", redacted: true);
        }

        var seeds = RecallRequestMiner.Read(this.db, null, "history_search");

        var seed = Assert.Single(seeds);
        Assert.Equal(asked.ToString(System.Globalization.CultureInfo.InvariantCulture), seed.RequestMessageId);
        Assert.Equal(("real", "real"), (seed.Category, seed.Stratum));
        Assert.Equal(T0.AddSeconds(5), seed.RequestTime);
        Assert.Equal("""{"query":"ferry booking","channel":"ch"}""", seed.ModelArgs);
        Assert.Empty(seed.SourceIds);
        Assert.Single(RecallRequestMiner.Read(this.db, null, "memory_search"));
        Assert.Empty(RecallRequestMiner.Read(Path.Combine(this.root, "missing.db"), null, "history_search"));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0103 `RecallRequestMiner` does not exist.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RecallRequestMiner.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// Real recall requests (the co-primary stratum of §4.2), mined from the agent's own past searches: every
/// <c>tool_call</c> event of <c>history_search</c> or <c>memory_search</c> with recorded arguments, paired with the
/// owner's message that preceded it in the same channel. One seed per owner message (the first search counts); the
/// recorded arguments are the model-written query. Reads ids, times and the (already redacted) arguments only.
/// </summary>
public static class RecallRequestMiner
{
    /// <summary>Seeds for the searches of <paramref name="tool"/> in the database at <paramref name="db"/>.</summary>
    public static IReadOnlyList<LabelSeed> Read(string db, string? password, string tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        if (!File.Exists(db))
        {
            return [];
        }

        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(db, password, SqliteOpenMode.ReadOnly, pooling: false));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.Ts, json_extract(e.Payload, '$.args'),
                   (SELECT m.Id FROM Messages m
                     WHERE m.ChannelId = e.ChannelId AND m.Role = 'user' AND m.Category = 0 AND m.Timestamp <= e.Ts
                     ORDER BY m.Timestamp DESC, m.Id DESC LIMIT 1)
              FROM Events e
             WHERE e.Type = 'tool_call' AND e.ChannelId IS NOT NULL
               AND json_extract(e.Payload, '$.tool') = @tool
               AND json_extract(e.Payload, '$.args') IS NOT NULL
               AND json_extract(e.Payload, '$.redaction') IS NULL
             ORDER BY e.Seq;
            """;
        cmd.Parameters.AddWithValue("@tool", tool);
        using var reader = cmd.ExecuteReader();
        var seeds = new List<LabelSeed>();
        var seen = new HashSet<long>();
        while (reader.Read())
        {
            if (reader.IsDBNull(2) || !seen.Add(reader.GetInt64(2)))
            {
                continue;
            }

            seeds.Add(new LabelSeed
            {
                SeedId = string.Empty,
                Category = "real",
                Stratum = "real",
                RequestMessageId = reader.GetInt64(2).ToString(CultureInfo.InvariantCulture),
                RequestTime = SqliteDateTimeText.Parse(reader.GetString(0)),
                ModelArgs = IsJsonObject(reader.GetString(1)) ? reader.GetString(1) : null,
            });
        }

        return seeds;
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~RecallRequestMinerTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 1`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RecallRequestMiner.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/RecallRequestMinerTests.cs
git commit -m "feat(eval): mine real recall requests from the event log" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: Query writer with copy check and paraphrase guard

The writer model drafts one query per seed in its category (real requests: a standalone rewrite plus a category), never repeating four words of the snippet, with oracle filters/tags; drafts failing a check are written again with the reason (L4).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/QueryDrafter.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/QueryDrafterTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/QueryDrafterTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class QueryDrafterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 14, 18, 0, 0, TimeSpan.Zero);

    private static readonly SeedMaterial Snippet = new(
    [
        new MaterialItem("42", "we booked the night ferry to TownB for the fourteenth of March", "discord-dm", T0, "user"),
    ]);

    private readonly FakeLabellingLlm fake = new();

    private QueryDrafter Drafter(string tool = "history-eval") =>
        new(new LabellingLlm(this.fake, FakeLabellingLlm.ServesAll, new LabellingOptions { MinCallIntervalMs = 0 }, delay: (_, _) => Task.CompletedTask), "gpt-6.1-sol", tool, 3, 1500);

    private static LabelSeed Seed(string category) => new() { SeedId = "s0001", Category = category, SourceIds = ["42"] };

    [Fact]
    public async Task DraftAsync_CopiedPhraseThenGuardFailure_WritesAgainWithTheReason()
    {
        var replies = new Queue<string>(
        [
            """{"text":"when is the night ferry to TownB","category":"paraphrase"}""",
            """{"text":"which boat trip did we reserve","category":"paraphrase"}""",
            """{"text":"what overnight sea crossing did I arrange","category":"paraphrase"}""",
        ]);
        this.fake.Respond = (_, _, _) => replies.Dequeue();

        var answer = await this.Drafter().DraftAsync(Seed("paraphrase"), Snippet, (text, _) => !text.Contains("boat", StringComparison.Ordinal), CancellationToken.None);

        Assert.Equal("what overnight sea crossing did I arrange", answer!.Text);
        Assert.Equal(3, this.fake.Requests.Count);
        Assert.All(this.fake.Requests, r => Assert.Equal("gpt-6.1-sol", r.Model));
        Assert.Contains("Kind of query: paraphrase", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("Current time (UTC): ", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("night ferry to TownB", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("four or more consecutive words", this.fake.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("paraphrase guard", this.fake.Requests[2].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DraftAsync_TemporalFilterMissesTheMessageTime_Rejected()
    {
        this.fake.Respond = (_, _, _) => """{"text":"what did we plan in February","category":"temporal","goldFilters":{"since":"2026-02-01T00:00:00Z","until":"2026-03-01T00:00:00Z"}}""";

        var answer = await this.Drafter().DraftAsync(Seed("temporal"), Snippet, (_, _) => true, CancellationToken.None);

        Assert.Null(answer);
        Assert.Equal(3, this.fake.Requests.Count);
    }

    [Fact]
    public async Task DraftAsync_TemporalFilterContainsTheTime_KeepsFilters()
    {
        this.fake.Respond = (_, _, _) => """{"text":"what did we plan in mid March","category":"lookup","goldFilters":{"since":"2026-03-01T00:00:00Z","until":"2026-04-01T00:00:00Z"}}""";

        var answer = await this.Drafter().DraftAsync(Seed("temporal"), Snippet, (_, _) => true, CancellationToken.None);

        Assert.Equal("temporal", answer!.Category);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), answer.GoldFilters!.Until);
    }

    [Fact]
    public async Task DraftAsync_RealRequest_ShowsRequestAndContextAndClampsTheCategory()
    {
        this.fake.Respond = (_, _, _) => """{"text":"when did we book the ferry to TownB","category":"abstention"}""";
        var seed = new LabelSeed { SeedId = "s0002", Category = "real", Stratum = "real", RequestTime = T0 };
        var material = new SeedMaterial([], "and when did we book it?") { Context = ["user: about the ferry to TownB"] };

        var answer = await this.Drafter().DraftAsync(seed, material, (_, _) => true, CancellationToken.None);

        Assert.Equal("lookup", answer!.Category);
        var prompt = Assert.Single(this.fake.Requests).User;
        Assert.Contains("and when did we book it?", prompt, StringComparison.Ordinal);
        Assert.Contains("user: about the ferry to TownB", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Snippet:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DraftAsync_TagFilteredMemory_TagsMustBeTheMemorysTags()
    {
        var replies = new Queue<string>(
        [
            """{"text":"which invoices did I file","category":"tag_filtered","tags":["receipts"]}""",
            """{"text":"which invoices did I file","category":"tag_filtered","tags":["Finance"]}""",
        ]);
        this.fake.Respond = (_, _, _) => replies.Dequeue();
        var material = new SeedMaterial([new MaterialItem("mem-1", "Invoice for the boat repair was paid", Title: "Invoice") { Tags = ["finance"] }]);

        var answer = await this.Drafter("memory-eval").DraftAsync(Seed("tag_filtered"), material, (_, _) => true, CancellationToken.None);

        Assert.Equal(["Finance"], answer!.Tags);
        Assert.Contains("tags: finance", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("curated memories", this.fake.Requests[0].System, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("night ferry to TownB", 5, false)]
    [InlineData("the night ferry to townb", 4, true)]
    [InlineData("booked the night", 3, true)]
    public void SharesRun_FoldedWords(string query, int run, bool expected) =>
        Assert.Equal(expected, DraftChecks.SharesRun(query, "We booked the NIGHT ferry to TownB.", run));

    [Fact]
    public void Problem_IdentifierWithoutIdentifier_Rejected()
    {
        var answer = new DraftAnswer { Text = "which invoice was paid", Category = "identifier" };

        Assert.Contains("identifier", DraftChecks.Problem(Seed("identifier"), Snippet, answer, (_, _) => true), StringComparison.Ordinal);
        Assert.Null(DraftChecks.Problem(Seed("identifier"), Snippet, answer with { Text = "was INV-2041 paid" }, (_, _) => true));
        Assert.NotNull(DraftChecks.Problem(Seed("lookup"), Snippet, answer with { Text = string.Empty }, (_, _) => true));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `QueryDrafter` / `SeedMaterial` / `MaterialItem` / `DraftChecks` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/QueryDrafter.cs` (new):

```csharp
using System.Globalization;
using System.Text;
using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>One stored message or memory shown to a model (raw text; never written out).</summary>
public sealed record MaterialItem(string Id, string Text, string? Channel = null, DateTimeOffset? Time = null, string? Role = null, string? Title = null)
{
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>What the writer sees for one seed: the snippet items, or a real request with its preceding turns.</summary>
public sealed record SeedMaterial(IReadOnlyList<MaterialItem> Items, string? Request = null)
{
    /// <summary>Real requests: the turns before the request, oldest first (<c>role: text</c>).</summary>
    public IReadOnlyList<string> Context { get; init; } = [];
}

/// <summary>The writer's answer (strict JSON).</summary>
public sealed record DraftAnswer
{
    public string? Text { get; init; }

    public string? Category { get; init; }

    public EvalFilters? GoldFilters { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// The query writer (a model of another family than the judge, §4.2): drafts one query per seed in the seed's
/// category, never copying the snippet's wording, with the oracle filters (history) or tags (memory). Every draft
/// is checked (<see cref="DraftChecks"/>, including the paraphrase guard for paraphrase queries) and written again
/// with the reason when it fails, up to <see cref="LabellingOptions.MaxDraftAttempts"/> times.
/// </summary>
public sealed class QueryDrafter
{
    /// <summary>Categories a real history request can be classified as.</summary>
    public static readonly IReadOnlyList<string> RealHistoryCategories = ["lookup", "temporal", "identifier", "enumeration"];

    /// <summary>Categories a real memory request can be classified as.</summary>
    public static readonly IReadOnlyList<string> RealMemoryCategories = ["lookup", "identifier", "tag_filtered", "multi"];

    private const string Rules = """
        Rules:
        - Never copy wording from the snippet: no run of four or more of its words (an identifier may be reused when asked).
        - The query must stand alone: do not mention a snippet, a message or "the text above".
        - One sentence, under 200 characters, written the way the owner would type or say it.
        """;

    private static readonly Dictionary<string, string> Instructions = new(StringComparer.Ordinal)
    {
        ["lookup"] = "Write a question whose answer is in the snippet, in the snippet's language.",
        ["paraphrase"] = "Write a question whose answer is in the snippet but that shares NO content word with it: use synonyms and describe things indirectly.",
        ["cross_language"] = "Write a question whose answer is in the snippet, in another language than the snippet (English if the snippet is not in English, otherwise Danish or Russian).",
        ["temporal"] = "Write a question that asks about the snippet's content by when it happened (a period such as 'last spring' or 'in March'); set goldFilters.since and goldFilters.until to the period the question names, which must contain the message time.",
        ["identifier"] = "Write a question that uses the exact identifier from the snippet (a code, number with letters, file name or similar) as written.",
        ["enumeration"] = "Write a question that asks for all of several things mentioned across these messages (e.g. 'which … did we …'), so that at least two of the messages are needed for a complete answer.",
        ["abstention"] = "The snippet only shows the topic area. Write a plausible, specific question in this area whose answer is NOT in the snippet and is unlikely to have been discussed at all.",
        ["tag_filtered"] = "Write a question whose answer is in the memory and set tags to the memory's tags an ideal searcher would filter by (a subset of its tags).",
        ["multi"] = "Write a question that is only answered by several of these memories together, so that at least two of them are needed.",
    };

    private readonly LabellingLlm llm;
    private readonly string model;
    private readonly bool memory;
    private readonly int maxAttempts;
    private readonly int maxChars;
    private readonly TimeProvider time;

    /// <param name="llm">The pinned client.</param>
    /// <param name="model">The query writer model.</param>
    /// <param name="tool"><c>history-eval</c> or <c>memory-eval</c>.</param>
    /// <param name="maxAttempts">Drafts per seed.</param>
    /// <param name="maxChars">Characters per snippet item.</param>
    /// <param name="time">The clock (relative periods such as 'last spring' are read against it).</param>
    public QueryDrafter(LabellingLlm llm, string model, string tool, int maxAttempts, int maxChars, TimeProvider? time = null)
    {
        this.llm = llm;
        this.model = model;
        this.memory = string.Equals(tool, "memory-eval", StringComparison.Ordinal);
        this.maxAttempts = maxAttempts;
        this.maxChars = maxChars;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>A checked draft for <paramref name="seed"/>, or null when every attempt failed.</summary>
    /// <param name="seed">The seed.</param>
    /// <param name="material">Its snippet or request.</param>
    /// <param name="paraphraseOk">The paraphrase guard: query text and gold texts → passes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<DraftAnswer?> DraftAsync(LabelSeed seed, SeedMaterial material, Func<string, IReadOnlyList<string>, bool> paraphraseOk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(material);
        var prompt = this.Prompt(seed, material);
        string? problem = null;
        for (var attempt = 0; attempt < this.maxAttempts; attempt++)
        {
            var user = problem is null ? prompt : prompt + "\n\nYour previous query was rejected: " + problem + " Write a different one.";
            var answer = await this.llm.CompleteJsonAsync<DraftAnswer>(this.model, this.System(), user, 800, a => !string.IsNullOrWhiteSpace(a.Text), cancellationToken).ConfigureAwait(false);
            if (answer is null)
            {
                return null;
            }

            answer = this.Normalise(seed, answer);
            problem = DraftChecks.Problem(seed, material, answer, paraphraseOk);
            if (problem is null)
            {
                return answer;
            }
        }

        return null;
    }

    private DraftAnswer Normalise(LabelSeed seed, DraftAnswer answer)
    {
        var allowed = this.memory ? RealMemoryCategories : RealHistoryCategories;
        var category = seed.Stratum == "real"
            ? (allowed.Contains(answer.Category ?? string.Empty, StringComparer.Ordinal) ? answer.Category! : "lookup")
            : seed.Category;
        return answer with { Text = answer.Text!.Trim(), Category = category };
    }

    private string System() => this.memory
        ? "You write test queries for evaluating search over a person's curated memories (short notes their assistant stored). " +
          "You get one or more memories (the snippet) and the kind of query to write: the question the owner would ask to find that information later.\n" + Rules +
          "\n- tags: only for tag_filtered queries, otherwise null.\nReturn ONLY a JSON object: {\"text\": string, \"category\": string, \"tags\": [string] or null}."
        : "You write test queries for evaluating search over a person's own chat history with their assistant. " +
          "You get one or more stored messages (the snippet) and the kind of query to write: the question the owner would ask to find that information later.\n" + Rules +
          "\n- goldFilters are the filters an ideal searcher would use: channel only when the query names the conversation, since/until (ISO 8601, UTC) only when the query names a time.\n" +
          "Return ONLY a JSON object: {\"text\": string, \"category\": string, \"goldFilters\": {\"channel\": string or null, \"since\": string or null, \"until\": string or null}}.";

    private string Prompt(LabelSeed seed, SeedMaterial material)
    {
        var text = new StringBuilder();
        if (seed.Stratum == "real")
        {
            var categories = string.Join(", ", this.memory ? RealMemoryCategories : RealHistoryCategories);
            text.AppendLine("The owner really asked the request below. Rewrite it as a standalone search request with the same intent and in the same language, resolving references with the context only and adding no facts.");
            text.AppendLine(CultureInfo.InvariantCulture, $"Set category to one of: {categories}.");
            if (material.Context.Count > 0)
            {
                text.AppendLine("Context (oldest first):");
                foreach (var turn in material.Context)
                {
                    text.AppendLine(this.Clip(turn));
                }
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"Request ({seed.RequestTime:yyyy-MM-dd HH:mm} UTC): {this.Clip(material.Request ?? string.Empty)}");
            return text.ToString();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Current time (UTC): {this.time.GetUtcNow():yyyy-MM-dd HH:mm}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Kind of query: {seed.Category}. {Instructions[seed.Category]}");
        text.AppendLine("Snippet:");
        for (var i = 0; i < material.Items.Count; i++)
        {
            var item = material.Items[i];
            var header = this.memory
                ? $"[{i + 1}] title: {item.Title ?? "-"} · tags: {(item.Tags.Count == 0 ? "-" : string.Join(", ", item.Tags))}"
                : string.Create(CultureInfo.InvariantCulture, $"[{i + 1}] {item.Time:yyyy-MM-dd HH:mm} UTC · channel {item.Channel} · {item.Role}");
            text.AppendLine(header);
            text.AppendLine(this.Clip(item.Text));
        }

        return text.ToString();
    }

    private string Clip(string text) => text.Length <= this.maxChars ? text : string.Concat(text.AsSpan(0, this.maxChars), "…");
}

/// <summary>Checks of a draft before it is accepted (reasons hold no text).</summary>
public static class DraftChecks
{
    /// <summary>Longest run of snippet words a query may repeat.</summary>
    public const int MaxSharedRun = 3;

    /// <summary>Why <paramref name="answer"/> is not usable for <paramref name="seed"/>, or null when it is.</summary>
    public static string? Problem(LabelSeed seed, SeedMaterial material, DraftAnswer answer, Func<string, IReadOnlyList<string>, bool> paraphraseOk)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(paraphraseOk);
        var text = answer.Text ?? string.Empty;
        if (text.Length is 0 or > 300)
        {
            return "the query is empty or longer than 300 characters.";
        }

        var sources = material.Items.Select(i => i.Text).ToList();
        if (seed.Stratum != "real" && answer.Category != "identifier" && sources.Any(s => SharesRun(text, s, MaxSharedRun + 1)))
        {
            return "it repeats four or more consecutive words of the snippet.";
        }

        switch (answer.Category)
        {
            case "paraphrase" when !paraphraseOk(text, sources):
                return "it shares words with the snippet (paraphrase guard: Jaccard above 0.2 or a shared rare word).";
            case "identifier" when !QueryIntent.HasExactIntent(text):
                return "it does not contain the identifier as written.";
            case "temporal" when seed.Stratum != "real" && !Contains(answer.GoldFilters, material.Items[0].Time):
                return "goldFilters.since/until must name a period that contains the message time.";
            case "tag_filtered" when answer.Tags is not { Count: > 0 } tags
                || (seed.Stratum != "real" && !tags.All(t => material.Items.SelectMany(i => i.Tags).Contains(t, StringComparer.OrdinalIgnoreCase))):
                return "tags must be a non-empty subset of the memory's tags.";
            default:
                return null;
        }
    }

    /// <summary>True when <paramref name="query"/> repeats <paramref name="run"/> consecutive folded words of <paramref name="source"/>.</summary>
    public static bool SharesRun(string query, string source, int run)
    {
        var words = KeywordTokenizer.Tokenize(TextFolder.Fold(query));
        if (words.Count < run)
        {
            return false;
        }

        var grams = Grams(KeywordTokenizer.Tokenize(TextFolder.Fold(source)), run);
        return Grams(words, run).Any(grams.Contains);
    }

    private static HashSet<string> Grams(IReadOnlyList<string> words, int run) =>
        Enumerable.Range(0, Math.Max(0, words.Count - run + 1)).Select(i => string.Join(' ', words.Skip(i).Take(run))).ToHashSet(StringComparer.Ordinal);

    private static bool Contains(EvalFilters? filters, DateTimeOffset? time) =>
        filters is not null && time is { } t && (filters.Since is not null || filters.Until is not null)
        && (filters.Since is null || filters.Since <= t) && (filters.Until is null || t < filters.Until);
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~QueryDrafterTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 9`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/QueryDrafter.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/QueryDrafterTests.cs
git commit -m "feat(eval): query writer with copy check, paraphrase guard and retries" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Model-written queries

"Oracle vs model-written filters" (§4.2): the agent's main model gets the real tool description and schema and writes its own search; real requests use the recorded arguments. `MemorySearchTool` exposes its description statically like `HistorySearchTool` (L5).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/MainModelQueryWriter.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/MainModelQueryWriterTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/MainModelQueryWriterTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class MainModelQueryWriterTests
{
    private readonly FakeLabellingLlm fake = new();

    private MainModelQueryWriter Writer() =>
        new(new LabellingLlm(this.fake, FakeLabellingLlm.ServesAll, new LabellingOptions { MinCallIntervalMs = 0 }, delay: (_, _) => Task.CompletedTask), "main-model");

    [Fact]
    public async Task WriteAsync_History_UsesTheToolDescriptionAndReadsFilters()
    {
        this.fake.Respond = (_, _, _) => """{"query":"ferry TownB","order":"relevance","since":"2026-03-01T00:00:00Z","until":null,"channel":"discord-dm"}""";

        var written = await this.Writer().WriteAsync("history-eval", "when did we book the ferry", CancellationToken.None);

        Assert.Equal("ferry TownB", written!.Text);
        Assert.Equal("discord-dm", written.Filters!.Channel);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), written.Filters.Since);
        var request = Assert.Single(this.fake.Requests);
        Assert.Equal("main-model", request.Model);
        Assert.Contains("history_search", request.System, StringComparison.Ordinal);
        Assert.Contains("when did we book the ferry", request.User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_Memory_ReadsTagsGivenAsAString()
    {
        this.fake.Respond = (_, _, _) => """{"query":"boat invoice","tags":"[\"finance\"]"}""";

        var written = await this.Writer().WriteAsync("memory-eval", "which boat invoice did I pay", CancellationToken.None);

        Assert.Equal(["finance"], written!.Tags);
        Assert.Null(written.Filters);
        Assert.Contains("memory_search", this.fake.Requests[0].System, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"query\":\"cut")]
    [InlineData("{\"order\":\"time\"}")]
    [InlineData("[1,2]")]
    public void FromArgs_NoQuery_Null(string? json) => Assert.Null(MainModelQueryWriter.FromArgs(json));
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `MainModelQueryWriter` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/MainModelQueryWriter.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>A search as the agent's main model would issue it: text plus its filters (history) or tags (memory).</summary>
public sealed record ModelWrittenQuery(string Text, EvalFilters? Filters = null, IReadOnlyList<string>? Tags = null);

/// <summary>
/// The model-written side of "oracle vs model-written filters" (§4.2): the agent's main model gets exactly the tool
/// description and schema it sees in conversation and writes one <c>history_search</c> / <c>memory_search</c> call for
/// the query. Real requests already have one (the recorded arguments, <see cref="FromArgs"/>).
/// </summary>
public sealed class MainModelQueryWriter
{
    private readonly LabellingLlm llm;
    private readonly string model;
    private readonly TimeProvider time;

    public MainModelQueryWriter(LabellingLlm llm, string model, TimeProvider? time = null)
    {
        this.llm = llm;
        this.model = model;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The main model's call for <paramref name="request"/>, or null when it gave no usable answer.</summary>
    public async Task<ModelWrittenQuery?> WriteAsync(string tool, string request, CancellationToken cancellationToken)
    {
        var memory = string.Equals(tool, "memory-eval", StringComparison.Ordinal);
        var (name, description, schema) = memory
            ? ("memory_search", MemorySearchTool.ToolDescription, MemorySearchTool.ToolSchema)
            : ("history_search", HistorySearchTool.ToolDescription, HistorySearchTool.ToolSchema);
        var user =
            $"Tool description:\n{description}\n\nParameters schema:\n{schema}\n\n" +
            $"Current time (UTC): {this.time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)}\n\nUser request:\n{request}";
        var answer = await this.llm.CompleteJsonAsync<JsonObject>(
            this.model,
            $"You are deciding how to call the tool {name}. Return ONLY a JSON object with the arguments of ONE {name} call.",
            user,
            600,
            a => FromArgs(a.ToJsonString()) is not null,
            cancellationToken).ConfigureAwait(false);
        return answer is null ? null : FromArgs(answer.ToJsonString());
    }

    /// <summary>Text, filters and tags of recorded or written tool arguments; null without a query.</summary>
    public static ModelWrittenQuery? FromArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonObject? args;
        try
        {
            args = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (args?["query"] is not JsonValue query || !query.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var filters = new EvalFilters { Channel = Text(args["channel"]), Since = Time(args["since"]), Until = Time(args["until"]) };
        return new ModelWrittenQuery(
            text.Trim(),
            filters.Channel is null && filters.Since is null && filters.Until is null ? null : filters,
            Tags(args["tags"]));
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static DateTimeOffset? Time(JsonNode? node) =>
        Text(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    // memory_search takes tags as a JSON array or as a string holding one.
    private static List<string>? Tags(JsonNode? node)
    {
        var array = node as JsonArray;
        if (array is null && Text(node) is { } text)
        {
            try
            {
                array = JsonNode.Parse(text) as JsonArray;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var tags = array?.Select(Text).OfType<string>().ToList();
        return tags is { Count: > 0 } ? tags : null;
    }
}
```

`src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs` — apply:

```diff
@@ -32,7 +32,12 @@ internal sealed class MemorySearchTool : IAgentTool
 
     public string Name => "memory_search";
 
-    public string Description =>
+    public string Description => ToolDescription;
+
+    public string ParametersSchema => ToolSchema;
+
+    /// <summary>The description the model sees (shared with the evaluation's model-written queries).</summary>
+    internal static string ToolDescription =>
         "Search curated memories (stored notes and facts). Returns the most relevant memories, best first. " +
         "Use this to find previously stored information, notes, or context. " +
         "Search when the user asks about the past, or when missing history would materially change the answer. " +
@@ -44,7 +49,8 @@ internal sealed class MemorySearchTool : IAgentTool
         "mode: auto (default) uses the configured search; hybrid combines exact keywords with meaning; " +
         "use mode=keyword or a quoted phrase for exact names, numbers or identifiers, and mode=dense for meaning only.";
 
-    public string ParametersSchema => """
+    /// <summary>The parameters schema the model sees.</summary>
+    internal static string ToolSchema => """
         {
           "type": "object",
           "properties": {
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~MainModelQueryWriterTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/MainModelQueryWriter.cs src/Cortex.Contained.Agent.Host/Tools/BuiltIn/MemorySearchTool.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/MainModelQueryWriterTests.cs
git commit -m "feat(eval): model-written queries from the main model or the recorded call" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: Batched relevance judge

The judge grades pooled candidates 0–2 in batches with aliases (ids never reach the model), strict JSON naming every alias once; invalid batches are asked again and then split in halves (L7).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RelevanceJudge.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/RelevanceJudgeTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/RelevanceJudgeTests.cs` (new):

```csharp
using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed partial class RelevanceJudgeTests
{
    private readonly FakeLabellingLlm fake = new();

    private RelevanceJudge Judge(int batchSize, int attempts = 2) =>
        new(new LabellingLlm(this.fake, FakeLabellingLlm.ServesAll, new LabellingOptions { MinCallIntervalMs = 0, MaxAttempts = attempts }, delay: (_, _) => Task.CompletedTask),
            "claude-opus-5.5", batchSize, "history-eval");

    private static List<JudgeCandidate> Candidates(int count) =>
        Enumerable.Range(1, count).Select(i => new JudgeCandidate($"10{i}", $"2026-03-0{i} · user: zqtext {i}")).ToList();

    // Grades every alias of the prompt with (alias number % 3).
    private static string GradeAll(string user) =>
        "{\"grades\":{" + string.Join(',', Alias().Matches(user).Select(m => $"\"c{m.Groups[1].Value}\":{int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) % 3}")) + "}}";

    [GeneratedRegex(@"\[c(\d+)\]")]
    private static partial Regex Alias();

    [Fact]
    public async Task GradeAsync_Batches_MapsAliasesBackToIds()
    {
        this.fake.Respond = (_, _, user) => GradeAll(user);

        var grades = await this.Judge(batchSize: 2).GradeAsync("when was the ferry booked", Candidates(3), CancellationToken.None);

        Assert.Equal(2, this.fake.Requests.Count);
        Assert.Equal(1, grades!["101"]);
        Assert.Equal(2, grades["102"]);
        Assert.Equal(1, grades["103"]);
        Assert.All(this.fake.Requests, r => Assert.Equal("claude-opus-5.5", r.Model));
        Assert.Contains("Query: when was the ferry booked", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("[c1] 2026-03-01 · user: zqtext 1", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.DoesNotContain("101", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("2 = the item directly answers", this.fake.Requests[0].System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GradeAsync_BatchNeverValid_SplitsUntilSingleItemsSucceed()
    {
        this.fake.Respond = (_, _, user) => Alias().Count(user) > 1 ? """{"grades":{"c1":2}}""" : GradeAll(user);

        var grades = await this.Judge(batchSize: 4).GradeAsync("q", Candidates(4), CancellationToken.None);

        Assert.Equal(4, grades!.Count);
        Assert.Equal(2 + 2 + 2 + 4, this.fake.Requests.Count);
    }

    [Fact]
    public async Task GradeAsync_OutOfRangeGradeEverywhere_Null()
    {
        this.fake.Respond = (_, _, _) => """{"grades":{"c1":3}}""";

        Assert.Null(await this.Judge(batchSize: 1).GradeAsync("q", Candidates(1), CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `RelevanceJudge` / `JudgeCandidate` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RelevanceJudge.cs` (new):

```csharp
using System.Globalization;
using System.Text;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>One pooled candidate shown to the judge (raw text; never written out).</summary>
public sealed record JudgeCandidate(string Id, string Text);

/// <summary>The judge's answer (strict JSON): a grade per item alias.</summary>
public sealed record JudgeAnswer(Dictionary<string, int>? Grades);

/// <summary>
/// The relevance judge (a model of another family than the query writer, §4.2): grades pooled candidates 0–2 in
/// batches, as strict JSON keyed by short item aliases. An answer that misses an item, adds one or uses another
/// grade is asked again; a batch that never gets a valid answer is split in halves down to single items. Graded
/// event/message-level labels result (one grade per message or memory).
/// </summary>
public sealed class RelevanceJudge
{
    private const string Rubric = """
        For each item decide how well it answers the query:
        2 = the item directly answers the query or contains exactly the information asked for;
        1 = the item is related and partly answers it (part of the answer, one of several things asked for, or context needed to answer);
        0 = not relevant.
        Judge every item on its own text only and do not assume information that is not there. Items and queries may be in any language.
        Return ONLY a JSON object {"grades": {"<item id>": 0, 1 or 2}} with every item id exactly once.
        """;

    private readonly LabellingLlm llm;
    private readonly string model;
    private readonly int batchSize;
    private readonly string subject;

    /// <param name="llm">The pinned client.</param>
    /// <param name="model">The judge model.</param>
    /// <param name="batchSize">Candidates per call.</param>
    /// <param name="tool"><c>history-eval</c> (messages) or <c>memory-eval</c> (memories).</param>
    public RelevanceJudge(LabellingLlm llm, string model, int batchSize, string tool)
    {
        this.llm = llm;
        this.model = model;
        this.batchSize = batchSize;
        this.subject = string.Equals(tool, "memory-eval", StringComparison.Ordinal)
            ? "You judge search results over a person's curated memories (notes their assistant stored)."
            : "You judge search results over a person's chat history with their assistant; every item is one stored message.";
    }

    /// <summary>Grade per candidate id, or null when some candidate could not be graded.</summary>
    public async Task<IReadOnlyDictionary<string, int>?> GradeAsync(string query, IReadOnlyList<JudgeCandidate> candidates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var grades = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var batch in candidates.Chunk(this.batchSize))
        {
            if (!await this.GradeBatchAsync(query, batch, grades, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        return grades;
    }

    private async Task<bool> GradeBatchAsync(string query, JudgeCandidate[] batch, Dictionary<string, int> grades, CancellationToken cancellationToken)
    {
        var aliases = batch.Select((c, i) => (Alias: "c" + (i + 1).ToString(CultureInfo.InvariantCulture), Candidate: c)).ToList();
        var prompt = new StringBuilder();
        prompt.AppendLine(CultureInfo.InvariantCulture, $"Query: {query}");
        prompt.AppendLine();
        prompt.AppendLine("Items:");
        foreach (var (alias, candidate) in aliases)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"[{alias}] {candidate.Text}");
        }

        var expected = aliases.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal);
        var answer = await this.llm.CompleteJsonAsync<JudgeAnswer>(
            this.model,
            this.subject + "\n" + Rubric,
            prompt.ToString(),
            400 + (20 * batch.Length),
            a => a.Grades is { } g && g.Count == expected.Count && g.Keys.All(expected.Contains) && g.Values.All(v => v is >= 0 and <= 2),
            cancellationToken).ConfigureAwait(false);
        if (answer?.Grades is { } valid)
        {
            foreach (var (alias, candidate) in aliases)
            {
                grades[candidate.Id] = valid[alias];
            }

            return true;
        }

        if (batch.Length == 1)
        {
            return false;
        }

        var half = batch.Length / 2;
        return await this.GradeBatchAsync(query, batch[..half], grades, cancellationToken).ConfigureAwait(false)
            && await this.GradeBatchAsync(query, batch[half..], grades, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~RelevanceJudgeTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 3`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RelevanceJudge.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/RelevanceJudgeTests.cs
git commit -m "feat(eval): batched 0-2 relevance judge with strict JSON and batch splitting" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 9: Finalisation rules and the κ sample

Pre-declared rules turn judged drafts into valid dataset lines (L8) and the owner's κ sample is stratified by the judge's grade, one item per query while possible (L10).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/DatasetFinaliser.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/DatasetFinaliserTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/DatasetFinaliserTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class DatasetFinaliserTests
{
    private static DraftedQuery Draft(string id, string category, string stratum = "synthetic", IReadOnlyList<string>? tags = null) =>
        new() { Id = id, SeedId = "s" + id, Text = "query " + id, Category = category, Stratum = stratum, GoldSourceIds = ["1"], Tags = tags };

    private static Dictionary<string, int> Grades(params (string Id, int Grade)[] grades) =>
        grades.ToDictionary(g => g.Id, g => g.Grade, StringComparer.Ordinal);

    [Fact]
    public void Finalise_AppliesThePreDeclaredRules()
    {
        (DraftedQuery, IReadOnlyDictionary<string, int>)[] judged =
        [
            (Draft("h1", "lookup"), Grades(("1", 2), ("2", 0))),
            (Draft("h2", "lookup"), Grades(("1", 0))),
            (Draft("h3", "lookup", "real"), Grades(("5", 0))),
            (Draft("h4", "abstention"), Grades(("7", 1))),
            (Draft("h5", "abstention"), Grades(("7", 0))),
            (Draft("h6", "enumeration"), Grades(("1", 2), ("2", 0))),
            (Draft("h7", "paraphrase"), Grades(("1", 2))),
            (Draft("h8", "paraphrase"), Grades(("3", 2))),
        ];

        var (queries, report) = DatasetFinaliser.Finalise(judged, (draft, relevant) => draft.Id != "h7");

        Assert.Equal(["h1", "h3", "h4", "h5", "h6", "h7", "h8"], queries.Select(q => q.Draft.Id));
        Assert.Equal(("abstention", true), (queries[1].Category, queries[1].ExpectAbstain));
        Assert.Equal("lookup", queries[2].Category);
        Assert.Equal(("abstention", true), (queries[3].Category, queries[3].ExpectAbstain));
        Assert.Equal("lookup", queries[4].Category);
        Assert.Equal("lookup", queries[5].Category);
        Assert.Equal("paraphrase", queries[6].Category);
        Assert.Equal((8, 7, 1), (report.Judged, report.Kept, report.Dropped));
        Assert.Equal(1, report.Recategorised["paraphrase→lookup"]);
        Assert.Equal(1, report.Recategorised["lookup→abstention"]);
    }

    [Fact]
    public void ToHistory_ValidDatasetLineWithAllJudgments()
    {
        var draft = Draft("h1", "temporal") with
        {
            GoldFilters = new EvalFilters { Since = DateTimeOffset.UnixEpoch },
            ModelText = "ferry",
            ModelFilters = new EvalFilters { Channel = "ch" },
        };
        var (queries, _) = DatasetFinaliser.Finalise([(draft, Grades(("12", 2), ("3", 0)))], (_, _) => true);

        var line = DatasetFinaliser.ToHistory(queries[0], "gpt", "claude");

        Assert.Empty(EvalDataset.Validate([line]));
        Assert.Equal([3L, 12L], line.Judgments.Select(j => j.MessageId));
        Assert.Equal(("gpt", "claude"), (line.QueryModelFamily, line.JudgeModelFamily));
        Assert.Equal("ch", line.ModelQuery!.Filters!.Channel);
    }

    [Fact]
    public void ToMemory_TagsOnlyForTagFiltered()
    {
        var (queries, _) = DatasetFinaliser.Finalise(
            [(Draft("m1", "tag_filtered", tags: ["finance"]), Grades(("mem-a", 2))), (Draft("m2", "lookup", tags: ["finance"]), Grades(("mem-a", 1)))],
            (_, _) => true);

        var lines = queries.Select(q => DatasetFinaliser.ToMemory(q, "gpt", "claude")).ToList();

        Assert.Empty(MemoryEvalDataset.Validate(lines));
        Assert.Equal(["finance"], lines[0].Tags);
        Assert.Null(lines[1].Tags);
        Assert.Equal("1", lines[0].SourceMemoryId);
    }

    [Fact]
    public void Select_StratifiedByGradeAndSpreadOverQueries()
    {
        var queries = Enumerable.Range(0, 40)
            .Select(i => new LabelledQuery(Draft($"h{i}", "lookup"), "lookup", false, Grades(($"{i}0", 0), ($"{i}1", 1), ($"{i}2", 2))))
            .ToList();

        var picks = KappaSampler.Select(queries, 75);

        Assert.Equal(75, picks.Count);
        Assert.Equal([25, 25, 25], picks.GroupBy(p => p.LlmGrade).OrderBy(g => g.Key).Select(g => g.Count()));
        Assert.Equal(75, picks.Distinct().Count());
        Assert.Equal(picks, KappaSampler.Select(queries, 75));
    }

    [Fact]
    public void Select_ScarceGrade_ToppedUpFromTheOthers()
    {
        var queries = Enumerable.Range(0, 10)
            .Select(i => new LabelledQuery(Draft($"h{i}", "lookup"), "lookup", false, Grades(($"{i}0", 0), ($"{i}1", 0), ($"{i}2", i == 0 ? 2 : 0))))
            .ToList();

        var picks = KappaSampler.Select(queries, 9);

        Assert.Equal(9, picks.Count);
        Assert.Equal(1, picks.Count(p => p.LlmGrade == 2));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `DatasetFinaliser` / `LabelledQuery` / `KappaSampler` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/DatasetFinaliser.cs` (new):

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>A judged query after the pre-declared finalisation rules.</summary>
public sealed record LabelledQuery(DraftedQuery Draft, string Category, bool ExpectAbstain, IReadOnlyDictionary<string, int> Grades)
{
    /// <summary>Candidates graded ≥ 1.</summary>
    public IReadOnlyList<string> Relevant => this.Grades.Where(g => g.Value > 0).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
}

/// <summary>What finalisation did (counts only).</summary>
public sealed record FinaliseReport(int Judged, int Kept, int Dropped, IReadOnlyDictionary<string, int> Recategorised);

/// <summary>
/// Turns judged drafts into dataset queries with rules fixed before any system is compared (they are written into
/// the analysis plan): a synthetic query whose source the judge found irrelevant is dropped; a real request with no
/// relevant item becomes an abstention query; an abstention query with a relevant item becomes <c>lookup</c>;
/// enumeration/multi with fewer than two relevant items, paraphrase failing the guard against its judged gold and
/// tag_filtered without tags become <c>lookup</c>. Every judged candidate (grade 0 included) stays a judgment, so
/// judged@10 can be reported.
/// </summary>
public static class DatasetFinaliser
{
    /// <summary>Applies the rules; <paramref name="paraphraseOk"/> checks a paraphrase against its relevant items.</summary>
    public static (IReadOnlyList<LabelledQuery> Queries, FinaliseReport Report) Finalise(
        IEnumerable<(DraftedQuery Draft, IReadOnlyDictionary<string, int> Grades)> judged, Func<DraftedQuery, IReadOnlyList<string>, bool> paraphraseOk)
    {
        ArgumentNullException.ThrowIfNull(judged);
        ArgumentNullException.ThrowIfNull(paraphraseOk);
        var kept = new List<LabelledQuery>();
        var moved = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var (draft, grades) in judged)
        {
            total++;
            var relevant = grades.Where(g => g.Value > 0).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
            var category = draft.Category;
            if (category == "abstention" || relevant.Count == 0)
            {
                if (category != "abstention" && draft.Stratum != "real")
                {
                    continue;
                }

                category = relevant.Count == 0 ? "abstention" : "lookup";
            }
            else if ((category is "enumeration" or "multi" && relevant.Count < 2)
                || (category == "paraphrase" && !paraphraseOk(draft, relevant))
                || (category == "tag_filtered" && draft.Tags is not { Count: > 0 }))
            {
                category = "lookup";
            }

            if (category != draft.Category)
            {
                var key = draft.Category + "→" + category;
                moved[key] = moved.GetValueOrDefault(key) + 1;
            }

            kept.Add(new LabelledQuery(draft, category, category == "abstention", grades));
        }

        return (kept, new FinaliseReport(total, kept.Count, total - kept.Count, moved));
    }

    /// <summary>The history dataset line of <paramref name="query"/>.</summary>
    public static EvalQuery ToHistory(LabelledQuery query, string queryFamily, string judgeFamily)
    {
        ArgumentNullException.ThrowIfNull(query);
        var draft = query.Draft;
        return new EvalQuery
        {
            Id = draft.Id,
            Text = draft.Text,
            Category = query.Category,
            Stratum = draft.Stratum,
            Judgments = query.Grades
                .Select(g => new EvalJudgment(long.Parse(g.Key, CultureInfo.InvariantCulture), g.Value))
                .OrderBy(j => j.MessageId)
                .ToList(),
            GoldFilters = draft.GoldFilters,
            ModelQuery = draft.ModelText is null ? null : new EvalModelQuery { Text = draft.ModelText, Filters = draft.ModelFilters },
            QueryModelFamily = queryFamily,
            JudgeModelFamily = judgeFamily,
            ExpectAbstain = query.ExpectAbstain,
        };
    }

    /// <summary>The memory dataset line of <paramref name="query"/>.</summary>
    public static MemoryEvalQuery ToMemory(LabelledQuery query, string queryFamily, string judgeFamily)
    {
        ArgumentNullException.ThrowIfNull(query);
        var draft = query.Draft;
        return new MemoryEvalQuery
        {
            Id = draft.Id,
            Text = draft.Text,
            Category = query.Category,
            Stratum = draft.Stratum,
            Judgments = query.Grades.Select(g => new MemoryJudgment(g.Key, g.Value)).OrderBy(j => j.MemoryId, StringComparer.Ordinal).ToList(),
            Tags = query.Category == "tag_filtered" ? draft.Tags : null,
            SourceMemoryId = draft.GoldSourceIds.Count > 0 ? draft.GoldSourceIds[0] : null,
            ModelQuery = draft.ModelText is null ? null : new MemoryModelQuery { Text = draft.ModelText, Tags = draft.ModelTags },
            QueryModelFamily = queryFamily,
            JudgeModelFamily = judgeFamily,
            ExpectAbstain = query.ExpectAbstain,
        };
    }
}

/// <summary>One judged (query, candidate) pair picked for the owner's hand grading.</summary>
public sealed record KappaPick(string QueryId, string CandidateId, int LlmGrade);

/// <summary>
/// The κ sample: <c>count</c> judged pairs stratified by the judge's grade (equal thirds for 0, 1 and 2, topped up
/// from the other grades when one is scarce), at most one pair per query while possible, in a deterministic hash order.
/// </summary>
public static class KappaSampler
{
    public static IReadOnlyList<KappaPick> Select(IReadOnlyList<LabelledQuery> queries, int count, int seed = 20261121)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var pairs = queries
            .SelectMany(q => q.Grades.Select(g => new KappaPick(q.Draft.Id, g.Key, g.Value)))
            .OrderBy(p => Rank(seed, p.QueryId + "|" + p.CandidateId), StringComparer.Ordinal)
            .ToList();
        var chosen = new List<KappaPick>();
        var usedQueries = new HashSet<string>(StringComparer.Ordinal);
        for (var grade = 0; grade <= 2; grade++)
        {
            var quota = (count / 3) + (grade < count % 3 ? 1 : 0);
            chosen.AddRange(Take(pairs.Where(p => p.LlmGrade == grade), quota, chosen, usedQueries));
        }

        chosen.AddRange(Take(pairs, count - chosen.Count, chosen, usedQueries));
        return chosen;
    }

    private static List<KappaPick> Take(IEnumerable<KappaPick> pool, int wanted, List<KappaPick> chosen, HashSet<string> usedQueries)
    {
        var taken = new List<KappaPick>();
        var candidates = pool.Where(p => !chosen.Contains(p)).ToList();
        foreach (var distinct in new[] { true, false })
        {
            foreach (var pick in candidates)
            {
                if (taken.Count >= wanted)
                {
                    return taken;
                }

                if (!taken.Contains(pick) && (!distinct || !usedQueries.Contains(pick.QueryId)))
                {
                    taken.Add(pick);
                    usedQueries.Add(pick.QueryId);
                }
            }
        }

        return taken;
    }

    private static string Rank(int seed, string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString(CultureInfo.InvariantCulture) + ":" + id)));
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~DatasetFinaliserTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/DatasetFinaliser.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/DatasetFinaliserTests.cs
git commit -m "feat(eval): pre-declared finalisation rules and the grade-stratified kappa sample" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 10: The labelling pipeline

`Labeller` chains seeds → writer → main model → pool → judge → finalise → sealed dataset, κ sample and a counts-only report, checkpointing every stage; `ILabelSource` keeps it independent of the corpus (the tests use a fake source). The estimate is printed first and the job refuses when it exceeds the cap (L9).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/KappaSampleFile.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/Labeller.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellerTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellerTests.cs` (new):

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed partial class LabellerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"labeller-{Guid.NewGuid():N}");
    private readonly FakeLabellingLlm fake = new();
    private readonly FakeSource source = new();
    private readonly StringWriter output = new();
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());

    public LabellerTests()
    {
        Directory.CreateDirectory(this.root);
        this.fake.Respond = Respond;
    }

    public void Dispose()
    {
        this.output.Dispose();
        Directory.Delete(this.root, recursive: true);
    }

    // Writer: a fresh query per seed; main model: a search call; judge: grade 2 for the item named in the query, else 0.
    private static string Respond(string model, string system, string user) => model switch
    {
        "gpt-6.1-sol" when user.Contains("Request (", StringComparison.Ordinal) => """{"text":"zqquery about item 4","category":"lookup","goldFilters":{"until":"2027-01-01T00:00:00Z"}}""",
        "gpt-6.1-sol" => $"{{\"text\":\"zqquery about item {Item().Match(user).Groups[1].Value}\",\"category\":\"lookup\"}}",
        "main-model" => """{"query":"zqsearch"}""",
        _ => "{\"grades\":{" + string.Join(',', user.Split('\n').Where(l => l.StartsWith("[c", StringComparison.Ordinal)).Select(l =>
            $"\"{l[1..l.IndexOf(']', StringComparison.Ordinal)]}\":{(l.Contains($"zqitem {Target(user)} text", StringComparison.Ordinal) ? 2 : 0)}")) + "}}",
    };

    private static string Target(string user) => QueryItem().Match(user).Groups[1].Value;

    [GeneratedRegex(@"zqitem (\d+) ")]
    private static partial Regex Item();

    [GeneratedRegex(@"Query: zqquery about item (\d+)")]
    private static partial Regex QueryItem();

    private Labeller Create(LabellingOptions options) =>
        new(this.source, new LabellingLlm(this.fake, FakeLabellingLlm.ServesAll, options, new LabellingStateStore(this.root, this.protection).Load()?.Calls ?? 0, (_, _) => Task.CompletedTask),
            options, "main-model", this.root, this.protection, this.output);

    private static LabellingOptions Options(int maxCalls = 1000) =>
        new() { MinCallIntervalMs = 0, MaxCalls = maxCalls, PoolDepth = 3, JudgeBatchSize = 2, MaxCandidatesPerQuery = 6, RealShare = 0.2 };

    [Fact]
    public async Task RunAsync_Pipeline_WritesSealedDatasetKappaSampleAndCountsOnlyOutput()
    {
        var code = await this.Create(Options()).RunAsync(5, kappaItems: 4, CancellationToken.None);

        Assert.Equal(0, code);
        var written = this.source.Written!;
        Assert.Equal(5, written.Count);
        Assert.Equal("real", written[0].Draft.Stratum);
        Assert.Equal(T0, written[0].Draft.GoldFilters!.Until);
        Assert.Equal("zqrecorded", written[0].Draft.ModelText);
        Assert.All(written.Skip(1), q => Assert.Equal("zqsearch", q.Draft.ModelText));
        Assert.All(written, q => Assert.Single(q.Relevant));
        Assert.Equal(2, written[1].Grades[written[1].Draft.GoldSourceIds[0]]);
        Assert.Equal(4, this.fake.Requests.Count(r => r.Model == "main-model"));

        var sample = KappaSampleFile.Read(this.root, this.protection);
        Assert.Equal(4, sample.Count);
        Assert.All(sample, s => Assert.Null(s.HumanGrade));
        Assert.True(EvalProtection.IsSealed(Path.Combine(this.root, KappaSampleFile.FileName)));
        Assert.True(EvalProtection.IsSealed(Path.Combine(this.root, LabellingStateStore.FileName)));
        Assert.DoesNotContain("zq", this.output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("zq", await File.ReadAllTextAsync(Path.Combine(this.root, Labeller.ReportFileName)), StringComparison.Ordinal);
        Assert.Contains("Estimate for 5 history-eval queries", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CapReached_StopsAndTheNextRunContinuesWithoutRedrafting()
    {
        this.fake.Respond = (model, system, user) => model == "claude-opus-5.5" ? "{}" : Respond(model, system, user);
        Assert.Equal(3, await this.Create(Options(maxCalls: 30)).RunAsync(5, 4, CancellationToken.None));
        Assert.Contains("raise the cap", this.output.ToString(), StringComparison.Ordinal);
        Assert.Null(this.source.Written);

        this.fake.Requests.Clear();
        this.fake.Respond = Respond;
        Assert.Equal(0, await this.Create(Options()).RunAsync(5, 4, CancellationToken.None));

        Assert.Equal(5, this.source.Written!.Count);
        Assert.DoesNotContain(this.fake.Requests, r => r.Model == "gpt-6.1-sol");
    }

    [Fact]
    public async Task RunAsync_EstimateAboveTheCap_RefusedWithoutCalls()
    {
        Assert.Equal(2, await this.Create(Options(maxCalls: 5)).RunAsync(5, 4, CancellationToken.None));

        Assert.Empty(this.fake.Requests);
        Assert.Contains("MaxCalls", this.output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Ten items; seeds: one real request and lookups of items 1, 2, 3, ….</summary>
    private sealed class FakeSource : ILabelSource
    {
        private readonly Dictionary<string, MaterialItem> items = Enumerable.Range(1, 10)
            .ToDictionary(
                i => i.ToString(CultureInfo.InvariantCulture),
                i => new MaterialItem(i.ToString(CultureInfo.InvariantCulture), $"zqitem {i} text", "ch", T0.AddHours(i - 20), "user"));

        public string Tool => "history-eval";

        public IReadOnlyList<LabelledQuery>? Written { get; private set; }

        public Task<IReadOnlyList<LabelSeed>> SampleAsync(int count, double realShare, CancellationToken cancellationToken)
        {
            var real = new LabelSeed { SeedId = "s0001", Category = "real", Stratum = "real", RequestMessageId = "99", RequestTime = T0, ModelArgs = """{"query":"zqrecorded"}""" };
            var synthetic = Enumerable.Range(2, count - 1)
                .Select(i => new LabelSeed { SeedId = $"s{i:0000}", Category = "lookup", SourceIds = [(i - 1).ToString(CultureInfo.InvariantCulture)] });
            return Task.FromResult<IReadOnlyList<LabelSeed>>([real, .. synthetic]);
        }

        public Task<SeedMaterial?> MaterialAsync(LabelSeed seed, CancellationToken cancellationToken) =>
            Task.FromResult<SeedMaterial?>(seed.Stratum == "real"
                ? new SeedMaterial([], "zqwhat about item 4")
                : new SeedMaterial(seed.SourceIds.Select(id => this.items[id]).ToList()));

        public Task<IReadOnlyList<string>> PoolAsync(DraftedQuery query, int depth, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["4", "5", "6", "7"]);

        public Task<IReadOnlyDictionary<string, MaterialItem>> ReadAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, MaterialItem>>(ids.Where(this.items.ContainsKey).ToDictionary(id => id, id => this.items[id]));

        public bool ParaphraseOk(string text, IReadOnlyList<string> goldTexts) => true;

        public void WriteDataset(IReadOnlyList<LabelledQuery> queries, string queryFamily, string judgeFamily)
        {
            Assert.Equal(("gpt", "claude"), (queryFamily, judgeFamily));
            this.Written = queries;
        }
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `Labeller` / `ILabelSource` / `KappaSampleFile` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/KappaSampleFile.cs` (new):

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>One pair of the owner's κ sample: the judge's grade, the texts to grade blind, and the owner's grade once given.</summary>
public sealed record KappaItem
{
    public required string Tool { get; init; }

    public required string QueryId { get; init; }

    public required string CandidateId { get; init; }

    public required int LlmGrade { get; init; }

    public required string Query { get; init; }

    public required string Candidate { get; init; }

    /// <summary>The owner's grade (0–2), or null while not graded.</summary>
    public int? HumanGrade { get; init; }
}

/// <summary>The κ sample of one workspace (<c>kappa-sample.json</c>, sealed: it holds query and candidate text).</summary>
public static class KappaSampleFile
{
    public const string FileName = "kappa-sample.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The items in <paramref name="directory"/>, or none.</summary>
    public static IReadOnlyList<KappaItem> Read(string directory, EvalProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        var path = Path.Combine(directory, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<KappaItem>>(protection.ReadText(path), Json) ?? [] : [];
    }

    public static void Write(string directory, EvalProtection protection, IReadOnlyList<KappaItem> items)
    {
        ArgumentNullException.ThrowIfNull(protection);
        protection.WriteText(Path.Combine(directory, FileName), JsonSerializer.Serialize(items, Json));
    }
}
```

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/Labeller.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// What the labelling pipeline needs from a corpus (a private copy of the history or the memory store): seeds,
/// their material, the pooled candidates of every system in the ablation grid, texts by id, the paraphrase guard and
/// the dataset writer. <c>HistoryLabelSource</c> and <c>MemoryLabelSource</c> implement it; tests use a fake.
/// </summary>
public interface ILabelSource
{
    /// <summary><c>history-eval</c> or <c>memory-eval</c>.</summary>
    string Tool { get; }

    /// <summary>Stratified seeds, real requests included (<see cref="LabelSeeds"/>).</summary>
    Task<IReadOnlyList<LabelSeed>> SampleAsync(int count, double realShare, CancellationToken cancellationToken);

    /// <summary>The snippet or request of <paramref name="seed"/>; null when its sources no longer exist.</summary>
    Task<SeedMaterial?> MaterialAsync(LabelSeed seed, CancellationToken cancellationToken);

    /// <summary>The union of the top <paramref name="depth"/> of every pooled configuration (best ranks first), nothing at or after <see cref="DraftedQuery.NotAfter"/>.</summary>
    Task<IReadOnlyList<string>> PoolAsync(DraftedQuery query, int depth, CancellationToken cancellationToken);

    /// <summary>Items by id (missing ids are left out).</summary>
    Task<IReadOnlyDictionary<string, MaterialItem>> ReadAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken);

    /// <summary>The paraphrase guard of §4.2 (Jaccard ≤ 0.2, no shared rare token) against <paramref name="goldTexts"/>.</summary>
    bool ParaphraseOk(string text, IReadOnlyList<string> goldTexts);

    /// <summary>Writes the sealed <c>dataset.jsonl</c> of the workspace.</summary>
    void WriteDataset(IReadOnlyList<LabelledQuery> queries, string queryFamily, string judgeFamily);
}

/// <summary>Expected LLM calls and duration of a label job (printed before it starts; the cap is enforced).</summary>
public sealed record LabelEstimate(int WriterCalls, int MainCalls, int JudgeCalls, double Hours)
{
    public int Total => this.WriterCalls + this.MainCalls + this.JudgeCalls;
}

/// <summary>
/// The <c>label</c> job (§4.2 automated): seeds → query writer (paraphrase guard and copy check, rewritten on failure)
/// → the main model's version → candidate pool (union of the grid's top-k plus the gold source: judged@10) → batched
/// judge → pre-declared finalisation → sealed <c>dataset.jsonl</c>, the owner's κ sample and a counts-only report. Every
/// stage checkpoints to the sealed <see cref="LabellingState"/>, so a crash, a cancel or the call cap resumes where it
/// stopped. Output holds ids and numbers only.
/// </summary>
public sealed class Labeller
{
    /// <summary>Report file (counts only).</summary>
    public const string ReportFileName = "label-report.json";

    // Seconds per call assumed by the estimate (measured order of magnitude for large models), and candidates per query.
    private const double WriterSeconds = 5;
    private const double MainSeconds = 4;
    private const double JudgeSeconds = 15;
    private const int AssumedPoolSize = 30;
    private const int CheckpointEvery = 5;

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILabelSource source;
    private readonly LabellingLlm llm;
    private readonly LabellingOptions options;
    private readonly string mainModel;
    private readonly LabellingStateStore store;
    private readonly string workspace;
    private readonly EvalProtection protection;
    private readonly TextWriter output;
    private readonly TimeProvider time;

    public Labeller(
        ILabelSource source,
        LabellingLlm llm,
        LabellingOptions options,
        string mainModel,
        string workspace,
        EvalProtection protection,
        TextWriter output,
        TimeProvider? time = null)
    {
        this.source = source;
        this.llm = llm;
        this.options = options;
        this.mainModel = mainModel;
        this.workspace = workspace;
        this.protection = protection;
        this.output = output;
        this.time = time ?? TimeProvider.System;
        this.store = new LabellingStateStore(workspace, protection);
    }

    /// <summary>Calls and hours for <paramref name="queries"/> queries (real requests need no main-model call).</summary>
    public static LabelEstimate Estimate(int queries, LabellingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var real = (int)Math.Floor(queries * options.RealShare);
        var writer = (int)Math.Ceiling(queries * 1.3);
        var main = queries - real;
        var judge = (int)Math.Ceiling(queries * Math.Ceiling(Math.Min(options.MaxCandidatesPerQuery, AssumedPoolSize) / (double)options.JudgeBatchSize) * 1.1);
        var seconds = (writer * WriterSeconds) + (main * MainSeconds) + (judge * JudgeSeconds) + ((writer + main + judge) * options.MinCallIntervalMs / 1000.0);
        return new LabelEstimate(writer, main, judge, Math.Round(seconds / 3600, 1));
    }

    /// <summary>Runs (or resumes) the job: 0 done, 2 refused, 3 stopped at the call cap (run again to continue).</summary>
    public async Task<int> RunAsync(int queries, int kappaItems, CancellationToken cancellationToken)
    {
        var estimate = Estimate(queries, this.options);
        await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Estimate for {queries} {this.source.Tool} queries: ~{estimate.Total} LLM calls (writer {estimate.WriterCalls} on {this.options.QueryModel}, main model {estimate.MainCalls} on {this.mainModel}, judge {estimate.JudgeCalls} on {this.options.JudgeModel}), ~{estimate.Hours} h; cap {this.options.MaxCalls} calls.")).ConfigureAwait(false);
        if (estimate.Total > this.options.MaxCalls)
        {
            await this.output.WriteLineAsync("error: the estimate exceeds Evaluation:Labelling:MaxCalls; raise the cap or lower the query counts.").ConfigureAwait(false);
            return 2;
        }

        this.llm.EnsureServed(this.options.QueryModel, this.options.JudgeModel, this.mainModel);
        var state = this.store.Load() is { } saved && saved.Tool == this.source.Tool ? saved : new LabellingState { Tool = this.source.Tool };

        // A judge failure may have been transient (provider outage): a new run grades those queries again.
        state.FailedJudging.Clear();
        try
        {
            if (state.Seeds.Count == 0)
            {
                state.Seeds = [.. await this.source.SampleAsync(queries, this.options.RealShare, cancellationToken).ConfigureAwait(false)];
                this.store.Save(state);
            }

            await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{state.Seeds.Count} seeds ({state.Seeds.Count(s => s.Stratum == "real")} real requests); resuming with {state.Drafts.Count} drafted, {state.Pools.Count} pooled, {state.Grades.Count} judged.")).ConfigureAwait(false);
            await this.DraftAsync(state, cancellationToken).ConfigureAwait(false);
            await this.PoolAsync(state, cancellationToken).ConfigureAwait(false);
            await this.JudgeAsync(state, cancellationToken).ConfigureAwait(false);
            await this.FinishAsync(state, kappaItems, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (LabellingBudgetExceededException ex)
        {
            await this.output.WriteLineAsync(ex.Message + " Progress is saved: raise the cap and run the job again to continue.").ConfigureAwait(false);
            return 3;
        }
        finally
        {
            state.Calls = this.llm.Calls;
            this.store.Save(state);
        }
    }

    /// <summary>A candidate as the judge and the owner see it.</summary>
    public static string Render(MaterialItem item, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(item);
        var text = item.Text.Length <= maxChars ? item.Text : string.Concat(item.Text.AsSpan(0, maxChars), "…");
        return item.Time is { } time
            ? string.Create(CultureInfo.InvariantCulture, $"{time:yyyy-MM-dd HH:mm} UTC · {item.Channel} · {item.Role}: {text}")
            : $"title: {item.Title ?? "-"} · tags: {(item.Tags.Count == 0 ? "-" : string.Join(", ", item.Tags))}\n{text}";
    }

    private async Task DraftAsync(LabellingState state, CancellationToken cancellationToken)
    {
        var drafter = new QueryDrafter(this.llm, this.options.QueryModel, this.source.Tool, this.options.MaxDraftAttempts, this.options.MaxSnippetChars, this.time);
        var writer = new MainModelQueryWriter(this.llm, this.mainModel, this.time);
        var prefix = this.source.Tool == "memory-eval" ? "m" : "h";
        var pending = 0;
        foreach (var seed in state.Seeds.Where(s => !state.Drafts.ContainsKey(s.SeedId) && !state.FailedSeeds.Contains(s.SeedId)).ToList())
        {
            var material = await this.source.MaterialAsync(seed, cancellationToken).ConfigureAwait(false);
            var answer = material is null ? null : await drafter.DraftAsync(seed, material, this.source.ParaphraseOk, cancellationToken).ConfigureAwait(false);
            if (answer is null)
            {
                state.FailedSeeds.Add(seed.SeedId);
            }
            else
            {
                var model = seed.Stratum == "real"
                    ? MainModelQueryWriter.FromArgs(seed.ModelArgs)
                    : await writer.WriteAsync(this.source.Tool, answer.Text!, cancellationToken).ConfigureAwait(false);
                state.Drafts[seed.SeedId] = new DraftedQuery
                {
                    Id = prefix + seed.SeedId[1..],
                    SeedId = seed.SeedId,
                    Text = answer.Text!,
                    Category = answer.Category!,
                    Stratum = seed.Stratum,
                    GoldSourceIds = seed.SourceIsGold ? seed.SourceIds : [],
                    GoldFilters = Clamp(answer.GoldFilters, seed.RequestTime),
                    Tags = answer.Tags,
                    ModelText = model?.Text,
                    ModelFilters = model?.Filters,
                    ModelTags = model?.Tags,
                    NotAfter = seed.RequestTime,
                };
            }

            if (++pending % CheckpointEvery == 0)
            {
                state.Calls = this.llm.Calls;
                this.store.Save(state);
                await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"drafted {state.Drafts.Count} / {state.Seeds.Count} ({state.FailedSeeds.Count} given up), {this.llm.Calls} calls")).ConfigureAwait(false);
            }
        }

        this.store.Save(state);
    }

    private async Task PoolAsync(LabellingState state, CancellationToken cancellationToken)
    {
        foreach (var draft in state.Drafts.Values.Where(d => !state.Pools.ContainsKey(d.Id)).ToList())
        {
            var pooled = await this.source.PoolAsync(draft, this.options.PoolDepth, cancellationToken).ConfigureAwait(false);
            state.Pools[draft.Id] = draft.GoldSourceIds.Concat(pooled).Distinct(StringComparer.Ordinal).Take(this.options.MaxCandidatesPerQuery).ToList();
        }

        this.store.Save(state);
        await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"pooled {state.Pools.Count} queries, {state.Pools.Values.Sum(p => p.Count)} candidates (depth {this.options.PoolDepth})")).ConfigureAwait(false);
    }

    private async Task JudgeAsync(LabellingState state, CancellationToken cancellationToken)
    {
        var judge = new RelevanceJudge(this.llm, this.options.JudgeModel, this.options.JudgeBatchSize, this.source.Tool);
        var done = 0;
        foreach (var draft in state.Drafts.Values.Where(d => !state.Grades.ContainsKey(d.Id) && !state.FailedJudging.Contains(d.Id)).ToList())
        {
            var pool = state.Pools[draft.Id];
            var items = await this.source.ReadAsync(pool, cancellationToken).ConfigureAwait(false);
            var candidates = pool.Where(items.ContainsKey).Select(id => new JudgeCandidate(id, Render(items[id], this.options.MaxSnippetChars))).ToList();
            var grades = await judge.GradeAsync(draft.Text, candidates, cancellationToken).ConfigureAwait(false);
            if (grades is null)
            {
                state.FailedJudging.Add(draft.Id);
            }
            else
            {
                state.Grades[draft.Id] = new Dictionary<string, int>(grades, StringComparer.Ordinal);
            }

            state.Calls = this.llm.Calls;
            this.store.Save(state);
            if (++done % 25 == 0)
            {
                await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"judged {state.Grades.Count} / {state.Drafts.Count} ({state.FailedJudging.Count} failed), {this.llm.Calls} calls")).ConfigureAwait(false);
            }
        }
    }

    private async Task FinishAsync(LabellingState state, int kappaItems, CancellationToken cancellationToken)
    {
        var judged = state.Seeds
            .Where(s => state.Drafts.ContainsKey(s.SeedId) && state.Grades.ContainsKey(state.Drafts[s.SeedId].Id))
            .Select(s => state.Drafts[s.SeedId])
            .Select(d => (Draft: d, Grades: (IReadOnlyDictionary<string, int>)state.Grades[d.Id]))
            .ToList();
        var goldTexts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (draft, grades) in judged.Where(j => j.Draft.Category == "paraphrase"))
        {
            var relevant = grades.Where(g => g.Value > 0).Select(g => g.Key).ToList();
            goldTexts[draft.Id] = (await this.source.ReadAsync(relevant, cancellationToken).ConfigureAwait(false)).Values.Select(i => i.Text).ToList();
        }

        var (queries, report) = DatasetFinaliser.Finalise(judged, (draft, _) => this.source.ParaphraseOk(draft.Text, goldTexts[draft.Id]));
        var queryFamily = ModelFamily.Of(this.options.QueryModel);
        var judgeFamily = ModelFamily.Of(this.options.JudgeModel);
        this.source.WriteDataset(queries, queryFamily, judgeFamily);

        var picks = KappaSampler.Select(queries, kappaItems);
        var texts = await this.source.ReadAsync(picks.Select(p => p.CandidateId).Distinct(StringComparer.Ordinal).ToList(), cancellationToken).ConfigureAwait(false);
        var byId = queries.ToDictionary(q => q.Draft.Id, StringComparer.Ordinal);
        var sample = picks
            .Where(p => texts.ContainsKey(p.CandidateId))
            .Select(p => new KappaItem
            {
                Tool = this.source.Tool,
                QueryId = p.QueryId,
                CandidateId = p.CandidateId,
                LlmGrade = p.LlmGrade,
                Query = byId[p.QueryId].Draft.Text,
                Candidate = Render(texts[p.CandidateId], this.options.MaxSnippetChars),
            })
            .ToList();
        KappaSampleFile.Write(this.workspace, this.protection, sample);

        var summary = new
        {
            tool = this.source.Tool,
            queryModel = this.options.QueryModel,
            judgeModel = this.options.JudgeModel,
            mainModel = this.mainModel,
            seeds = state.Seeds.Count,
            realSeeds = state.Seeds.Count(s => s.Stratum == "real"),
            drafted = state.Drafts.Count,
            draftsGivenUp = state.FailedSeeds.Count,
            judgingFailed = state.FailedJudging.Count,
            report.Judged,
            report.Kept,
            report.Dropped,
            report.Recategorised,
            categories = queries.GroupBy(q => q.Category, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            strata = queries.GroupBy(q => q.Draft.Stratum, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            candidatesJudged = queries.Sum(q => q.Grades.Count),
            kappaItems = sample.Count,
            calls = this.llm.Calls,
        };
        await File.WriteAllTextAsync(Path.Combine(this.workspace, ReportFileName), JsonSerializer.Serialize(summary, ReportJson), cancellationToken).ConfigureAwait(false);
        await this.output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Labelled {queries.Count} {this.source.Tool} queries ({summary.strata.GetValueOrDefault("real")} real; {report.Dropped} dropped, {report.Recategorised.Values.Sum()} recategorised) with {summary.candidatesJudged} judgments; {sample.Count} items to grade by hand; {this.llm.Calls} LLM calls. Report: {Path.Combine(this.workspace, ReportFileName)}")).ConfigureAwait(false);
    }

    // A real request's oracle filters never reach past the moment it was asked.
    private static EvalFilters? Clamp(EvalFilters? filters, DateTimeOffset? requestTime) =>
        requestTime is not { } asked ? filters
        : (filters ?? new EvalFilters()) with { Until = filters?.Until is { } until && until < asked ? until : asked };
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~LabellerTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 3`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/KappaSampleFile.cs src/Cortex.Contained.Agent.Host/Evaluation/Labelling/Labeller.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabellerTests.cs
git commit -m "feat(eval): resumable labelling pipeline (draft, pool, judge, finalise, kappa sample)" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 11: History label source

`ILabelSource` over the embedded copy (`EvalCorpus`): eligible messages without recall echoes, real requests from the copy's events, the ablation grid's non-rerank configurations as pooled systems (interleaved by rank, nothing after a real request), the corpus's document frequencies for the guard, the sealed dataset writer. `EvalCorpus.OpenCopy` gives read-only access to the copy.

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/HistoryLabelSource.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RecallRequestMiner.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/HistoryLabelSourceTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/HistoryLabelSourceTests.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.History;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

/// <summary>The history label source on a real, embedded corpus copy (fake embedder, no Ollama).</summary>
public sealed class HistoryLabelSourceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly AblationConfig[] Configs =
    [
        new() { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword },
        new() { Name = "hybrid-rrf60" },
        new() { Name = "hybrid-rerank", RerankTopN = 10 },
    ];

    private readonly string root = Path.Combine(Path.GetTempPath(), $"history-label-{Guid.NewGuid():N}");
    private long ferry;
    private long asked;
    private long later;

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private async Task<(EvalCorpus Corpus, HistoryLabelSource Source, EvalWorkspace Workspace)> OpenAsync()
    {
        Directory.CreateDirectory(this.root);
        var source = Path.Combine(this.root, "messages.db");
        await using (var store = new MessageStore(source, NullLogger<MessageStore>.Instance))
        {
            for (var i = 0; i < 30; i++)
            {
                await store.SaveMessageAsync("u", i % 2 == 0 ? "ch" : "other", "user", $"we discussed the garden plan number {i} with the neighbours at length", T0.AddHours(i));
            }

            this.ferry = await store.SaveMessageAsync("u", "ch", "user", "we booked the night ferry to TownB for the fourteenth", T0.AddDays(3));
            await store.SaveMessageAsync("a", "ch", "assistant", "I found it: the night ferry to TownB", T0.AddDays(4), toolCalls: """[{"name":"history_search","args":"{}","ok":true}]""");
            this.asked = await store.SaveMessageAsync("u", "ch", "user", "and when did we book it?", T0.AddDays(5));
            this.later = await store.SaveMessageAsync("u", "ch", "user", "the ferry to TownB was late again", T0.AddDays(6));
            await store.AppendAsync(
                new EventDraft(EventTypes.ToolCall, AuthorKinds.Main) { ChannelId = "ch", Ts = T0.AddDays(5).AddSeconds(3), Payload = new JsonObject { ["tool"] = "history_search", ["toolCallId"] = "c1", ["args"] = """{"query":"ferry booking"}""" } },
                CancellationToken.None);
        }

        var workspace = new EvalWorkspace(Path.Combine(this.root, "eval"), EvalProtection.Plaintext);
        Directory.CreateDirectory(workspace.Directory);
        var corpus = await EvalCorpus.CreateAsync(
            source, Path.Combine(this.root, "run"), new HistoryOptions { Enabled = true, TimeZone = "UTC" }, Configs[..2], _ => new FakeHistoryEmbedder(), CancellationToken.None);
        return (corpus, new HistoryLabelSource(corpus, workspace, Configs), workspace);
    }

    [Fact]
    public async Task SampleAndMaterial_SkipRecallEchoesAndPairTheRealRequestWithItsContext()
    {
        var (corpus, source, _) = await this.OpenAsync();
        await using (corpus)
        {
            var seeds = await source.SampleAsync(10, 0.3, CancellationToken.None);

            var real = Assert.Single(seeds, s => s.Stratum == "real");
            Assert.Equal(this.asked.ToString(CultureInfo.InvariantCulture), real.RequestMessageId);
            Assert.DoesNotContain(seeds.SelectMany(s => s.SourceIds), id => id == (this.ferry + 1).ToString(CultureInfo.InvariantCulture));
            var material = (await source.MaterialAsync(real, CancellationToken.None))!;
            Assert.Equal("and when did we book it?", material.Request);
            Assert.Equal(HistoryLabelSource.ContextTurns, material.Context.Count);
            Assert.StartsWith("assistant: I found it", material.Context[^1], StringComparison.Ordinal);
            var synthetic = seeds.First(s => s.Stratum == "synthetic");
            Assert.Equal(synthetic.SourceIds.Count, (await source.MaterialAsync(synthetic, CancellationToken.None))!.Items.Count);
        }
    }

    [Fact]
    public async Task PoolAsync_UnionOfTheSystemsWithoutLaterMessages()
    {
        var (corpus, source, _) = await this.OpenAsync();
        await using (corpus)
        {
            var draft = new DraftedQuery { Id = "h0001", SeedId = "s0001", Text = "ferry TownB", Category = "lookup" };

            var all = await source.PoolAsync(draft, 10, CancellationToken.None);
            var early = await source.PoolAsync(draft with { NotAfter = T0.AddDays(5) }, 10, CancellationToken.None);

            Assert.Contains(this.ferry.ToString(CultureInfo.InvariantCulture), all);
            Assert.Contains(this.later.ToString(CultureInfo.InvariantCulture), all);
            Assert.Contains(this.ferry.ToString(CultureInfo.InvariantCulture), early);
            Assert.DoesNotContain(this.later.ToString(CultureInfo.InvariantCulture), early);
            Assert.Equal(all.Count, all.Distinct().Count());
        }
    }

    [Fact]
    public async Task WriteDatasetAndGuard_DatasetReadableAndGuardUsesTheCorpus()
    {
        var (corpus, source, workspace) = await this.OpenAsync();
        await using (corpus)
        {
            var draft = new DraftedQuery { Id = "h0001", SeedId = "s0001", Text = "overnight boat", Category = "paraphrase", GoldSourceIds = [this.ferry.ToString(CultureInfo.InvariantCulture)] };
            var grades = new Dictionary<string, int> { [this.ferry.ToString(CultureInfo.InvariantCulture)] = 2 };
            var (queries, _) = DatasetFinaliser.Finalise([(draft, grades)], (_, _) => true);

            source.WriteDataset(queries, "gpt", "claude");

            var line = Assert.Single(workspace.ReadDataset());
            Assert.Equal(this.ferry, Assert.Single(line.Judgments).MessageId);
            Assert.True(source.ParaphraseOk("overnight boat", ["we booked the night ferry to TownB"]));
            Assert.False(source.ParaphraseOk("night ferry TownB", ["we booked the night ferry to TownB"]));
        }
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `HistoryLabelSource` not found and CS1061 `EvalCorpus` has no `OpenCopy`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/HistoryLabelSource.cs` (new):

```csharp
using System.Globalization;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// <see cref="ILabelSource"/> over a private, embedded copy of <c>messages.db</c> (<see cref="EvalCorpus"/>): user and
/// assistant messages (not recall echoes, not internal rows) as seeds, <c>history_search</c> calls as real requests,
/// the ablation grid's configurations (without reranking) as the pooled systems, and the corpus's own document
/// frequencies for the paraphrase guard.
/// </summary>
public sealed class HistoryLabelSource : ILabelSource
{
    /// <summary>Turns shown before a real request.</summary>
    public const int ContextTurns = 4;

    private readonly EvalCorpus corpus;
    private readonly EvalWorkspace workspace;
    private readonly IReadOnlyList<AblationConfig> pooled;
    private long? segments;

    /// <param name="corpus">The embedded copy (built for every keyword variant of <paramref name="pooled"/>).</param>
    /// <param name="workspace">Where the dataset is written (its protection seals it).</param>
    /// <param name="pooled">The configurations whose top-k is pooled (reranked ones are left out).</param>
    public HistoryLabelSource(EvalCorpus corpus, EvalWorkspace workspace, IReadOnlyList<AblationConfig> pooled)
    {
        ArgumentNullException.ThrowIfNull(pooled);
        this.corpus = corpus;
        this.workspace = workspace;
        this.pooled = pooled.Where(c => c.RerankBudget() is null).ToList();
    }

    public string Tool => "history-eval";

    public Task<IReadOnlyList<LabelSeed>> SampleAsync(int count, double realShare, CancellationToken cancellationToken)
    {
        using var connection = this.corpus.OpenCopy();
        var messages = new List<SourceMessage>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, ChannelId, Role, Timestamp, Content, ToolCalls FROM Messages WHERE Role IN ('user', 'assistant') AND Category IN (0, 3) ORDER BY Id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var echo = !reader.IsDBNull(5) && ToolCallSummary.ParseJson(reader.GetString(5))
                    .Any(e => ToolCallSummary.IsRecallSource(e.Name) || ToolCallSummary.IsRecallTurnMarker(e));
                if (!echo)
                {
                    messages.Add(new SourceMessage(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), SqliteDateTimeText.Parse(reader.GetString(3)), reader.GetString(4)));
                }
            }
        }

        var real = RecallRequestMiner.Read(connection, "history_search");
        return Task.FromResult(LabelSeeds.History(messages, real, count, realShare));
    }

    public async Task<SeedMaterial?> MaterialAsync(LabelSeed seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Stratum == "real")
        {
            return seed.RequestMessageId is { } id ? this.Request(long.Parse(id, CultureInfo.InvariantCulture)) : null;
        }

        var items = await this.ReadAsync(seed.SourceIds, cancellationToken).ConfigureAwait(false);
        return items.Count == seed.SourceIds.Count ? new SeedMaterial(seed.SourceIds.Select(id => items[id]).ToList()) : null;
    }

    public async Task<IReadOnlyList<string>> PoolAsync(DraftedQuery query, int depth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var probe = new EvalQuery
        {
            Id = query.Id,
            Text = query.Text,
            Category = query.Category,
            GoldFilters = query.GoldFilters,
            ModelQuery = query.ModelText is null ? null : new EvalModelQuery { Text = query.ModelText, Filters = query.ModelFilters },
        };
        var lists = new List<IReadOnlyList<long>>();
        foreach (var config in this.pooled)
        {
            var run = await this.corpus.SearchAsync(config, probe, cancellationToken).ConfigureAwait(false);
            lists.Add(RankingMetrics.RankedMessages(run.Hits).Take(depth).ToList());
        }

        // Interleaved by rank, so a cap on the pool keeps every system's best results.
        var union = Enumerable.Range(0, depth).SelectMany(rank => lists.Where(l => rank < l.Count).Select(l => l[rank])).Distinct().ToList();
        if (query.NotAfter is { } notAfter)
        {
            var rows = await this.corpus.ReadRowsAsync(union, cancellationToken).ConfigureAwait(false);
            var early = rows.Where(r => r.Timestamp < notAfter).Select(r => r.Id).ToHashSet();
            union = union.Where(early.Contains).ToList();
        }

        return union.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
    }

    public async Task<IReadOnlyDictionary<string, MaterialItem>> ReadAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var numbers = ids.Select(id => long.TryParse(id, CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n > 0).ToList();
        var rows = await this.corpus.ReadRowsAsync(numbers, cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(
            r => r.Id.ToString(CultureInfo.InvariantCulture),
            r => new MaterialItem(r.Id.ToString(CultureInfo.InvariantCulture), r.Content, r.ChannelId, r.Timestamp, r.Role),
            StringComparer.Ordinal);
    }

    public bool ParaphraseOk(string text, IReadOnlyList<string> goldTexts)
    {
        this.segments ??= this.corpus.SegmentCount();
        return ParaphraseGuard.Check("draft", text, goldTexts, this.corpus.DocumentFrequency, this.segments.Value).Passed;
    }

    public void WriteDataset(IReadOnlyList<LabelledQuery> queries, string queryFamily, string judgeFamily)
    {
        ArgumentNullException.ThrowIfNull(queries);
        this.workspace.Protection.WriteLines(this.workspace.DatasetPath, queries.Select(q => EvalDataset.ToLine(DatasetFinaliser.ToHistory(q, queryFamily, judgeFamily))));
    }

    // The owner's request and up to four earlier user/assistant turns of its channel.
    private SeedMaterial? Request(long messageId)
    {
        using var connection = this.corpus.OpenCopy();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.Content,
                   (SELECT json_group_array(Role || ': ' || Content) FROM (
                       SELECT m.Role, m.Content FROM Messages m
                        WHERE m.ChannelId = r.ChannelId AND m.Role IN ('user', 'assistant') AND m.Category IN (0, 3)
                          AND (m.Timestamp < r.Timestamp OR (m.Timestamp = r.Timestamp AND m.Id < r.Id))
                        ORDER BY m.Timestamp DESC, m.Id DESC LIMIT @turns))
              FROM Messages r WHERE r.Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", messageId);
        cmd.Parameters.AddWithValue("@turns", ContextTurns);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var context = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [];
        context.Reverse();
        return new SeedMaterial([], reader.GetString(0)) { Context = context };
    }
}
```

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RecallRequestMiner.cs` — apply:

```diff
@@ -24,6 +24,13 @@ public static class RecallRequestMiner
 
         using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(db, password, SqliteOpenMode.ReadOnly, pooling: false));
         connection.Open();
+        return Read(connection, tool);
+    }
+
+    /// <summary>Seeds for the searches of <paramref name="tool"/> through an open connection to a <c>messages.db</c> (or a copy).</summary>
+    public static IReadOnlyList<LabelSeed> Read(SqliteConnection connection, string tool)
+    {
+        ArgumentNullException.ThrowIfNull(connection);
         using var cmd = connection.CreateCommand();
         cmd.CommandText = """
             SELECT e.Ts, json_extract(e.Payload, '$.args'),
```

`src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs` — apply:

```diff
@@ -25,10 +25,14 @@ public sealed class EvalCorpus : IAsyncDisposable
     private readonly MessageStore store;
     private readonly MutableOptionsMonitor monitor;
     private readonly IRerankStage? reranker;
+    private readonly string copyPath;
+    private readonly string? copyPassword;
     private readonly Dictionary<HistoryKeywordVariant, (HistoryProjector Projector, HistoryRecallService Recall)> variants = [];
 
-    private EvalCorpus(MessageStore store, MutableOptionsMonitor monitor, IRerankStage? reranker)
+    private EvalCorpus(MessageStore store, MutableOptionsMonitor monitor, IRerankStage? reranker, string copyPath, string? copyPassword)
     {
+        this.copyPath = copyPath;
+        this.copyPassword = copyPassword;
         this.store = store;
         this.monitor = monitor;
         this.reranker = reranker;
@@ -65,7 +69,7 @@ public sealed class EvalCorpus : IAsyncDisposable
 
         var monitor = new MutableOptionsMonitor(baseline);
         var embedder = embedderFactory(monitor);
-        var corpus = new EvalCorpus(new MessageStore(copyPath, NullLogger<MessageStore>.Instance, protection.CopyMessageEncryption), monitor, reranker);
+        var corpus = new EvalCorpus(new MessageStore(copyPath, NullLogger<MessageStore>.Instance, protection.CopyMessageEncryption), monitor, reranker, copyPath, copyPassword);
         foreach (var variant in configs.Select(c => c.KeywordVariant).Distinct())
         {
             monitor.CurrentValue = new AblationConfig { Name = "build", KeywordVariant = variant }.Apply(baseline);
@@ -157,6 +161,14 @@ public sealed class EvalCorpus : IAsyncDisposable
         return new RetrievalRun(hits, confidence, latency) { Rerank = rerankOutcome };
     }
 
+    /// <summary>A read-only connection to the private copy (automated labelling reads rows and events; the caller disposes it).</summary>
+    public SqliteConnection OpenCopy()
+    {
+        var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(this.copyPath, this.copyPassword, SqliteOpenMode.ReadOnly, pooling: false));
+        connection.Open();
+        return connection;
+    }
+
     /// <summary>Texts of the given rows (paraphrase guard only; never written out).</summary>
     public async Task<IReadOnlyList<MessageRecord>> ReadRowsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
     {
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~HistoryLabelSourceTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 3`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/HistoryLabelSource.cs src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RecallRequestMiner.cs src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/HistoryLabelSourceTests.cs
git commit -m "feat(eval): history label source over the embedded corpus copy" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 12: Memory label source

The same over the memory copy (`MemoryEvalCorpus`); real requests come from `messages.db` (read-only). The request-and-context query moves into `RecallRequestMiner.Material` so both sources share it.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/HistoryLabelSource.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/MemoryLabelSource.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RecallRequestMiner.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/MemoryLabelSourceTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/MemoryLabelSourceTests.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

/// <summary>The memory label source on a private copy of a seeded store (fake embedder, no Ollama).</summary>
public sealed class MemoryLabelSourceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"memory-label-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions live;
    private readonly string messages;

    public MemoryLabelSourceTests()
    {
        this.live = new MemoryMcpOptions { DataDirectory = Path.Combine(this.root, "live"), Ollama = new OllamaOptions { Dimensions = FakeMemoryEmbedder.Dimensions } };
        this.messages = Path.Combine(this.root, "messages.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private async Task<(MemoryEvalCorpus Corpus, MemoryLabelSource Source, MemoryEvalWorkspace Workspace, long Asked)> OpenAsync()
    {
        await MemoryEvalCorpusTests.SeedAsync(
            this.live,
            [
                .. Enumerable.Range(0, 20).Select(i => ($"mem-{i:00}", (string?)null, $"note {i} about the garden and the neighbours", new[] { i % 2 == 0 ? "home" : "garden" })),
                ("mem-train", "Trip", "Train trip: the night train to TownB on Friday", ["travel"]),
                ("mem-invoice", null, "Invoice INV-2041 was paid in March", ["finance"]),
            ]);
        long asked;
        await using (var store = new MessageStore(this.messages, NullLogger<MessageStore>.Instance))
        {
            asked = await store.SaveMessageAsync("u", "ch", "user", "which train did I take to TownB?", T0);
            await store.AppendAsync(
                new EventDraft(EventTypes.ToolCall, AuthorKinds.Main) { ChannelId = "ch", Ts = T0.AddSeconds(2), Payload = new JsonObject { ["tool"] = "memory_search", ["toolCallId"] = "c1", ["args"] = """{"query":"train TownB","tags":"[\"travel\"]"}""" } },
                CancellationToken.None);
        }

        var workspace = new MemoryEvalWorkspace(Path.Combine(this.root, "eval"), EvalProtection.Plaintext);
        Directory.CreateDirectory(workspace.Directory);
        var corpus = await MemoryEvalCorpus.CreateAsync(this.live.DataDirectory, Path.Combine(this.root, "run"), this.live, new FakeMemoryEmbedder(), CancellationToken.None);
        MemoryAblationConfig[] configs = [new() { Name = "dense", Mode = MemorySearchMode.Dense }, new() { Name = "keyword-plain", Mode = MemorySearchMode.Keyword }];
        var source = new MemoryLabelSource(corpus, workspace, configs, () =>
        {
            var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(this.messages, null, SqliteOpenMode.ReadOnly, pooling: false));
            connection.Open();
            return connection;
        });
        return (corpus, source, workspace, asked);
    }

    [Fact]
    public async Task SampleMaterialPoolAndWrite_WorkOnTheCopy()
    {
        var (corpus, source, workspace, asked) = await this.OpenAsync();
        await using (corpus)
        {
            var seeds = await source.SampleAsync(12, 0.3, CancellationToken.None);

            var real = Assert.Single(seeds, s => s.Stratum == "real");
            Assert.Equal(asked.ToString(CultureInfo.InvariantCulture), real.RequestMessageId);
            Assert.Equal("which train did I take to TownB?", (await source.MaterialAsync(real, CancellationToken.None))!.Request);
            Assert.Equal("train TownB", MainModelQueryWriter.FromArgs(real.ModelArgs)!.Text);
            Assert.Equal(["travel"], MainModelQueryWriter.FromArgs(real.ModelArgs)!.Tags);

            var draft = new DraftedQuery { Id = "m0001", SeedId = "s0001", Text = "railway journey", Category = "paraphrase", GoldSourceIds = ["mem-train"] };
            var pool = await source.PoolAsync(draft, 3, CancellationToken.None);
            Assert.Equal("mem-train", pool[0]);
            Assert.True(source.ParaphraseOk("railway journey", ["Train trip: the night train to TownB on Friday"]));
            Assert.False(source.ParaphraseOk("night train TownB", ["Train trip: the night train to TownB on Friday"]));

            var (queries, _) = DatasetFinaliser.Finalise([(draft, new Dictionary<string, int> { ["mem-train"] = 2, ["mem-00"] = 0 })], (_, _) => true);
            source.WriteDataset(queries, "gpt", "claude");
            var line = Assert.Single(workspace.ReadDataset());
            Assert.Equal(2, line.Judgments.Count);
            Assert.Equal("mem-train", line.SourceMemoryId);
        }
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `MemoryLabelSource` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/HistoryLabelSource.cs` — apply:

```diff
@@ -61,7 +61,8 @@ public sealed class HistoryLabelSource : ILabelSource
         ArgumentNullException.ThrowIfNull(seed);
         if (seed.Stratum == "real")
         {
-            return seed.RequestMessageId is { } id ? this.Request(long.Parse(id, CultureInfo.InvariantCulture)) : null;
+            using var connection = this.corpus.OpenCopy();
+            return seed.RequestMessageId is { } id ? RecallRequestMiner.Material(connection, long.Parse(id, CultureInfo.InvariantCulture), ContextTurns) : null;
         }
 
         var items = await this.ReadAsync(seed.SourceIds, cancellationToken).ConfigureAwait(false);
@@ -120,31 +121,4 @@ public sealed class HistoryLabelSource : ILabelSource
         ArgumentNullException.ThrowIfNull(queries);
         this.workspace.Protection.WriteLines(this.workspace.DatasetPath, queries.Select(q => EvalDataset.ToLine(DatasetFinaliser.ToHistory(q, queryFamily, judgeFamily))));
     }
-
-    // The owner's request and up to four earlier user/assistant turns of its channel.
-    private SeedMaterial? Request(long messageId)
-    {
-        using var connection = this.corpus.OpenCopy();
-        using var cmd = connection.CreateCommand();
-        cmd.CommandText = """
-            SELECT r.Content,
-                   (SELECT json_group_array(Role || ': ' || Content) FROM (
-                       SELECT m.Role, m.Content FROM Messages m
-                        WHERE m.ChannelId = r.ChannelId AND m.Role IN ('user', 'assistant') AND m.Category IN (0, 3)
-                          AND (m.Timestamp < r.Timestamp OR (m.Timestamp = r.Timestamp AND m.Id < r.Id))
-                        ORDER BY m.Timestamp DESC, m.Id DESC LIMIT @turns))
-              FROM Messages r WHERE r.Id = @id;
-            """;
-        cmd.Parameters.AddWithValue("@id", messageId);
-        cmd.Parameters.AddWithValue("@turns", ContextTurns);
-        using var reader = cmd.ExecuteReader();
-        if (!reader.Read())
-        {
-            return null;
-        }
-
-        var context = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [];
-        context.Reverse();
-        return new SeedMaterial([], reader.GetString(0)) { Context = context };
-    }
 }
```

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/MemoryLabelSource.cs` (new):

```csharp
using System.Globalization;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// <see cref="ILabelSource"/> over a private copy of the memory store (<see cref="MemoryEvalCorpus"/>): memories as
/// seeds (<see cref="MemoryQuerySeeds"/>), <c>memory_search</c> calls from the event log as real requests, the memory
/// ablation grid (without reranking) as the pooled systems, and document frequencies over the memories for the
/// paraphrase guard. Memories carry no reliable time, so real requests are not limited to older candidates.
/// </summary>
public sealed class MemoryLabelSource : ILabelSource
{
    private readonly MemoryEvalCorpus corpus;
    private readonly MemoryEvalWorkspace workspace;
    private readonly IReadOnlyList<MemoryAblationConfig> pooled;
    private readonly Func<SqliteConnection?> openMessages;
    private Dictionary<string, MemorySeedSource>? memories;
    private Dictionary<string, long>? documentFrequency;

    /// <param name="corpus">The memory copy.</param>
    /// <param name="workspace">Where the dataset is written (its protection seals it).</param>
    /// <param name="pooled">The configurations whose top-k is pooled (reranked ones are left out).</param>
    /// <param name="openMessages">Opens <c>messages.db</c> read-only for the real requests, or returns null.</param>
    public MemoryLabelSource(MemoryEvalCorpus corpus, MemoryEvalWorkspace workspace, IReadOnlyList<MemoryAblationConfig> pooled, Func<SqliteConnection?> openMessages)
    {
        ArgumentNullException.ThrowIfNull(pooled);
        this.corpus = corpus;
        this.workspace = workspace;
        this.pooled = pooled.Where(c => c.RerankBudget() is null).ToList();
        this.openMessages = openMessages;
    }

    public string Tool => "memory-eval";

    public async Task<IReadOnlyList<LabelSeed>> SampleAsync(int count, double realShare, CancellationToken cancellationToken)
    {
        var all = await this.MemoriesAsync(cancellationToken).ConfigureAwait(false);
        using var connection = this.openMessages();
        IReadOnlyList<LabelSeed> real = connection is null ? [] : RecallRequestMiner.Read(connection, "memory_search");
        return LabelSeeds.Memory(all.Values.OrderBy(m => m.MemoryId, StringComparer.Ordinal).ToList(), real, count, realShare);
    }

    public async Task<SeedMaterial?> MaterialAsync(LabelSeed seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Stratum == "real")
        {
            using var connection = this.openMessages();
            return connection is not null && seed.RequestMessageId is { } id
                ? RecallRequestMiner.Material(connection, long.Parse(id, CultureInfo.InvariantCulture), HistoryLabelSource.ContextTurns)
                : null;
        }

        var items = await this.ReadAsync(seed.SourceIds, cancellationToken).ConfigureAwait(false);
        return items.Count == seed.SourceIds.Count ? new SeedMaterial(seed.SourceIds.Select(id => items[id]).ToList()) : null;
    }

    public async Task<IReadOnlyList<string>> PoolAsync(DraftedQuery query, int depth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var probe = new MemoryEvalQuery
        {
            Id = query.Id,
            Text = query.Text,
            Category = query.Category,
            Tags = query.Category == "tag_filtered" ? query.Tags : null,
            ModelQuery = query.ModelText is null ? null : new MemoryModelQuery { Text = query.ModelText, Tags = query.ModelTags },
        };
        var lists = new List<IReadOnlyList<string>>();
        foreach (var config in this.pooled)
        {
            var run = await this.corpus.SearchAsync(config, probe, cancellationToken).ConfigureAwait(false);
            lists.Add(run.Memories.Take(depth).Select(m => m.MemoryId).ToList());
        }

        return Enumerable.Range(0, depth).SelectMany(rank => lists.Where(l => rank < l.Count).Select(l => l[rank])).Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyDictionary<string, MaterialItem>> ReadAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var all = await this.MemoriesAsync(cancellationToken).ConfigureAwait(false);
        return ids.Where(all.ContainsKey).Distinct(StringComparer.Ordinal).ToDictionary(
            id => id,
            id => new MaterialItem(id, all[id].Content, Title: all[id].Title) { Tags = all[id].Tags },
            StringComparer.Ordinal);
    }

    public bool ParaphraseOk(string text, IReadOnlyList<string> goldTexts)
    {
        var frequency = this.documentFrequency ?? throw new InvalidOperationException("Sample the memories before checking paraphrases.");
        return ParaphraseGuard.Check("draft", text, goldTexts, term => frequency.GetValueOrDefault(term), this.memories!.Count).Passed;
    }

    public void WriteDataset(IReadOnlyList<LabelledQuery> queries, string queryFamily, string judgeFamily)
    {
        ArgumentNullException.ThrowIfNull(queries);
        this.workspace.Protection.WriteLines(this.workspace.DatasetPath, queries.Select(q => MemoryEvalDataset.ToLine(DatasetFinaliser.ToMemory(q, queryFamily, judgeFamily))));
    }

    private async Task<Dictionary<string, MemorySeedSource>> MemoriesAsync(CancellationToken cancellationToken)
    {
        if (this.memories is null)
        {
            this.memories = (await this.corpus.ListMemoriesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(m => m.MemoryId, StringComparer.Ordinal);
            this.documentFrequency = this.memories.Values
                .SelectMany(m => KeywordTokenizer.Tokenize(TextFolder.Fold(m.Title + "\n" + m.Content)).Distinct(StringComparer.Ordinal))
                .GroupBy(t => t, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        }

        return this.memories;
    }
}
```

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RecallRequestMiner.cs` — apply:

```diff
@@ -69,6 +69,32 @@ public static class RecallRequestMiner
         return seeds;
     }
 
+    /// <summary>The owner's request <paramref name="messageId"/> and up to <paramref name="turns"/> earlier user/assistant turns of its channel (oldest first); null when the message is gone.</summary>
+    public static SeedMaterial? Material(SqliteConnection connection, long messageId, int turns)
+    {
+        ArgumentNullException.ThrowIfNull(connection);
+        using var cmd = connection.CreateCommand();
+        cmd.CommandText = """
+            SELECT r.Content,
+                   (SELECT json_group_array(Role || ': ' || Content) FROM (
+                       SELECT m.Role, m.Content FROM Messages m
+                        WHERE m.ChannelId = r.ChannelId AND m.Role IN ('user', 'assistant') AND m.Category IN (0, 3)
+                          AND (m.Timestamp < r.Timestamp OR (m.Timestamp = r.Timestamp AND m.Id < r.Id))
+                        ORDER BY m.Timestamp DESC, m.Id DESC LIMIT @turns))
+              FROM Messages r WHERE r.Id = @id;
+            """;
+        cmd.Parameters.AddWithValue("@id", messageId);
+        cmd.Parameters.AddWithValue("@turns", turns);
+        using var reader = cmd.ExecuteReader();
+        if (!reader.Read())
+        {
+            return null;
+        }
+
+        var context = JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [];
+        context.Reverse();
+        return new SeedMaterial([], reader.GetString(0)) { Context = context };
+    }
     private static bool IsJsonObject(string text)
     {
         try
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~MemoryLabelSourceTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 1`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Labelling/HistoryLabelSource.cs src/Cortex.Contained.Agent.Host/Evaluation/Labelling/MemoryLabelSource.cs src/Cortex.Contained.Agent.Host/Evaluation/Labelling/RecallRequestMiner.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/MemoryLabelSourceTests.cs
git commit -m "feat(eval): memory label source and shared real-request material" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 13: `history-eval label` and `memory-eval label` jobs

`LabelCli` copies the store (encrypted), runs the pipeline and deletes the copy; `EvaluationRegistration` routes `label` to it with the configured options, the agent's client and main model, and binds `Evaluation:Labelling` (L2). The two CLIs expose their agent embedders for reuse.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabelCli.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabelCliTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabelCliTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.History;
using Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

/// <summary><c>history-eval label</c> end to end on a synthetic store: fake LLM, fake embedder, encrypted workspace.</summary>
public sealed class LabelCliTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"label-cli-{Guid.NewGuid():N}");
    private readonly FakeLabellingLlm fake = new();
    private readonly StringWriter output = new();
    private readonly StorageKeyRing keys = EvalTestKeys.Ring();
    private readonly EvalProtection protection;

    public LabelCliTests() => this.protection = EvalProtection.Encrypted(this.keys);

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        this.output.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private LabelContext Context(LabellingOptions options) => new(
        options,
        this.fake,
        FakeLabellingLlm.ServesAll,
        () => "main-model",
        Path.Combine(this.root, "messages.db"),
        Path.Combine(this.root, "hybrid"),
        Path.Combine(this.root, "memory"),
        new MemoryMcpOptions { DataDirectory = Path.Combine(this.root, "live-memory"), Ollama = new OllamaOptions { Dimensions = FakeMemoryEmbedder.Dimensions } },
        _ => new FakeHistoryEmbedder(),
        _ => new FakeMemoryEmbedder(),
        TimeProvider.System);

    private static string Respond(string model, string system, string user) => model switch
    {
        "gpt-6.1-sol" => """{"text":"what happened with Q7x over that period","category":"lookup","goldFilters":{"since":"2000-01-01T00:00:00Z","until":"2100-01-01T00:00:00Z"}}""",
        "main-model" => """{"query":"Q7x"}""",
        _ => "{\"grades\":{" + string.Join(',', user.Split('\n').Where(l => l.StartsWith("[c", StringComparison.Ordinal))
            .Select(l => l[1..l.IndexOf(']', StringComparison.Ordinal)]).Select(a => $"\"{a}\":{(a == "c1" ? 2 : 0)}")) + "}}",
    };

    [Fact]
    public async Task RunAsync_Label_WritesAValidSealedDatasetAndLeavesNoCopy()
    {
        Directory.CreateDirectory(this.root);
        await using (var store = new MessageStore(Path.Combine(this.root, "messages.db"), NullLogger<MessageStore>.Instance, new MessageStoreEncryption(this.keys, TimeSpan.FromSeconds(5))))
        {
            for (var i = 0; i < 40; i++)
            {
                await store.SaveMessageAsync("u", i % 3 == 0 ? "ch" : "other", "user", $"message {i} about the garden plan and the boat repair schedule", T0.AddHours(i * 5));
            }
        }

        var workspace = new EvalWorkspace(Path.Combine(this.root, "hybrid"), this.protection);
        Directory.CreateDirectory(workspace.Directory);
        workspace.WritePlan(new AblationPlan { Configs = [new AblationConfig { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }, new AblationConfig { Name = "hybrid-rrf60" }] });
        this.fake.Respond = Respond;
        var run = new EvaluationRun(["label", "--queries", "6", "--kappa", "3"], this.output, this.protection, null);

        var code = await LabelCli.RunAsync("history-eval", run, this.Context(new LabellingOptions { MinCallIntervalMs = 0 }), CancellationToken.None);

        Assert.Equal(0, code);
        var dataset = workspace.ReadDataset();
        Assert.NotEmpty(dataset);
        Assert.Empty(EvalDataset.Validate(dataset));
        Assert.All(dataset, q => Assert.Equal(("gpt", "claude"), (q.QueryModelFamily, q.JudgeModelFamily)));
        Assert.True(EvalProtection.IsSealed(workspace.DatasetPath));
        Assert.Equal(3, KappaSampleFile.Read(workspace.Directory, this.protection).Count);
        Assert.Empty(Directory.EnumerateDirectories(workspace.RunsDirectory));
        Assert.DoesNotContain("garden", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Estimate_PrintsWithoutCalls()
    {
        var run = new EvaluationRun(["label", "--estimate"], this.output, EvalProtection.Plaintext, null);

        Assert.Equal(0, await LabelCli.RunAsync("memory-eval", run, this.Context(new LabellingOptions()), CancellationToken.None));

        Assert.Contains("memory-eval label: 270 queries", this.output.ToString(), StringComparison.Ordinal);
        Assert.Empty(this.fake.Requests);
    }

    [Fact]
    public async Task RunAsync_SameFamilyModels_Refused()
    {
        var run = new EvaluationRun(["label"], this.output, EvalProtection.Plaintext, null);

        Assert.Equal(2, await LabelCli.RunAsync("history-eval", run, this.Context(new LabellingOptions { QueryModel = "gpt-5", JudgeModel = "gpt-6.1-sol" }), CancellationToken.None));
        Assert.Contains("different families", this.output.ToString(), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `LabelCli` / `LabelContext` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs` — apply:

```diff
@@ -1,6 +1,8 @@
 using Cortex.Contained.Agent.Host.Evaluation.Frozen;
+using Cortex.Contained.Agent.Host.Evaluation.Labelling;
 using Cortex.Contained.Agent.Host.History;
 using Cortex.Contained.Agent.Host.History.Evaluation;
+using Cortex.Contained.Agent.Host.Llm;
 using Cortex.Contained.Agent.Host.Memory.Evaluation;
 using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Reranking.Evaluation;
@@ -14,11 +16,13 @@ namespace Cortex.Contained.Agent.Host.Evaluation;
 /// <summary>DI registration of the in-process evaluation jobs and workspace files (called from <c>Program.cs</c>).</summary>
 public static class EvaluationRegistration
 {
-    /// <summary>Registers <see cref="EvaluationJobService"/> and <see cref="EvaluationFiles"/>.</summary>
-    public static void AddEvaluation(IServiceCollection services, StorageEncryptionContext encryption)
+    /// <summary>Registers <see cref="EvaluationJobService"/>, <see cref="EvaluationFiles"/> and the labelling options.</summary>
+    public static void AddEvaluation(IServiceCollection services, StorageEncryptionContext encryption, IConfiguration configuration)
     {
         ArgumentNullException.ThrowIfNull(services);
         ArgumentNullException.ThrowIfNull(encryption);
+        ArgumentNullException.ThrowIfNull(configuration);
+        services.Configure<LabellingOptions>(configuration.GetSection(LabellingOptions.SectionName));
         services.AddSingleton(new FrozenCorpusStore(encryption.Layout.EvaluationDirectory));
         services.AddSingleton(sp =>
         {
@@ -28,9 +32,12 @@ public static class EvaluationRegistration
             Func<RerankerOptions, IReranker> reranker = options => RerankEvalSetup.LiveReranker(options, () => access.ApiKey);
             var commands = new Dictionary<string, EvaluationCommand>(StringComparer.Ordinal)
             {
-                ["history-eval"] = (run, ct) => HistoryEvalCli.RunInAgentAsync(
-                    run.Args, run.Output, run.Protection, history.CurrentValue, memory.CurrentValue, encryption.Layout.MessagesDatabase, reranker, ct),
-                ["memory-eval"] = (run, ct) => MemoryEvalCli.RunInAgentAsync(run.Args, run.Output, run.Protection, memory.CurrentValue, reranker, ct),
+                ["history-eval"] = (run, ct) => IsLabel(run)
+                    ? LabelCli.RunAsync("history-eval", run, Labelling(sp, encryption), ct)
+                    : HistoryEvalCli.RunInAgentAsync(run.Args, run.Output, run.Protection, history.CurrentValue, memory.CurrentValue, encryption.Layout.MessagesDatabase, reranker, ct),
+                ["memory-eval"] = (run, ct) => IsLabel(run)
+                    ? LabelCli.RunAsync("memory-eval", run, Labelling(sp, encryption), ct)
+                    : MemoryEvalCli.RunInAgentAsync(run.Args, run.Output, run.Protection, memory.CurrentValue, reranker, ct),
                 ["eval-corpus"] = (run, ct) => EvalCorpusCli.RunAsync(
                     run,
                     new EvalCorpusContext(
@@ -55,6 +62,27 @@ public static class EvaluationRegistration
             EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>())));
     }
 
+    /// <summary>What <c>label</c> runs with: the configured models through the agent's provider, the main model, the live stores.</summary>
+    public static LabelContext Labelling(IServiceProvider services, StorageEncryptionContext encryption)
+    {
+        ArgumentNullException.ThrowIfNull(services);
+        ArgumentNullException.ThrowIfNull(encryption);
+        var memory = services.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>().CurrentValue;
+        var direct = services.GetRequiredService<DirectLlmClient>();
+        return new LabelContext(
+            services.GetRequiredService<IOptionsMonitor<LabellingOptions>>().CurrentValue,
+            services.GetRequiredService<ILlmClient>(),
+            direct.ProviderFor,
+            () => services.GetService<Cortex.Contained.Agent.Host.Agent.IModelProvider>()?.DefaultModel,
+            encryption.Layout.MessagesDatabase,
+            services.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue.EvalArtifactsDirectory,
+            MemoryEvalWorkspace.DefaultDirectory,
+            memory,
+            HistoryEvalCli.AgentEmbedder(memory),
+            MemoryEvalCli.AgentEmbedder,
+            services.GetRequiredService<TimeProvider>());
+    }
+
     /// <summary>Workspace directory per tool.</summary>
     public static IReadOnlyDictionary<string, string> Workspaces(HistoryOptions history, StorageLayout layout)
     {
@@ -67,4 +95,6 @@ public static class EvaluationRegistration
             ["eval-corpus"] = Path.Combine(layout.EvaluationDirectory, "corpus"),
         };
     }
+
+    private static bool IsLabel(EvaluationRun run) => run.Args.Count > 0 && string.Equals(run.Args[0], "label", StringComparison.Ordinal);
 }
```

`src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabelCli.cs` (new):

```csharp
using System.Globalization;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>What the <c>label</c> command works with (supplied by <see cref="EvaluationRegistration"/>).</summary>
public sealed record LabelContext(
    LabellingOptions Options,
    ILlmClient Llm,
    Func<string, string?> ProviderFor,
    Func<string?> MainModel,
    string MessagesDb,
    string HistoryWorkspace,
    string MemoryWorkspace,
    MemoryMcpOptions Memory,
    Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> HistoryEmbedder,
    Func<MemoryMcpOptions, IEmbeddingService> MemoryEmbedder,
    TimeProvider Time);

/// <summary>
/// <c>history-eval label</c> and <c>memory-eval label</c> (evaluation jobs): <c>[--queries N] [--kappa N] [--estimate]</c>.
/// Copies the store into <c>runs/label-*</c> (encrypted with the evaluation key), labels it with
/// <see cref="Labeller"/> and deletes the copy. The workspace must be initialised (its <c>ablation.json</c> lists
/// the pooled configurations).
/// </summary>
public static class LabelCli
{
    public const string Usage = "usage: <history-eval|memory-eval> label [--queries N] [--kappa N] [--estimate]";

    /// <summary>Runs the command for <paramref name="tool"/>; returns the exit code.</summary>
    public static async Task<int> RunAsync(string tool, EvaluationRun run, LabelContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(context);
        var output = run.Output;
        var options = context.Options;
        if (options.Validate() is { Count: > 0 } errors)
        {
            foreach (var error in errors)
            {
                await output.WriteLineAsync("error: " + error).ConfigureAwait(false);
            }

            return 2;
        }

        var memory = tool == "memory-eval";
        var queries = IntOption(run.Args, "--queries") ?? (memory ? options.MemoryQueries : options.HistoryQueries);
        var kappa = IntOption(run.Args, "--kappa") ?? options.KappaSample;
        if (run.Args.Contains("--estimate", StringComparer.Ordinal))
        {
            var estimate = Labeller.Estimate(queries, options);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{tool} label: {queries} queries, ~{estimate.Total} LLM calls (writer {estimate.WriterCalls}, main model {estimate.MainCalls}, judge {estimate.JudgeCalls}), ~{estimate.Hours} h; cap {options.MaxCalls}.")).ConfigureAwait(false);
            return 0;
        }

        if (context.MainModel() is not { Length: > 0 } mainModel)
        {
            await output.WriteLineAsync("error: the agent has no main model yet (credentials have not arrived from the Bridge).").ConfigureAwait(false);
            return 2;
        }

        var directory = memory ? context.MemoryWorkspace : context.HistoryWorkspace;
        var calls = new LabellingStateStore(directory, run.Protection).Load()?.Calls ?? 0;
        var llm = new LabellingLlm(context.Llm, context.ProviderFor, options, calls, time: context.Time);
        var runDirectory = Path.Combine(directory, "runs", string.Create(CultureInfo.InvariantCulture, $"label-{context.Time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}"));
        try
        {
            return memory
                ? await RunMemoryAsync(run, context, llm, mainModel, runDirectory, queries, kappa, cancellationToken).ConfigureAwait(false)
                : await RunHistoryAsync(run, context, llm, mainModel, runDirectory, queries, kappa, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
            return 2;
        }
        finally
        {
            // The copy holds raw text: it never outlives the job.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(runDirectory))
            {
                Directory.Delete(runDirectory, recursive: true);
            }
        }
    }

    private static async Task<int> RunHistoryAsync(
        EvaluationRun run, LabelContext context, LabellingLlm llm, string mainModel, string runDirectory, int queries, int kappa, CancellationToken cancellationToken)
    {
        var workspace = new EvalWorkspace(context.HistoryWorkspace, run.Protection);
        var configs = workspace.ReadPlan().Configs.Where(c => c.RerankBudget() is null).ToList();
        await using var corpus = await EvalCorpus.CreateAsync(
            context.MessagesDb, runDirectory, new HistoryOptions { Enabled = true }, configs, context.HistoryEmbedder, cancellationToken, protection: run.Protection).ConfigureAwait(false);
        var source = new HistoryLabelSource(corpus, workspace, configs);
        return await new Labeller(source, llm, context.Options, mainModel, workspace.Directory, run.Protection, run.Output, context.Time)
            .RunAsync(queries, kappa, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunMemoryAsync(
        EvaluationRun run, LabelContext context, LabellingLlm llm, string mainModel, string runDirectory, int queries, int kappa, CancellationToken cancellationToken)
    {
        var workspace = new MemoryEvalWorkspace(context.MemoryWorkspace, run.Protection);
        var configs = workspace.ReadPlan().Configs;
        await using var corpus = await MemoryEvalCorpus.CreateAsync(
            context.Memory.DataDirectory, runDirectory, context.Memory, context.MemoryEmbedder(context.Memory), cancellationToken, protection: run.Protection).ConfigureAwait(false);
        var source = new MemoryLabelSource(corpus, workspace, configs, () =>
        {
            if (!File.Exists(context.MessagesDb))
            {
                return null;
            }

            var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(
                context.MessagesDb, run.Protection.SourcePassword(MessageStore.PasswordPurpose), SqliteOpenMode.ReadOnly, pooling: false));
            connection.Open();
            return connection;
        });
        return await new Labeller(source, llm, context.Options, mainModel, workspace.Directory, run.Protection, run.Output, context.Time)
            .RunAsync(queries, kappa, cancellationToken).ConfigureAwait(false);
    }

    private static int? IntOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal) && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }
}
```

`src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs` — apply:

```diff
@@ -81,20 +81,17 @@ public static class HistoryEvalCli
     {
         ArgumentNullException.ThrowIfNull(args);
         ArgumentNullException.ThrowIfNull(memory);
-        return RunAsync(
-            [.. args, "--source", messagesDb],
-            output,
-            history =>
-            {
-                var memoryMonitor = new StaticOptionsMonitor<MemoryMcpOptions>(memory);
-                return new OllamaHistoryEmbedder(new OllamaEmbeddingService(memoryMonitor, NullLogger<OllamaEmbeddingService>.Instance), memoryMonitor, history);
-            },
-            cancellationToken,
-            configured,
-            rerankerFactory,
-            protection);
+        return RunAsync([.. args, "--source", messagesDb], output, AgentEmbedder(memory), cancellationToken, configured, rerankerFactory, protection);
     }
 
+    /// <summary>The agent's configured embedder for history evaluation runs (jobs and automated labelling).</summary>
+    public static Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> AgentEmbedder(MemoryMcpOptions memory) =>
+        history =>
+        {
+            var memoryMonitor = new StaticOptionsMonitor<MemoryMcpOptions>(memory);
+            return new OllamaHistoryEmbedder(new OllamaEmbeddingService(memoryMonitor, NullLogger<OllamaEmbeddingService>.Instance), memoryMonitor, history);
+        };
+
     /// <summary>Entry point with an injected embedder (tests use a deterministic fake).</summary>
     internal static async Task<int> RunAsync(
         IReadOnlyList<string> args,
```

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs` — apply:

```diff
@@ -236,11 +236,15 @@ public static class MemoryEvalCli
             args,
             output,
             live,
-            options => new OllamaEmbeddingService(new StaticOptionsMonitor(options), NullLogger<OllamaEmbeddingService>.Instance),
+            AgentEmbedder,
             cancellationToken,
             rerankerFactory,
             protection);
 
+    /// <summary>The agent's configured embedder for memory evaluation runs (jobs and automated labelling).</summary>
+    public static IEmbeddingService AgentEmbedder(MemoryMcpOptions options) =>
+        new OllamaEmbeddingService(new StaticOptionsMonitor(options), NullLogger<OllamaEmbeddingService>.Instance);
+
     /// <summary>
     /// The agent's <c>MemoryMcp</c> configuration, loaded like <c>Program.cs</c> (appsettings, environment,
     /// then <c>cortex.yml</c>), with the CLI overrides.
```

`src/Cortex.Contained.Agent.Host/Program.cs` — apply:

```diff
@@ -297,7 +297,7 @@ builder.Services.AddSingleton(sp => new StorageEncryptionService(
     selfRestart: string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)));
 
 // --- Evaluation jobs (history-eval, memory-eval) inside the agent process, where the storage key lives ---
-Cortex.Contained.Agent.Host.Evaluation.EvaluationRegistration.AddEvaluation(builder.Services, storageEncryption);
+Cortex.Contained.Agent.Host.Evaluation.EvaluationRegistration.AddEvaluation(builder.Services, storageEncryption, builder.Configuration);
 
 // --- Memory Settings Store (runtime-mutable overrides for memory options) ---
 builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Memory.MemorySettingsStore>();
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~LabelCliTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 3`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs src/Cortex.Contained.Agent.Host/Evaluation/Labelling/LabelCli.cs src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs src/Cortex.Contained.Agent.Host/Program.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Labelling/LabelCliTests.cs
git commit -m "feat(eval): history-eval label and memory-eval label jobs" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 14: Blind hand grading

`GradingService` serves the κ sample one item at a time without the judge's grade, stores the owner's grades in the sealed sample files and, once complete, writes `judge-agreement.csv` and computes κ over all items (the gate) and per tool (L10).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/GradingService.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/GradingServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/GradingServiceTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Orchestration;

public sealed class GradingServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"grading-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
    private readonly Dictionary<string, string> workspaces;
    private readonly GradingService service;

    public GradingServiceTests()
    {
        this.workspaces = new(StringComparer.Ordinal) { ["history-eval"] = Path.Combine(this.root, "hybrid"), ["memory-eval"] = Path.Combine(this.root, "memory") };
        foreach (var directory in this.workspaces.Values)
        {
            Directory.CreateDirectory(directory);
        }

        KappaSampleFile.Write(this.workspaces["history-eval"], this.protection,
        [
            Item("history-eval", "h0001", "101", 2, "zqtext ferry"),
            Item("history-eval", "h0002", "102", 0, "zqtext garden"),
        ]);
        KappaSampleFile.Write(this.workspaces["memory-eval"], this.protection, [Item("memory-eval", "m0001", "mem-a", 1, "zqtext note")]);
        this.service = new GradingService(this.workspaces, () => this.protection);
    }

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private static KappaItem Item(string tool, string query, string candidate, int grade, string text) =>
        new() { Tool = tool, QueryId = query, CandidateId = candidate, LlmGrade = grade, Query = "q " + query, Candidate = text };

    [Fact]
    public void Item_HistoryFirstThenMemoryWithoutTheJudgesGrade()
    {
        var first = this.service.Item(0)!;
        var last = this.service.Item(2)!;

        Assert.Equal((0, 3, "history-eval", "zqtext ferry"), (first.Index, first.Total, first.Tool, first.Candidate));
        Assert.Equal(("memory-eval", "q m0001"), (last.Tool, last.Query));
        Assert.Null(this.service.Item(3));
        Assert.DoesNotContain("LlmGrade", System.Text.Json.JsonSerializer.Serialize(first), StringComparison.Ordinal);
    }

    [Fact]
    public void Grade_AllItems_StoresSealedWritesAgreementFilesAndComputesKappa()
    {
        this.service.Grade(0, 2);
        var partial = this.service.Grade(1, 1);
        Assert.Equal((3, 2, false), (partial.Total, partial.Graded, partial.Complete));
        Assert.Null(partial.Kappa);
        Assert.False(File.Exists(Path.Combine(this.workspaces["history-eval"], "judge-agreement.csv")));

        var done = this.service.Grade(2, 1);

        Assert.True(done.Complete);
        Assert.NotNull(done.Kappa);
        Assert.NotNull(done.HistoryKappa);
        Assert.Null(done.MemoryKappa);
        Assert.Equal(["101,2,2", "102,0,1"], File.ReadAllLines(Path.Combine(this.workspaces["history-eval"], "judge-agreement.csv")));
        Assert.Equal(["mem-a,1,1"], File.ReadAllLines(Path.Combine(this.workspaces["memory-eval"], "judge-agreement.csv")));
        Assert.True(EvalProtection.IsSealed(Path.Combine(this.workspaces["history-eval"], KappaSampleFile.FileName)));
        Assert.Equal(2, new GradingService(this.workspaces, () => this.protection).Item(0)!.HumanGrade);
        Assert.Equal(done, this.service.Progress());
    }

    [Fact]
    public void Grade_ClearAndOutOfRange()
    {
        this.service.Grade(0, 2);
        Assert.Equal(0, this.service.Grade(0, null).Graded);
        Assert.Throws<ArgumentOutOfRangeException>(() => this.service.Grade(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => this.service.Grade(9, 1));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `GradingService` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/GradingService.cs` (new):

```csharp
using System.Globalization;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>One item of the owner's κ sample as the grading screen shows it (decrypted on request; never logged).</summary>
public sealed record GradingItem(int Index, int Total, string Tool, string Query, string Candidate, int? HumanGrade);

/// <summary>Progress of the hand grading and, once complete, the LLM-vs-human agreement.</summary>
/// <param name="Total">Items in the sample.</param>
/// <param name="Graded">Items the owner graded.</param>
/// <param name="Kappa">Cohen's κ over every item (the gate), or null until complete.</param>
/// <param name="HistoryKappa">κ over the history items (reported), or null.</param>
/// <param name="MemoryKappa">κ over the memory items (reported), or null.</param>
public sealed record GradingProgress(int Total, int Graded, double? Kappa, double? HistoryKappa, double? MemoryKappa)
{
    public bool Complete => this.Total > 0 && this.Graded == this.Total;
}

/// <summary>
/// The owner's blind hand grading of the κ sample (§4.2: LLM-vs-human κ ≥ 0.6): items of the history workspace
/// first, then memory, one at a time, without the judge's grade. Grades are stored in the sealed sample files; when
/// every item is graded, each workspace gets its <c>judge-agreement.csv</c> (ids and grades only, the input of the
/// <c>kappa</c> command) and κ is computed over all items (the gate) and per tool (reported).
/// </summary>
public sealed class GradingService
{
    /// <summary>The tools whose samples are graded, in order.</summary>
    public static readonly IReadOnlyList<string> Tools = ["history-eval", "memory-eval"];

    private readonly IReadOnlyDictionary<string, string> workspaces;
    private readonly Func<EvalProtection> protection;
    private readonly Lock sync = new();

    /// <param name="workspaces">Workspace directory per tool.</param>
    /// <param name="protection">The protection (throws while the storage key is missing).</param>
    public GradingService(IReadOnlyDictionary<string, string> workspaces, Func<EvalProtection> protection)
    {
        this.workspaces = workspaces;
        this.protection = protection;
    }

    /// <summary>Item <paramref name="index"/> (0-based), or null outside the sample.</summary>
    public GradingItem? Item(int index)
    {
        lock (this.sync)
        {
            var items = this.Load();
            if (index < 0 || index >= items.Count)
            {
                return null;
            }

            var item = items[index].Item;
            return new GradingItem(index, items.Count, item.Tool, item.Query, item.Candidate, item.HumanGrade);
        }
    }

    /// <summary>Stores (or clears, with null) the owner's grade of item <paramref name="index"/>.</summary>
    public GradingProgress Grade(int index, int? grade)
    {
        if (grade is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(grade), "Grades are 0, 1 or 2.");
        }

        lock (this.sync)
        {
            var items = this.Load();
            if (index < 0 || index >= items.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "No such item.");
            }

            var tool = items[index].Item.Tool;
            var protection = this.protection();
            var updated = items
                .Where(i => i.Item.Tool == tool)
                .Select(i => i.Index == index ? i.Item with { HumanGrade = grade } : i.Item)
                .ToList();
            KappaSampleFile.Write(this.workspaces[tool], protection, updated);
            return this.Summarise(items.Select(i => i.Index == index ? i.Item with { HumanGrade = grade } : i.Item).ToList(), writeAgreement: true);
        }
    }

    /// <summary>Current progress (κ once complete).</summary>
    public GradingProgress Progress()
    {
        lock (this.sync)
        {
            return this.Summarise(this.Load().Select(i => i.Item).ToList(), writeAgreement: false);
        }
    }

    private List<(int Index, KappaItem Item)> Load()
    {
        var protection = this.protection();
        return Tools
            .Where(this.workspaces.ContainsKey)
            .SelectMany(tool => KappaSampleFile.Read(this.workspaces[tool], protection))
            .Select((item, i) => (i, item))
            .ToList();
    }

    private GradingProgress Summarise(List<KappaItem> items, bool writeAgreement)
    {
        var graded = items.Count(i => i.HumanGrade is not null);
        if (items.Count == 0 || graded < items.Count)
        {
            return new GradingProgress(items.Count, graded, null, null, null);
        }

        if (writeAgreement)
        {
            foreach (var group in items.GroupBy(i => i.Tool, StringComparer.Ordinal))
            {
                File.WriteAllLines(
                    Path.Combine(this.workspaces[group.Key], "judge-agreement.csv"),
                    group.Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.CandidateId},{i.LlmGrade},{i.HumanGrade}")));
            }
        }

        return new GradingProgress(items.Count, graded, Kappa(items), Kappa(items.Where(i => i.Tool == Tools[0]).ToList()), Kappa(items.Where(i => i.Tool == Tools[1]).ToList()));
    }

    private static double? Kappa(List<KappaItem> items) =>
        items.Count < 2 ? null : EvalStatistics.CohensKappa(items.Select(i => i.LlmGrade).ToList(), items.Select(i => i.HumanGrade!.Value).ToList());
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~GradingServiceTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 3`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/GradingService.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/GradingServiceTests.cs
git commit -m "feat(eval): blind hand grading of the kappa sample" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 15: Analysis plan and selection rules

The pre-registered plan is written by the run with counts, models, κ and the selection rule; `ConfigSelector` and `RerankCellSelector` implement the rule mechanically on dev results (L13).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/AnalysisPlanWriter.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/AnalysisPlanWriterTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/AnalysisPlanWriterTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Orchestration;

public sealed class AnalysisPlanWriterTests
{
    private static readonly CalibrationScore NoCalibration = new(0, 0);

    private static ConfigSummary Summary(string config, double judged) => new(config, 50, 0.5, 0.5, 0.5, judged, null, null, NoCalibration, 100);

    private static PairedComparison Compare(string config, double low, double mean, string slice = "all") => new(config, "keyword-plain", slice, 50, mean, low, mean + 0.1, 0.01);

    [Fact]
    public void Select_HighestLowerBoundAmongEligibleCandidates()
    {
        var report = new AblationReport(
            [],
            [Summary("keyword-plain", 1), Summary("hybrid-rrf60", 0.95), Summary("hybrid-rrf20", 0.95), Summary("hybrid-model-queries", 1), Summary("hybrid-poorly-judged", 0.5)],
            [
                Compare("hybrid-rrf60", 0.01, 0.05),
                Compare("hybrid-rrf20", 0.01, 0.06),
                Compare("hybrid-model-queries", 0.2, 0.3),
                Compare("hybrid-poorly-judged", 0.3, 0.4),
                Compare("hybrid-rrf60", 0.5, 0.6, "stratum:real"),
            ]);

        var chosen = ConfigSelector.Select(report, "keyword-plain", ["hybrid-rrf60", "hybrid-rrf20", "hybrid-poorly-judged"]);

        Assert.Equal("hybrid-rrf20", chosen);
        Assert.Null(ConfigSelector.Select(report, "keyword-plain", ["hybrid-model-queries-not-listed"]));
    }

    [Fact]
    public void SelectCell_FastestQualifyingBestLowerBound()
    {
        RerankCurveRow[] rows =
        [
            new("normal", "top10-chars600", 50, 0.02, 0.01, 0.03, 0.01, 80, 300, 900, 50, 0, 0),
            new("normal", "top30-chars2000", 50, 0.06, 0.04, 0.08, 0.01, 900, 2900, 3100, 50, 0, 0),
            new("normal", "top15-chars1200", 50, 0.04, 0.02, 0.06, 0.01, 120, 600, 1300, 50, 0, 0),
        ];

        Assert.Equal(RerankGrid.Cells().First(c => c.Label == "top15-chars1200"), RerankCellSelector.Select(rows));
        Assert.Null(RerankCellSelector.Select([rows[0] with { Low = -0.01 }]));
    }

    [Fact]
    public void Render_StatesCountsModelsKappaAndTheSelectionRule()
    {
        var plan = AnalysisPlanWriter.Render(new PlanFacts
        {
            Tool = "history-eval",
            Date = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero),
            Queries = 480,
            Dev = 150,
            Categories = new Dictionary<string, int> { ["lookup"] = 200, ["temporal"] = 80 },
            Strata = new Dictionary<string, int> { ["real"] = 120, ["synthetic"] = 360 },
            QueryModel = "gpt-6.1-sol",
            JudgeModel = "claude-opus-5.5",
            PoolDepth = 10,
            Kappa = 0.71,
            ToolKappa = 0.68,
            KappaItems = 75,
            Baseline = "keyword-plain",
            Margin = 0.02,
            Reranker = false,
        });

        Assert.Contains("480 queries, 150 dev / 330 test", plan, StringComparison.Ordinal);
        Assert.Contains("lookup 200, temporal 80", plan, StringComparison.Ordinal);
        Assert.Contains("κ = 0.710 on 75", plan, StringComparison.Ordinal);
        Assert.Contains("(this dataset: 0.680)", plan, StringComparison.Ordinal);
        Assert.Contains("highest lower bound of the paired 95 % CI", plan, StringComparison.Ordinal);
        Assert.Contains("margin 0.02", plan, StringComparison.Ordinal);
        Assert.Contains("not part of this run", plan, StringComparison.Ordinal);
        Assert.Contains("pre-registered", plan, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `AnalysisPlanWriter` / `PlanFacts` / `ConfigSelector` / `RerankCellSelector` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/AnalysisPlanWriter.cs` (new):

```csharp
using System.Globalization;
using System.Text;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>The facts an automated analysis plan states (counts, models, κ; never text).</summary>
public sealed record PlanFacts
{
    public required string Tool { get; init; }

    public required DateTimeOffset Date { get; init; }

    public required int Queries { get; init; }

    public required int Dev { get; init; }

    public required IReadOnlyDictionary<string, int> Categories { get; init; }

    public required IReadOnlyDictionary<string, int> Strata { get; init; }

    public required string QueryModel { get; init; }

    public required string JudgeModel { get; init; }

    public required int PoolDepth { get; init; }

    public required double Kappa { get; init; }

    public double? ToolKappa { get; init; }

    public required int KappaItems { get; init; }

    public required string Baseline { get; init; }

    public required double Margin { get; init; }

    public required bool Reranker { get; init; }
}

/// <summary>
/// The pre-registered analysis plan of an automated run (§4.2), written before the first dev ablation and
/// registered with the dataset and <c>ablation.json</c> before the held-out run. It fixes the configuration selection
/// rule (<see cref="ConfigSelector"/>, <see cref="RerankCellSelector"/>) so the choice made on the dev split is
/// mechanical.
/// </summary>
public static class AnalysisPlanWriter
{
    /// <summary>The plan's Markdown.</summary>
    public static string Render(PlanFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var memory = facts.Tool == "memory-eval";
        var unit = memory ? "memory" : "message";
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {(memory ? "Hybrid memory search" : "Hybrid history search")} — pre-registered analysis plan (automated run)");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Written by the evaluation run on {facts.Date:yyyy-MM-dd} before the first dev ablation. `register` fixes this file, `dataset.jsonl` and `ablation.json` before the held-out run.");
        text.AppendLine();
        text.AppendLine("## Data");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Snapshot: {facts.Date:yyyy-MM-dd}. A result on a mostly pre-event-log snapshot is provisional (re-run after ≥ 4 weeks of live capture).");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Dataset: {facts.Queries} queries, {facts.Dev} dev / {facts.Queries - facts.Dev} test (`EvalSplits.Assign`, seed 20261121, 30 % dev). Categories: {Counts(facts.Categories)}. Strata: {Counts(facts.Strata)} (`real` = real recall requests, co-primary).");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Queries written by `{facts.QueryModel}` from sampled snippets; graded 0–2 per {unit} by `{facts.JudgeModel}` on the union of the top {facts.PoolDepth} of every configuration in `ablation.json` plus the source (the model families differ).");
        text.AppendLine(CultureInfo.InvariantCulture, $"- LLM-vs-human κ = {facts.Kappa:F3} on {facts.KappaItems} blind hand-graded items of both datasets{(facts.ToolKappa is { } k ? string.Create(CultureInfo.InvariantCulture, $" (this dataset: {k:F3})") : string.Empty)}; LLM labels are used only with κ ≥ 0.6.");
        text.AppendLine("- Paraphrase guard (Jaccard ≤ 0.2, no shared rare token): enforced while drafting and again against the judged gold.");
        text.AppendLine("- Finalisation (before any comparison): a synthetic query whose source was judged irrelevant is dropped; a real request with no relevant item becomes an abstention query; an abstention query with a relevant item, an enumeration/multi query with fewer than two relevant items, a paraphrase failing the guard and tag_filtered without tags become `lookup`.");
        text.AppendLine();
        text.AppendLine("## Hypotheses and metrics");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Primary: nDCG@10 of `holdout.config` vs `{facts.Baseline}` on the test split; non-inferiority margin {facts.Margin.ToString(CultureInfo.InvariantCulture)}.");
        text.AppendLine("- Co-primary: the same on the `stratum:real` slice.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Secondary: recall@10, MRR, words to the first relevant {unit}, judged@10, {(memory ? "multi" : "enumeration")} set F1, abstention Brier/ECE, p95 latency; oracle vs model-written queries (`*-model-queries`).");
        text.AppendLine("- Raw data and the sanitised frozen copy: both reported with their difference; the gate uses the raw run.");
        text.AppendLine();
        text.AppendLine("## Configuration selection (dev split only)");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Candidates: every configuration in `ablation.json` except `{facts.Baseline}`, the `*-model-queries` configurations and reranked ones, with dev judged@10 ≥ {ConfigSelector.MinJudged.ToString(CultureInfo.InvariantCulture)}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Chosen: the highest lower bound of the paired 95 % CI of Δ nDCG@10 vs `{facts.Baseline}` on dev; ties by the higher mean, then by name. It becomes `holdout.config` with margin {facts.Margin.ToString(CultureInfo.InvariantCulture)}.");
        text.AppendLine(facts.Reranker
            ? "- Gate R: the rerank curve on dev (normal load, the chosen configuration as base); the cell with the highest CI lower bound among cells with p95 ≤ 2.5 s and a lower bound > 0 is added as `<config>-rerank-<cell>` and tested by `gate-r` on test; no such cell, no Gate R."
            : "- Gate R: not part of this run (the reranker was off).");
        text.AppendLine();
        text.AppendLine("## Statistics");
        text.AppendLine("- Paired bootstrap 95 % CI (10,000 resamples, seed 20261121) and a paired randomisation test.");
        text.AppendLine("- Per-category results as CIs only; dev ablations are exploratory; only the single pre-registered holdout comparison is confirmatory.");
        text.AppendLine();
        text.AppendLine("## Decision rule (owner)");
        text.AppendLine(memory
            ? "- Gate H for memory passes when the primary comparison is non-inferior and no category's CI shows a loss the owner will not accept; only then switch `MemoryMcp:Search:DefaultMode`."
            : "- Gate H passes when the primary comparison is non-inferior and the behaviour gate thresholds hold.");
        return text.ToString();
    }

    private static string Counts(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0 ? "none" : string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Key} {c.Value}")));
}

/// <summary>The pre-declared choice of <c>holdout.config</c> from the dev ablation (see the analysis plan).</summary>
public static class ConfigSelector
{
    /// <summary>Configurations with less dev judged@10 are not chosen (the pool would not cover them).</summary>
    public const double MinJudged = 0.8;

    /// <summary>The chosen configuration among <paramref name="candidates"/>, or null when none qualifies.</summary>
    public static string? Select(AblationReport dev, string baseline, IReadOnlyCollection<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(dev);
        ArgumentNullException.ThrowIfNull(candidates);
        var judged = dev.Summaries.ToDictionary(s => s.Config, s => s.Judged10, StringComparer.Ordinal);
        return dev.Comparisons
            .Where(c => c.Slice == "all" && c.Baseline == baseline && c.Config != baseline && candidates.Contains(c.Config)
                && judged.GetValueOrDefault(c.Config) >= MinJudged)
            .OrderByDescending(c => c.Low)
            .ThenByDescending(c => c.Mean)
            .ThenBy(c => c.Config, StringComparer.Ordinal)
            .Select(c => c.Config)
            .FirstOrDefault();
    }
}

/// <summary>The pre-declared choice of the reranked configuration for Gate R from the dev rerank curve.</summary>
public static class RerankCellSelector
{
    /// <summary>The chosen budget, or null when no cell is fast enough with a CI lower bound above zero.</summary>
    public static RerankBudget? Select(IReadOnlyList<RerankCurveRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var cell = rows
            .Where(r => r.P95EndToEndMs <= RerankEvaluation.MaxP95Ms && r.Low > 0)
            .OrderByDescending(r => r.Low)
            .ThenByDescending(r => r.DeltaNdcg)
            .ThenBy(r => r.Cell, StringComparer.Ordinal)
            .FirstOrDefault();
        return cell is null ? null : RerankGrid.Cells().First(c => c.Label == cell.Cell);
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~AnalysisPlanWriterTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 3`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/AnalysisPlanWriter.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/AnalysisPlanWriterTests.cs
git commit -m "feat(eval): automated analysis plan with the pre-declared selection rules" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 16: Run phases and persisted state

The fixed order of the run, which jobs each phase runs, the waits, Gate R skipping, and parsing of report paths, the frozen version and leakage findings — pure, so it is tested without jobs (L12).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/EvaluationRunPlan.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/EvaluationRunPlanTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/EvaluationRunPlanTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Orchestration;

public sealed class EvaluationRunPlanTests
{
    private static EvaluationRunState At(string phase) => new() { Phase = phase, HistoryKappaItems = 50, MemoryKappaItems = 25, Version = "v3" };

    [Fact]
    public void Jobs_PhasesRunTheirCommandsForBothTools()
    {
        Assert.Equal(["history-eval label --kappa 50"], EvaluationRunPlan.Jobs(At(EvaluationRunPlan.LabelHistory)).Select(j => j.Label));
        Assert.Equal(["history-eval ablate --split dev", "memory-eval ablate --split dev"], EvaluationRunPlan.Jobs(At(EvaluationRunPlan.AblateDev)).Select(j => j.Label));
        Assert.Equal(
            ["history-eval holdout --corpus frozen:v3", "memory-eval holdout --corpus frozen:v3"],
            EvaluationRunPlan.Jobs(At(EvaluationRunPlan.HoldoutFrozen)).Select(j => j.Label));
        Assert.Equal(
            ["eval-corpus compare --version v3", "eval-corpus compare --version v3 --tool memory-eval"],
            EvaluationRunPlan.Jobs(At(EvaluationRunPlan.Compare)).Select(j => j.Label));
        Assert.Empty(EvaluationRunPlan.Jobs(At(EvaluationRunPlan.Grading)));
        Assert.Equal(RunWait.Passphrase, EvaluationRunPlan.WaitOf(EvaluationRunPlan.Passphrase));
        Assert.True(EvaluationRunPlan.IsInternal(EvaluationRunPlan.Select));
    }

    [Fact]
    public void Next_WithoutReranker_SkipsTheGateRPhases()
    {
        var state = At(EvaluationRunPlan.Select);
        state.Selected["history-eval"] = "hybrid-rrf20";

        Assert.Equal(EvaluationRunPlan.Register, EvaluationRunPlan.Next(state));
        state.Phase = EvaluationRunPlan.HoldoutRaw;
        Assert.Equal(EvaluationRunPlan.Suggest, EvaluationRunPlan.Next(state));
        state.Phase = EvaluationRunPlan.Summary;
        Assert.Equal(EvaluationRunPlan.Done, EvaluationRunPlan.Next(state));
    }

    [Fact]
    public void Next_WithReranker_RunsTheCurveAndGateR()
    {
        var state = At(EvaluationRunPlan.Select);
        state.Reranker = true;
        state.Selected["history-eval"] = "hybrid-rrf20";

        Assert.Equal(EvaluationRunPlan.RerankCurve, EvaluationRunPlan.Next(state));
        state.Phase = EvaluationRunPlan.RerankCurve;
        Assert.Equal(["history-eval rerank-curve --condition normal --base hybrid-rrf20"], EvaluationRunPlan.Jobs(state).Select(j => j.Label));
        state.RerankConfigs["history-eval"] = "hybrid-rrf20-rerank-top15-chars1200";
        state.Phase = EvaluationRunPlan.HoldoutRaw;
        Assert.Equal(EvaluationRunPlan.GateR, EvaluationRunPlan.Next(state));
        state.Phase = EvaluationRunPlan.GateR;
        Assert.Equal(["history-eval gate-r --config hybrid-rrf20-rerank-top15-chars1200"], EvaluationRunPlan.Jobs(state).Select(j => j.Label));
    }

    [Fact]
    public void Record_ReportPathsVersionAndLeaks()
    {
        var state = At(EvaluationRunPlan.AblateDev);

        EvaluationRunPlan.Record(state, new RunJob("history-eval", ["ablate", "--split", "dev"]), "x\nReport: /app/state/eval/hybrid/runs/ablate-dev-1/report.md\n");
        EvaluationRunPlan.Record(state, new RunJob("eval-corpus", ["compare", "--version", "v3", "--tool", "memory-eval"]), "table\nReport: /app/state/eval/compare/m.md\n");
        EvaluationRunPlan.Record(state, new RunJob("eval-corpus", ["freeze"]), "Frozen v4: 10 messages");

        Assert.Equal("/app/state/eval/hybrid/runs/ablate-dev-1/report.md", state.Reports["history-eval:ablate"]);
        Assert.Equal("/app/state/eval/compare/m.md", state.Reports["memory-eval:compare"]);
        Assert.Equal("v4", state.Version);
        Assert.Equal(["messages.Content dictionary-term ×3"], EvaluationRunPlan.Leaks("leak: messages.Content dictionary-term ×3\nFreeze refused"));
    }

    [Fact]
    public void Store_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"run-{Guid.NewGuid():N}", "run.json");
        try
        {
            var store = new EvaluationRunStore(path);
            var state = At(EvaluationRunPlan.Kappa);
            state.Log.Add(new RunLogEntry(DateTimeOffset.UnixEpoch, EvaluationRunPlan.Kappa, "kappa", null, "κ 0.700"));

            store.Save(state);

            var loaded = store.Load()!;
            Assert.Equal(EvaluationRunPlan.Kappa, loaded.Phase);
            Assert.Equal("κ 0.700", Assert.Single(loaded.Log).Note);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `EvaluationRunState` / `EvaluationRunPlan` / `EvaluationRunStore` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/EvaluationRunPlan.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>Values of <see cref="EvaluationRunState.Status"/>.</summary>
public static class RunStatus
{
    public const string Running = "running";

    /// <summary>Waiting for the owner (<see cref="EvaluationRunState.Waiting"/>).</summary>
    public const string Waiting = "waiting";

    /// <summary>Stopped by a failed step or a gate; <see cref="EvaluationRunState.Message"/> says why and what to do. Resumable.</summary>
    public const string Stopped = "stopped";

    public const string Cancelled = "cancelled";

    public const string Completed = "completed";
}

/// <summary>What the run waits for.</summary>
public static class RunWait
{
    public const string Grading = "grading";

    public const string Dictionary = "dictionary";

    public const string Passphrase = "passphrase";
}

/// <summary>One evaluation command of the run.</summary>
public sealed record RunJob(string Tool, IReadOnlyList<string> Args)
{
    public string Command => this.Args.Count > 0 ? this.Args[0] : string.Empty;

    public string Label => this.Tool + " " + string.Join(' ', this.Args);
}

/// <summary>One finished step (ids and numbers only).</summary>
public sealed record RunLogEntry(DateTimeOffset At, string Phase, string Step, int? ExitCode, string Note);

/// <summary>
/// The persisted state of the one evaluation run (<c>/app/state/eval/run/run.json</c>, plaintext: phases, ids, numbers
/// and report paths only). Survives restarts: a step that was running when the agent stopped runs again.
/// </summary>
public sealed class EvaluationRunState
{
    public string RunId { get; set; } = string.Empty;

    public string Status { get; set; } = RunStatus.Running;

    public string Phase { get; set; } = EvaluationRunPlan.Phases[0];

    /// <summary>Index of the next job within <see cref="Phase"/>.</summary>
    public int Step { get; set; }

    public string? Waiting { get; set; }

    /// <summary>Guidance when stopped (no text from the data).</summary>
    public string? Message { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>The evaluation job running now, or null.</summary>
    public string? JobId { get; set; }

    public int HistoryKappaItems { get; set; }

    public int MemoryKappaItems { get; set; }

    public double? Kappa { get; set; }

    public double? HistoryKappa { get; set; }

    public double? MemoryKappa { get; set; }

    /// <summary>True when the reranker was on at the planning step (Gate R is part of the run).</summary>
    public bool Reranker { get; set; }

    /// <summary>The holdout configuration chosen on dev, per tool.</summary>
    public Dictionary<string, string> Selected { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The reranked configuration added for Gate R, per tool.</summary>
    public Dictionary<string, string> RerankConfigs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The frozen version of this run.</summary>
    public string? Version { get; set; }

    public int FreezeRounds { get; set; }

    /// <summary>Leakage findings of the last refused freeze (location, kind, count).</summary>
    public List<string> Leaks { get; set; } = [];

    /// <summary>Report paths in the container by key (<c>history-eval:ablate</c>, <c>memory-eval:holdout</c>, …).</summary>
    public Dictionary<string, string> Reports { get; set; } = new(StringComparer.Ordinal);

    public List<RunLogEntry> Log { get; set; } = [];
}

/// <summary>Reads and writes <see cref="EvaluationRunState"/> atomically.</summary>
public sealed class EvaluationRunStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    public EvaluationRunState? Load() =>
        File.Exists(this.Path) ? JsonSerializer.Deserialize<EvaluationRunState>(File.ReadAllText(this.Path), Json) : null;

    public void Save(EvaluationRunState state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(this.Path)!);
        var temp = this.Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, this.Path, overwrite: true);
    }
}

/// <summary>
/// The fixed order of an evaluation run and what each phase runs: jobs (evaluation commands), internal steps done by
/// <c>EvaluationRunService</c>, or waits for the owner. Pure: tests drive it without jobs.
/// </summary>
public static partial class EvaluationRunPlan
{
    public const string Prepare = "prepare";
    public const string LabelHistory = "label-history";
    public const string LabelMemory = "label-memory";
    public const string Validate = "validate";
    public const string Guard = "guard";
    public const string Grading = "grading";
    public const string Kappa = "kappa";
    public const string Plan = "plan";
    public const string AblateDev = "ablate-dev";
    public const string Select = "select";
    public const string RerankCurve = "rerank-curve";
    public const string SelectRerank = "select-rerank";
    public const string Register = "register";
    public const string HoldoutRaw = "holdout-raw";
    public const string GateR = "gate-r";
    public const string Suggest = "suggest";
    public const string Dictionary = "dictionary";
    public const string Freeze = "freeze";
    public const string HoldoutFrozen = "holdout-frozen";
    public const string Compare = "compare";
    public const string Passphrase = "passphrase";
    public const string Export = "export";
    public const string Summary = "summary";
    public const string Done = "done";

    /// <summary>Most freeze attempts (each refused one goes back to the dictionary review).</summary>
    public const int MaxFreezeRounds = 5;

    private static readonly string[] Order =
    [
        Prepare, LabelHistory, LabelMemory, Validate, Guard, Grading, Kappa, Plan, AblateDev, Select, RerankCurve, SelectRerank,
        Register, HoldoutRaw, GateR, Suggest, Dictionary, Freeze, HoldoutFrozen, Compare, Passphrase, Export, Summary, Done,
    ];

    private static readonly string[] Tools = ["history-eval", "memory-eval"];

    /// <summary>Every phase in order.</summary>
    public static IReadOnlyList<string> Phases => Order;

    /// <summary>The wait of <paramref name="phase"/>, or null when it is not a wait.</summary>
    public static string? WaitOf(string phase) => phase switch
    {
        Grading => RunWait.Grading,
        Dictionary => RunWait.Dictionary,
        Passphrase => RunWait.Passphrase,
        _ => null,
    };

    /// <summary>True for phases <c>EvaluationRunService</c> carries out itself.</summary>
    public static bool IsInternal(string phase) => phase is Kappa or Plan or Select or SelectRerank or Summary;

    /// <summary>The jobs of <paramref name="state"/>'s phase (empty for internal and wait phases).</summary>
    public static IReadOnlyList<RunJob> Jobs(EvaluationRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var version = state.Version ?? string.Empty;
        return state.Phase switch
        {
            Prepare => Both("init"),
            LabelHistory => [new RunJob(Tools[0], ["label", "--kappa", Number(state.HistoryKappaItems)])],
            LabelMemory => [new RunJob(Tools[1], ["label", "--kappa", Number(state.MemoryKappaItems)])],
            Validate => Both("validate"),
            Guard => Both("guard"),
            AblateDev => Both("ablate", "--split", "dev"),
            RerankCurve => state.Selected.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new RunJob(s.Key, ["rerank-curve", "--condition", "normal", "--base", s.Value])).ToList(),
            Register => Both("register"),
            HoldoutRaw => Both("holdout"),
            GateR => state.RerankConfigs.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new RunJob(s.Key, ["gate-r", "--config", s.Value])).ToList(),
            Suggest => [new RunJob("eval-corpus", ["suggest", "--llm"])],
            Freeze => [new RunJob("eval-corpus", ["freeze"])],
            HoldoutFrozen => Both("holdout", "--corpus", "frozen:" + version),
            Compare => [new RunJob("eval-corpus", ["compare", "--version", version]), new RunJob("eval-corpus", ["compare", "--version", version, "--tool", "memory-eval"])],
            Export => [new RunJob("eval-corpus", ["export", "--version", version])],
            _ => [],
        };
    }

    /// <summary>The phase after <paramref name="state"/>'s, skipping the Gate R phases when they do not apply.</summary>
    public static string Next(EvaluationRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var index = Array.IndexOf(Order, state.Phase);
        while (++index < Order.Length)
        {
            var phase = Order[index];
            var skip = phase switch
            {
                RerankCurve or SelectRerank => !state.Reranker || state.Selected.Count == 0,
                GateR => state.RerankConfigs.Count == 0,
                _ => false,
            };
            if (!skip)
            {
                return phase;
            }
        }

        return Done;
    }

    /// <summary>Records what a successful job printed: report paths, the frozen version.</summary>
    public static void Record(EvaluationRunState state, RunJob job, string output)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(output);
        var key = job.Tool == "eval-corpus" && job.Args.Contains("memory-eval", StringComparer.Ordinal) ? "memory-eval:compare"
            : job.Tool == "eval-corpus" ? "history-eval:" + job.Command
            : job.Tool + ":" + job.Command + (job.Args.Contains("--corpus", StringComparer.Ordinal) ? "-frozen" : string.Empty);
        if (ReportLine().Match(output) is { Success: true } report)
        {
            state.Reports[key] = report.Groups[1].Value.Trim();
        }

        if (job.Command == "freeze" && FrozenLine().Match(output) is { Success: true } frozen)
        {
            state.Version = frozen.Groups[1].Value;
            state.Leaks = [];
        }
    }

    /// <summary>The leakage findings a refused freeze printed (<c>leak: location kind ×count</c>).</summary>
    public static List<string> Leaks(string output) =>
        (output ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("leak: ", StringComparison.Ordinal)).Select(l => l[6..]).ToList();

    private static List<RunJob> Both(params string[] args) => Tools.Select(t => new RunJob(t, args)).ToList();

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?:Report|Curve): (.+)$", RegexOptions.Multiline)]
    private static partial Regex ReportLine();

    [GeneratedRegex(@"Frozen (v\d+):")]
    private static partial Regex FrozenLine();
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~EvaluationRunPlanTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/EvaluationRunPlan.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/EvaluationRunPlanTests.cs
git commit -m "feat(eval): evaluation run phases, persisted state and output parsing" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 17: The evaluation run service

The hosted state machine: runs each command as a job of `EvaluationJobService`, carries out κ, plan, selection and summary, waits for the owner, merges approved dictionary terms, starts the export with the passphrase (never stored), stops with guidance and resumes, survives restarts (L12–L16).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/EvaluationRunService.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/EvaluationRunServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/EvaluationRunServiceTests.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Orchestration;

/// <summary>The whole run against fake evaluation commands: no corpus, no LLM, no embedder.</summary>
public sealed class EvaluationRunServiceTests : IDisposable
{
    private const string Passphrase = "correct horse battery";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"eval-run-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
    private readonly RunEnvironment environment;
    private readonly List<string> calls = [];
    private EvaluationJobService jobs;
    private EvaluationRunService service;
    private int freezes;
    private TaskCompletionSource<int>? initGate;

    public EvaluationRunServiceTests()
    {
        this.environment = new RunEnvironment(
            Path.Combine(this.root, "hybrid"),
            Path.Combine(this.root, "memory"),
            Path.Combine(this.root, "eval"),
            () => this.protection,
            () => false,
            () => new LabellingOptions { HistoryQueries = 500, MemoryQueries = 250 });
        (this.jobs, this.service) = this.Create();
    }

    public void Dispose()
    {
        this.service.Dispose();
        this.jobs.Dispose();
        Directory.Delete(this.root, recursive: true);
    }

    private (EvaluationJobService Jobs, EvaluationRunService Service) Create()
    {
        var commands = new Dictionary<string, EvaluationCommand>(StringComparer.Ordinal)
        {
            ["history-eval"] = (run, ct) => this.FakeAsync("history-eval", run, ct),
            ["memory-eval"] = (run, ct) => this.FakeAsync("memory-eval", run, ct),
            ["eval-corpus"] = (run, ct) => this.FakeAsync("eval-corpus", run, ct),
        };
        var jobs = new EvaluationJobService(commands, () => this.protection, TimeProvider.System, NullLogger<EvaluationJobService>.Instance);
        var grading = new GradingService(
            new Dictionary<string, string> { ["history-eval"] = this.environment.HistoryWorkspace, ["memory-eval"] = this.environment.MemoryWorkspace },
            () => this.protection);
        return (jobs, new EvaluationRunService(jobs, grading, this.environment, TimeProvider.System, NullLogger<EvaluationRunService>.Instance));
    }

    private GradingService Grading() => new(
        new Dictionary<string, string> { ["history-eval"] = this.environment.HistoryWorkspace, ["memory-eval"] = this.environment.MemoryWorkspace },
        () => this.protection);

    // Each fake command writes what the real one writes (files and the lines the run parses).
    private async Task<int> FakeAsync(string tool, EvaluationRun run, CancellationToken cancellationToken)
    {
        var command = string.Join(' ', run.Args);
        this.calls.Add(tool + " " + command);
        var memory = tool == "memory-eval";
        var directory = tool == "eval-corpus" ? this.environment.CorpusDirectory : this.environment.Workspace(tool);
        Directory.CreateDirectory(directory);
        var runs = Path.Combine(directory, "runs");
        switch (run.Args[0])
        {
            case "init" when this.initGate is { } gate:
                return await gate.Task.WaitAsync(cancellationToken);
            case "init" when memory:
                new MemoryEvalWorkspace(directory).WritePlan(new MemoryAblationPlan { Configs = [new() { Name = "dense", Mode = MemorySearchMode.Dense }, new() { Name = "hybrid-rrf20", RrfK = 20 }] });
                return 0;
            case "init":
                new EvalWorkspace(directory).WritePlan(new AblationPlan { Configs = [new() { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }, new() { Name = "hybrid-rrf20", RrfK = 20 }, new() { Name = "hybrid-model-queries", UseModelQuery = true }] });
                return 0;
            case "label":
                var prefix = memory ? "m" : "h";
                run.Protection.WriteLines(Path.Combine(directory, "dataset.jsonl"), Enumerable.Range(1, 4).Select(i => memory
                    ? MemoryEvalDataset.ToLine(new MemoryEvalQuery { Id = $"m{i}", Text = "zq", Category = "lookup", Stratum = i == 1 ? "real" : "synthetic", Judgments = [new MemoryJudgment("a", 2)] })
                    : EvalDataset.ToLine(new EvalQuery { Id = $"h{i}", Text = "zq", Category = "lookup", Judgments = [new EvalJudgment(i, 2)] })));
                KappaSampleFile.Write(directory, run.Protection, Enumerable.Range(0, memory ? 2 : 3)
                    .Select(i => new KappaItem { Tool = tool, QueryId = $"{prefix}{i}", CandidateId = $"{i}", LlmGrade = i % 3, Query = "zqquery", Candidate = "zqcandidate" }).ToList());
                await run.Output.WriteLineAsync("Labelled 4 queries");
                return 0;
            case "ablate":
                var ablate = Path.Combine(runs, "ablate-dev-1");
                Directory.CreateDirectory(ablate);
                var baseline = memory ? "dense" : AblationGrid.Baseline;
                var calibration = new CalibrationScore(0, 0);
                var report = new AblationReport(
                    [],
                    [new(baseline, 4, 0.4, 0.4, 0.4, 1, null, null, calibration, 10), new("hybrid-rrf20", 4, 0.5, 0.5, 0.5, 1, null, null, calibration, 12), new("hybrid-model-queries", 4, 0.9, 0.9, 0.9, 1, null, null, calibration, 12)],
                    [new("hybrid-rrf20", baseline, "all", 4, 0.1, 0.02, 0.2, 0.04), new("hybrid-model-queries", baseline, "all", 4, 0.5, 0.4, 0.6, 0.01)]);
                await File.WriteAllTextAsync(Path.Combine(ablate, "results.json"), JsonSerializer.Serialize(report, JsonSerializerOptions.Web), cancellationToken);
                await run.Output.WriteLineAsync("Report: " + Path.Combine(ablate, "report.md"));
                return 0;
            case "register":
                await File.WriteAllTextAsync(Path.Combine(directory, "preregistration.json"), "{}", cancellationToken);
                return 0;
            case "holdout":
                var frozen = run.Args.Contains("--corpus");
                var holdout = frozen
                    ? Path.Combine(this.environment.EvaluationDirectory, "frozen", "v1", memory ? "memory" : "history", "runs", "holdout-test-1")
                    : Path.Combine(runs, "holdout-test-1");
                Directory.CreateDirectory(holdout);
                await File.WriteAllTextAsync(Path.Combine(holdout, "verdict.json"), string.Create(CultureInfo.InvariantCulture, $"{{\"config\":\"hybrid-rrf20\",\"mean\":{(frozen ? 0.03 : 0.04)},\"nonInferior\":true}}"), cancellationToken);
                return 0;
            case "freeze" when ++this.freezes == 1:
                await run.Output.WriteLineAsync("leak: messages.Content dictionary-term ×2");
                return 1;
            case "freeze":
                await run.Output.WriteLineAsync("Frozen v1: 10 messages, 3 memories");
                return 0;
            case "compare":
                var compare = Path.Combine(this.environment.EvaluationDirectory, "compare", (memory || run.Args.Contains("memory-eval") ? "memory" : "history") + ".md");
                Directory.CreateDirectory(Path.GetDirectoryName(compare)!);
                await File.WriteAllTextAsync(compare, "| Δ |", cancellationToken);
                await run.Output.WriteLineAsync("Report: " + compare);
                return 0;
            case "export":
                return run.Passphrase == Passphrase ? 0 : 2;
            default:
                return 0;
        }
    }

    private async Task<EvaluationRunState> UntilAsync(Func<EvaluationRunState, bool> done)
    {
        for (var i = 0; i < 300; i++)
        {
            this.service.Tick();
            await this.jobs.WhenIdleAsync();
            var state = this.service.Snapshot()!;
            if (done(state))
            {
                return state;
            }
        }

        throw new InvalidOperationException("The run did not get there: " + JsonSerializer.Serialize(this.service.Snapshot()));
    }

    private void GradeAll(Func<int, int> grade)
    {
        var grading = this.Grading();
        for (var i = 0; grading.Item(i) is not null; i++)
        {
            grading.Grade(i, grade(i));
        }
    }

    [Fact]
    public async Task Run_WholeProtocol_StopsOnlyForTheOwnersThreeInputs()
    {
        Assert.Null(this.service.Start(fresh: false));

        var state = await this.UntilAsync(s => s.Waiting == RunWait.Grading);
        Assert.Equal((50, 25), (state.HistoryKappaItems, state.MemoryKappaItems));
        Assert.Contains("history-eval label --kappa 50", this.calls);
        Assert.Contains("memory-eval guard", this.calls);

        this.GradeAll(i => i % 3);
        state = await this.UntilAsync(s => s.Waiting == RunWait.Dictionary);
        Assert.Equal(1.0, state.Kappa);
        Assert.Contains("pre-registered", await File.ReadAllTextAsync(Path.Combine(this.environment.HistoryWorkspace, "analysis-plan.md")), StringComparison.Ordinal);
        Assert.Equal("hybrid-rrf20", new EvalWorkspace(this.environment.HistoryWorkspace).ReadPlan().Holdout!.Config);
        Assert.Equal("hybrid-rrf20", new MemoryEvalWorkspace(this.environment.MemoryWorkspace).ReadPlan().Holdout!.Config);
        Assert.True(this.calls.IndexOf("history-eval register") < this.calls.IndexOf("history-eval holdout"));
        Assert.True(this.calls.IndexOf("history-eval ablate --split dev") < this.calls.IndexOf("history-eval register"));
        Assert.EndsWith("verdict.json", state.Reports["history-eval:holdout"], StringComparison.Ordinal);

        Assert.Null(this.service.ApproveDictionary([new DictionaryTerm("PersonA", "person"), new DictionaryTerm("TownB", "place")]));
        state = await this.UntilAsync(s => s.Waiting == RunWait.Dictionary && s.FreezeRounds == 1);
        Assert.Equal(["messages.Content dictionary-term ×2"], state.Leaks);
        var dictionary = this.protection.ReadLines(Path.Combine(this.environment.CorpusDirectory, "dictionary.jsonl"));
        Assert.Equal(2, dictionary.Count);
        Assert.Contains("PersonA", dictionary[0], StringComparison.Ordinal);

        Assert.Null(this.service.ApproveDictionary([new DictionaryTerm("personA", "person")]));
        state = await this.UntilAsync(s => s.Waiting == RunWait.Passphrase);
        Assert.Equal(2, this.protection.ReadLines(Path.Combine(this.environment.CorpusDirectory, "dictionary.jsonl")).Count);
        Assert.Equal("v1", state.Version);
        Assert.Contains("history-eval holdout --corpus frozen:v1", this.calls);
        Assert.Contains("eval-corpus compare --version v1 --tool memory-eval", this.calls);

        Assert.NotNull(this.service.Export("short"));
        Assert.Null(this.service.Export(Passphrase));
        state = await this.UntilAsync(s => s.Status == RunStatus.Completed);
        var summary = this.service.Report("summary")!;
        Assert.Contains("\"nonInferior\": true", summary, StringComparison.Ordinal);
        Assert.Contains("hybrid-rrf20", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("zq", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, await File.ReadAllTextAsync(Path.Combine(this.environment.RunDirectory, "run.json")), StringComparison.Ordinal);
        Assert.DoesNotContain(this.calls, c => c.Contains("rerank", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_KappaBelowTheGate_StopsWithGuidanceBeforeAnyComparison()
    {
        this.service.Start(fresh: false);
        await this.UntilAsync(s => s.Waiting == RunWait.Grading);

        this.GradeAll(i => (i + 1) % 3);
        var state = await this.UntilAsync(s => s.Status == RunStatus.Stopped);

        Assert.Contains("below 0.6", state.Message, StringComparison.Ordinal);
        Assert.Equal(EvaluationRunPlan.Grading, state.Phase);
        Assert.DoesNotContain(this.calls, c => c.Contains("ablate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_PreviousRegistration_RefusedUnlessFresh()
    {
        Directory.CreateDirectory(this.environment.HistoryWorkspace);
        await File.WriteAllTextAsync(Path.Combine(this.environment.HistoryWorkspace, "preregistration.json"), "{}", CancellationToken.None);

        Assert.Contains("fresh run", this.service.Start(fresh: false), StringComparison.Ordinal);
        Assert.Null(this.service.Start(fresh: true));

        Assert.False(File.Exists(Path.Combine(this.environment.HistoryWorkspace, "preregistration.json")));
        Assert.Contains("in progress", this.service.Start(fresh: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_InterruptedJob_RunsTheStepAgain()
    {
        this.initGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.service.Start(fresh: false);
        this.service.Tick();
        Assert.NotNull(this.service.Snapshot()!.JobId);

        // The agent restarts: the job is gone, the persisted run is picked up by a new service.
        this.initGate.SetResult(0);
        await this.jobs.WhenIdleAsync();
        this.service.Dispose();
        this.jobs.Dispose();
        this.initGate = null;
        this.calls.Clear();
        (this.jobs, this.service) = this.Create();

        var state = await this.UntilAsync(s => s.Phase == EvaluationRunPlan.LabelHistory);

        Assert.Equal(["history-eval init", "memory-eval init"], this.calls.Take(2));
        Assert.Null(state.Message);
    }

    [Fact]
    public async Task CancelThenResume_ContinuesTheSameStep()
    {
        this.initGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.service.Start(fresh: false);
        this.service.Tick();

        Assert.Null(this.service.Cancel());
        await this.jobs.WhenIdleAsync();
        Assert.Equal(RunStatus.Cancelled, this.service.Snapshot()!.Status);
        this.initGate = null;

        Assert.Null(this.service.Resume());
        var state = await this.UntilAsync(s => s.Phase == EvaluationRunPlan.LabelHistory);
        Assert.Equal(RunStatus.Running, state.Status);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `EvaluationRunService` / `RunEnvironment` / `DictionaryTerm` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/EvaluationRunService.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Sanitising;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>Where the run works and what it consults.</summary>
/// <param name="HistoryWorkspace">The history-eval workspace.</param>
/// <param name="MemoryWorkspace">The memory-eval workspace.</param>
/// <param name="EvaluationDirectory">The evaluation root (<c>/app/state/eval</c>): corpus, frozen versions, the run's own files.</param>
/// <param name="Protection">The protection (throws while the storage key is missing).</param>
/// <param name="RerankerEnabled">Whether the Bridge granted reranking (Gate R joins the run).</param>
/// <param name="Options">The labelling options (models, counts, κ sample).</param>
public sealed record RunEnvironment(
    string HistoryWorkspace,
    string MemoryWorkspace,
    string EvaluationDirectory,
    Func<EvalProtection> Protection,
    Func<bool> RerankerEnabled,
    Func<LabellingOptions> Options)
{
    public string RunDirectory => Path.Combine(this.EvaluationDirectory, "run");

    public string CorpusDirectory => Path.Combine(this.EvaluationDirectory, "corpus");

    public string Workspace(string tool) => tool == "memory-eval" ? this.MemoryWorkspace : this.HistoryWorkspace;
}

/// <summary>A dictionary term the owner accepted or added.</summary>
public sealed record DictionaryTerm(string Term, string Kind, string? Entity = null);

/// <summary>
/// "Run evaluation": one persisted state machine (<see cref="EvaluationRunPlan"/>) that runs every evaluation command as
/// a job of <see cref="EvaluationJobService"/>, carries out the internal steps (κ gate, analysis plan, dev-only
/// configuration choice, Gate R cell, summary) and stops at the owner's three inputs: the κ grading, the dictionary
/// approval and the package passphrase. One run at a time; resumable after a restart (an interrupted job runs again);
/// a failed step stops the run with guidance and <see cref="Resume"/> continues it. Logs and state hold ids and numbers only.
/// </summary>
public sealed partial class EvaluationRunService : BackgroundService
{
    /// <summary>Minimum LLM-vs-human agreement (§4.2).</summary>
    public const double MinKappa = 0.6;

    /// <summary>Non-inferiority margin written into <c>holdout.margin</c>.</summary>
    public const double Margin = 0.02;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] WorkspaceFiles =
    [
        "dataset.jsonl", "analysis-plan.md", "ablation.json", "preregistration.json", "judge-agreement.csv", "seeds.jsonl", "pool.jsonl",
        LabellingStateStore.FileName, KappaSampleFile.FileName, Labeller.ReportFileName,
    ];

    private readonly EvaluationJobService jobs;
    private readonly GradingService grading;
    private readonly RunEnvironment environment;
    private readonly EvaluationRunStore store;
    private readonly TimeProvider time;
    private readonly ILogger<EvaluationRunService> logger;
    private readonly Lock sync = new();
    private EvaluationRunState? state;

    public EvaluationRunService(EvaluationJobService jobs, GradingService grading, RunEnvironment environment, TimeProvider time, ILogger<EvaluationRunService> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        this.jobs = jobs;
        this.grading = grading;
        this.environment = environment;
        this.time = time;
        this.logger = logger;
        this.store = new EvaluationRunStore(Path.Combine(environment.RunDirectory, "run.json"));
        this.state = this.store.Load();
    }

    /// <summary>A copy of the run's state, or null when there never was a run.</summary>
    public EvaluationRunState? Snapshot()
    {
        lock (this.sync)
        {
            return this.state is null ? null : JsonSerializer.Deserialize<EvaluationRunState>(JsonSerializer.Serialize(this.state, Json), Json);
        }
    }

    /// <summary>Starts a new run; <paramref name="fresh"/> first deletes a previous evaluation's workspace files (not frozen versions or the dictionary).</summary>
    public string? Start(bool fresh)
    {
        lock (this.sync)
        {
            if (this.state is { Status: RunStatus.Running or RunStatus.Waiting })
            {
                return "An evaluation run is in progress; cancel it first.";
            }

            if (this.environment.Options().Validate() is { Count: > 0 } errors)
            {
                return string.Join(" ", errors);
            }

            var previous = GradingService.Tools
                .Any(t => File.Exists(Path.Combine(this.environment.Workspace(t), "dataset.jsonl")) || File.Exists(Path.Combine(this.environment.Workspace(t), "preregistration.json")));
            if (previous && !fresh)
            {
                return "A previous evaluation's dataset or registration exists. Start a fresh run to delete them (frozen versions and the dictionary are kept).";
            }

            if (fresh)
            {
                this.ResetWorkspaces();
            }

            var options = this.environment.Options();
            var history = (int)Math.Round(options.KappaSample * options.HistoryQueries / (double)(options.HistoryQueries + options.MemoryQueries), MidpointRounding.AwayFromZero);
            var now = this.time.GetUtcNow();
            this.state = new EvaluationRunState
            {
                RunId = Guid.NewGuid().ToString("N")[..12],
                StartedAt = now,
                HistoryKappaItems = history,
                MemoryKappaItems = options.KappaSample - history,
            };
            this.Save(this.state);
            this.LogStarted(this.state.RunId, fresh);
            return null;
        }
    }

    /// <summary>Cancels the run (and its running job).</summary>
    public string? Cancel()
    {
        lock (this.sync)
        {
            if (this.state is not { Status: RunStatus.Running or RunStatus.Waiting or RunStatus.Stopped } run)
            {
                return "No evaluation run to cancel.";
            }

            if (run.JobId is { } job)
            {
                this.jobs.Cancel(job);
            }

            run.Status = RunStatus.Cancelled;
            run.Waiting = null;
            run.JobId = null;
            this.Save(run);
            return null;
        }
    }

    /// <summary>Continues a stopped or cancelled run from the step it stopped at.</summary>
    public string? Resume()
    {
        lock (this.sync)
        {
            if (this.state is not { Status: RunStatus.Stopped or RunStatus.Cancelled } run)
            {
                return "Only a stopped or cancelled run can be resumed.";
            }

            run.Status = RunStatus.Running;
            run.Message = null;
            run.JobId = null;
            if (run.Phase == EvaluationRunPlan.Export)
            {
                run.Phase = EvaluationRunPlan.Passphrase;
                run.Step = 0;
            }

            this.Save(run);
            return null;
        }
    }

    /// <summary>Adds the owner's accepted terms to <c>dictionary.jsonl</c> (sealed) and continues with the freeze.</summary>
    public string? ApproveDictionary(IReadOnlyList<DictionaryTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        lock (this.sync)
        {
            if (this.state is not { Waiting: RunWait.Dictionary } run)
            {
                return "The run is not waiting for the dictionary.";
            }

            if (terms.FirstOrDefault(t => string.IsNullOrWhiteSpace(t.Term) || !SanitiserLexicon.Kinds.Contains(t.Kind, StringComparer.Ordinal)) is { } bad)
            {
                return $"Term kinds are {string.Join(", ", SanitiserLexicon.Kinds)}; '{bad.Kind}' is not one of them.";
            }

            var protection = this.environment.Protection();
            var path = Path.Combine(this.environment.CorpusDirectory, "dictionary.jsonl");
            var lines = File.Exists(path) ? protection.ReadLines(path).ToList() : [];
            var known = SanitiserLexicon.Parse(lines).Entries.Select(e => SanitiserLexicon.Canonical(e.Term)).ToHashSet(StringComparer.Ordinal);
            foreach (var term in terms.Where(t => known.Add(SanitiserLexicon.Canonical(t.Term))))
            {
                lines.Add(JsonSerializer.Serialize(new LexiconEntry { Term = term.Term.Trim(), Kind = term.Kind, Entity = term.Entity }, JsonSerializerOptions.Web));
            }

            Directory.CreateDirectory(this.environment.CorpusDirectory);
            protection.WriteLines(path, lines);
            run.Log.Add(new RunLogEntry(this.time.GetUtcNow(), run.Phase, "dictionary approved", null, string.Create(CultureInfo.InvariantCulture, $"{terms.Count} terms accepted")));
            MoveTo(run, EvaluationRunPlan.Freeze);
            this.Save(run);
            return null;
        }
    }

    /// <summary>Starts the export with the owner's package passphrase (held for the job only, never stored).</summary>
    public string? Export(string passphrase)
    {
        lock (this.sync)
        {
            if (this.state is not { Waiting: RunWait.Passphrase } run)
            {
                return "The run is not waiting for the package passphrase.";
            }

            if (passphrase is not { Length: >= PassphraseKdf.MinimumPassphraseLength })
            {
                return $"The passphrase needs at least {PassphraseKdf.MinimumPassphraseLength} characters.";
            }

            MoveTo(run, EvaluationRunPlan.Export);
            var job = EvaluationRunPlan.Jobs(run)[0];
            var started = this.jobs.Start(new EvaluationStartRequestDto { Tool = job.Tool, Args = job.Args, Passphrase = passphrase });
            if (started.State == EvaluationJobStates.Refused)
            {
                MoveTo(run, EvaluationRunPlan.Passphrase);
                run.Status = RunStatus.Waiting;
                run.Waiting = RunWait.Passphrase;
                this.Save(run);
                return started.Output;
            }

            run.JobId = started.Id;
            this.Save(run);
            return null;
        }
    }

    /// <summary>A report the run recorded (Markdown or JSON, ids and numbers only), or null.</summary>
    public string? Report(string key)
    {
        lock (this.sync)
        {
            return this.state?.Reports.TryGetValue(key, out var path) == true && File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    /// <summary>One step of progress: checks the running job, a wait, or starts the next step.</summary>
    public void Tick()
    {
        lock (this.sync)
        {
            if (this.state is not { Status: RunStatus.Running or RunStatus.Waiting } run)
            {
                return;
            }

            var before = JsonSerializer.Serialize(run, Json);
            try
            {
                this.Advance(run);
            }
            catch (StorageLockedException)
            {
                // The key has not arrived yet (agent restart): try again on the next tick.
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or InvalidDataException)
            {
                this.Stop(run, ex.Message);
            }

            if (!string.Equals(before, JsonSerializer.Serialize(run, Json), StringComparison.Ordinal))
            {
                this.Save(run);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            this.Tick();
            await Task.Delay(TickInterval, this.time, stoppingToken).ConfigureAwait(false);
        }
    }

    private void Advance(EvaluationRunState run)
    {
        if (run.JobId is { } jobId)
        {
            var job = this.jobs.Get(jobId);
            if (job is { State: EvaluationJobStates.Running })
            {
                return;
            }

            run.JobId = null;
            if (job is not null)
            {
                this.Finished(run, job);
            }

            return;
        }

        if (run.Phase == EvaluationRunPlan.Done)
        {
            run.Status = RunStatus.Completed;
            run.FinishedAt ??= this.time.GetUtcNow();
            return;
        }

        if (EvaluationRunPlan.WaitOf(run.Phase) is { } wait)
        {
            if (wait == RunWait.Grading && this.grading.Progress().Complete)
            {
                MoveTo(run, EvaluationRunPlan.Next(run));
                return;
            }

            run.Status = RunStatus.Waiting;
            run.Waiting = wait;
            return;
        }

        if (EvaluationRunPlan.IsInternal(run.Phase))
        {
            this.Internal(run);
            return;
        }

        var jobs = EvaluationRunPlan.Jobs(run);
        if (run.Step >= jobs.Count)
        {
            MoveTo(run, EvaluationRunPlan.Next(run));
            return;
        }

        if (run.Phase == EvaluationRunPlan.Export)
        {
            // The passphrase is never stored: after a restart the owner enters it again.
            MoveTo(run, EvaluationRunPlan.Passphrase);
            return;
        }

        var next = jobs[run.Step];
        if (run.Phase == EvaluationRunPlan.Register && File.Exists(Path.Combine(this.environment.Workspace(next.Tool), "preregistration.json")))
        {
            run.Log.Add(new RunLogEntry(this.time.GetUtcNow(), run.Phase, next.Label, 0, "already registered"));
            run.Step++;
            return;
        }

        var started = this.jobs.Start(new EvaluationStartRequestDto { Tool = next.Tool, Args = next.Args });
        if (started.State == EvaluationJobStates.Refused)
        {
            // Another job (a manual one) or a missing key: wait. Anything else stops the run.
            if (!started.Output.Contains("Another evaluation job", StringComparison.Ordinal) && !started.Output.Contains("storage key", StringComparison.Ordinal))
            {
                this.Stop(run, $"{next.Label} was refused: {started.Output}");
            }

            return;
        }

        run.JobId = started.Id;
    }

    private void Finished(EvaluationRunState run, EvaluationJobDto job)
    {
        var step = EvaluationRunPlan.Jobs(run)[run.Step];
        var tail = Tail(job.Output);
        run.Log.Add(new RunLogEntry(this.time.GetUtcNow(), run.Phase, step.Label, job.ExitCode, tail));
        this.LogStep(run.RunId, run.Phase, step.Tool, step.Command, job.ExitCode ?? -1);
        if (job.State == EvaluationJobStates.Succeeded)
        {
            EvaluationRunPlan.Record(run, step, job.Output);
            this.RecordRunDirectory(run, step);
            run.Step++;
            return;
        }

        if (job.State == EvaluationJobStates.Cancelled)
        {
            run.Status = RunStatus.Cancelled;
            return;
        }

        if (run.Phase == EvaluationRunPlan.Freeze && job.ExitCode == 1)
        {
            run.Leaks = EvaluationRunPlan.Leaks(job.Output);
            if (++run.FreezeRounds >= EvaluationRunPlan.MaxFreezeRounds)
            {
                this.Stop(run, string.Create(CultureInfo.InvariantCulture,
                    $"The leakage check refused {run.FreezeRounds} freezes. Add the missing spellings to the dictionary (Edit-CortexEvalFile.ps1 -Tool eval-corpus -Name dictionary.jsonl), then Resume."));
                run.Phase = EvaluationRunPlan.Freeze;
                return;
            }

            MoveTo(run, EvaluationRunPlan.Suggest);
            return;
        }

        this.Stop(run, run.Phase is EvaluationRunPlan.LabelHistory or EvaluationRunPlan.LabelMemory && job.ExitCode == 3
            ? "The label job reached Evaluation:Labelling:MaxCalls. Raise the cap in the agent configuration and press Resume: it continues where it stopped."
            : string.Create(CultureInfo.InvariantCulture, $"{step.Label} failed (exit {job.ExitCode}): {tail}"));
    }

    private void Internal(EvaluationRunState run)
    {
        switch (run.Phase)
        {
            case EvaluationRunPlan.Kappa:
                var progress = this.grading.Progress();
                (run.Kappa, run.HistoryKappa, run.MemoryKappa) = (progress.Kappa, progress.HistoryKappa, progress.MemoryKappa);
                if (progress.Kappa is not { } kappa || kappa < MinKappa)
                {
                    this.Stop(run, string.Create(CultureInfo.InvariantCulture,
                        $"LLM-vs-human κ = {progress.Kappa ?? 0:F3} is below {MinKappa}: the judge's grades may not be used. Check a few disagreements, then start a fresh run with another Evaluation:Labelling:JudgeModel (or regrade the sample and Resume)."));
                    run.Phase = EvaluationRunPlan.Grading;
                    return;
                }

                break;
            case EvaluationRunPlan.Plan:
                run.Reranker = this.environment.RerankerEnabled();
                this.WritePlans(run);
                break;
            case EvaluationRunPlan.Select:
                this.SelectConfigs(run);
                break;
            case EvaluationRunPlan.SelectRerank:
                this.SelectRerank(run);
                break;
            case EvaluationRunPlan.Summary:
                this.WriteSummary(run);
                break;
        }

        run.Log.Add(new RunLogEntry(this.time.GetUtcNow(), run.Phase, run.Phase, null, "done"));
        MoveTo(run, EvaluationRunPlan.Next(run));
    }

    private void WritePlans(EvaluationRunState run)
    {
        var protection = this.environment.Protection();
        var options = this.environment.Options();
        foreach (var tool in GradingService.Tools)
        {
            var directory = this.environment.Workspace(tool);
            var (categories, strata, ids) = tool == "memory-eval"
                ? Facts(new MemoryEvalWorkspace(directory, protection).ReadDataset().Select(q => (q.Category, q.Stratum, q.Id)))
                : Facts(new EvalWorkspace(directory, protection).ReadDataset().Select(q => (q.Category, q.Stratum, q.Id)));
            var facts = new PlanFacts
            {
                Tool = tool,
                Date = this.time.GetUtcNow(),
                Queries = ids.Count,
                Dev = ids.Count(id => EvalSplits.Assign(id) == EvalSplits.Dev),
                Categories = categories,
                Strata = strata,
                QueryModel = options.QueryModel,
                JudgeModel = options.JudgeModel,
                PoolDepth = options.PoolDepth,
                Kappa = run.Kappa ?? 0,
                ToolKappa = tool == "memory-eval" ? run.MemoryKappa : run.HistoryKappa,
                KappaItems = run.HistoryKappaItems + run.MemoryKappaItems,
                Baseline = tool == "memory-eval" ? MemoryAblationGrid.Baseline : AblationGrid.Baseline,
                Margin = Margin,
                Reranker = run.Reranker,
            };
            File.WriteAllText(Path.Combine(directory, "analysis-plan.md"), AnalysisPlanWriter.Render(facts));
        }
    }

    private void SelectConfigs(EvaluationRunState run)
    {
        var protection = this.environment.Protection();
        foreach (var tool in GradingService.Tools)
        {
            var report = ReadReport(run, tool + ":ablate", "results.json");
            var dev = JsonSerializer.Deserialize<AblationReport>(report, Json) ?? throw new InvalidDataException("The dev ablation results are empty.");
            if (tool == "memory-eval")
            {
                var workspace = new MemoryEvalWorkspace(this.environment.MemoryWorkspace, protection);
                var plan = workspace.ReadPlan();
                var chosen = ConfigSelector.Select(dev, plan.Baseline, plan.Configs.Where(c => !c.UseModelQuery && c.RerankBudget() is null).Select(c => c.Name).ToList())
                    ?? throw new InvalidOperationException("No memory configuration qualifies on dev (judged@10 below 0.8 everywhere): deepen the pool (Evaluation:Labelling:PoolDepth) and start a fresh run.");
                workspace.WritePlan(plan with { Holdout = new MemoryHoldoutPlan { Config = chosen, Margin = Margin } });
                run.Selected[tool] = chosen;
            }
            else
            {
                var workspace = new EvalWorkspace(this.environment.HistoryWorkspace, protection);
                var plan = workspace.ReadPlan();
                var chosen = ConfigSelector.Select(dev, plan.Baseline, plan.Configs.Where(c => !c.UseModelQuery && c.RerankBudget() is null).Select(c => c.Name).ToList())
                    ?? throw new InvalidOperationException("No history configuration qualifies on dev (judged@10 below 0.8 everywhere): deepen the pool (Evaluation:Labelling:PoolDepth) and start a fresh run.");
                workspace.WritePlan(plan with { Holdout = new HoldoutPlan { Config = chosen, Margin = Margin } });
                run.Selected[tool] = chosen;
            }
        }
    }

    private void SelectRerank(EvaluationRunState run)
    {
        var protection = this.environment.Protection();
        foreach (var (tool, selected) in run.Selected)
        {
            var curve = JsonSerializer.Deserialize<CurveFile>(ReadReport(run, tool + ":rerank-curve", "curve.json"), Json);
            if (RerankCellSelector.Select(curve?.Rows ?? []) is not { } budget)
            {
                continue;
            }

            var name = selected + "-rerank-" + budget.Label;
            if (tool == "memory-eval")
            {
                var workspace = new MemoryEvalWorkspace(this.environment.MemoryWorkspace, protection);
                var plan = workspace.ReadPlan();
                var config = plan.Configs.Single(c => c.Name == selected) with { Name = name, RerankTopN = budget.TopN, RerankMaxDocChars = budget.MaxDocChars };
                workspace.WritePlan(plan with { Configs = [.. plan.Configs.Where(c => c.Name != name), config] });
            }
            else
            {
                var workspace = new EvalWorkspace(this.environment.HistoryWorkspace, protection);
                var plan = workspace.ReadPlan();
                var config = plan.Configs.Single(c => c.Name == selected) with { Name = name, RerankTopN = budget.TopN, RerankMaxDocChars = budget.MaxDocChars };
                workspace.WritePlan(plan with { Configs = [.. plan.Configs.Where(c => c.Name != name), config] });
            }

            run.RerankConfigs[tool] = name;
        }
    }

    private void WriteSummary(EvaluationRunState run)
    {
        object? Verdict(string key) => run.Reports.TryGetValue(key, out var path) && File.Exists(path)
            ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path))
            : null;
        var summary = new
        {
            run.RunId,
            run.StartedAt,
            finishedAt = this.time.GetUtcNow(),
            run.Version,
            kappa = new { all = run.Kappa, history = run.HistoryKappa, memory = run.MemoryKappa, items = run.HistoryKappaItems + run.MemoryKappaItems },
            selected = run.Selected,
            history = new { raw = Verdict("history-eval:holdout"), sanitised = Verdict("history-eval:holdout-frozen"), gateR = Verdict("history-eval:gate-r") },
            memory = new { raw = Verdict("memory-eval:holdout"), sanitised = Verdict("memory-eval:holdout-frozen"), gateR = Verdict("memory-eval:gate-r") },
            compare = new
            {
                history = run.Reports.GetValueOrDefault("history-eval:compare"),
                memory = run.Reports.GetValueOrDefault("memory-eval:compare"),
            },
        };
        var path = Path.Combine(this.environment.RunDirectory, "summary.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, Json));
        run.Reports["summary"] = path;
    }

    // Holdout and Gate R print no path: their newest run directory holds verdict.json.
    private void RecordRunDirectory(EvaluationRunState run, RunJob step)
    {
        var (prefix, frozen) = step.Command switch
        {
            "holdout" => ("holdout-test-", step.Args.Contains("--corpus", StringComparer.Ordinal)),
            "gate-r" => ("gate-r-", false),
            _ => (null, false),
        };
        if (prefix is null)
        {
            return;
        }

        var workspace = frozen
            ? Path.Combine(new FrozenCorpusStore(this.environment.EvaluationDirectory).DirectoryOf(run.Version!), step.Tool == "memory-eval" ? "memory" : "history")
            : this.environment.Workspace(step.Tool);
        var runs = Path.Combine(workspace, "runs");
        var newest = Directory.Exists(runs)
            ? Directory.EnumerateDirectories(runs, prefix + "*").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (newest is not null)
        {
            run.Reports[step.Tool + ":" + step.Command + (frozen ? "-frozen" : string.Empty)] = Path.Combine(newest, "verdict.json");
        }
    }

    private void ResetWorkspaces()
    {
        foreach (var tool in GradingService.Tools)
        {
            var directory = this.environment.Workspace(tool);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var name in WorkspaceFiles)
            {
                File.Delete(Path.Combine(directory, name));
            }

            if (Directory.Exists(Path.Combine(directory, "runs")))
            {
                Directory.Delete(Path.Combine(directory, "runs"), recursive: true);
            }
        }

        if (Directory.Exists(this.environment.CorpusDirectory))
        {
            File.Delete(Path.Combine(this.environment.CorpusDirectory, "dictionary.suggested.jsonl"));
        }
    }

    private static void MoveTo(EvaluationRunState run, string phase)
    {
        run.Phase = phase;
        run.Step = 0;
        run.Status = RunStatus.Running;
        run.Waiting = null;
    }

    private void Stop(EvaluationRunState run, string message)
    {
        run.Status = RunStatus.Stopped;
        run.Message = message;
        run.Waiting = null;
        this.LogStopped(run.RunId, run.Phase);
    }

    private void Save(EvaluationRunState run)
    {
        run.UpdatedAt = this.time.GetUtcNow();
        this.store.Save(run);
    }

    private static string ReadReport(EvaluationRunState run, string key, string file) =>
        run.Reports.TryGetValue(key, out var path) && Path.GetDirectoryName(path) is { } directory && File.Exists(Path.Combine(directory, file))
            ? File.ReadAllText(Path.Combine(directory, file))
            : throw new InvalidOperationException($"The run has no {file} for {key}; run that step again (Resume).");

    private static (Dictionary<string, int> Categories, Dictionary<string, int> Strata, List<string> Ids) Facts(IEnumerable<(string Category, string Stratum, string Id)> queries)
    {
        var list = queries.ToList();
        return (
            list.GroupBy(q => q.Category, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            list.GroupBy(q => q.Stratum, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            list.Select(q => q.Id).ToList());
    }

    // The last non-empty lines of a job's output (ids, numbers and paths).
    private static string Tail(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(3);
        var text = string.Join(" | ", lines);
        return text.Length <= 600 ? text : text[^600..];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation run {RunId} started (fresh: {Fresh})")]
    private partial void LogStarted(string runId, bool fresh);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation run {RunId} {Phase}: {Tool} {Command} exited {ExitCode}")]
    private partial void LogStep(string runId, string phase, string tool, string command, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evaluation run {RunId} stopped at {Phase}")]
    private partial void LogStopped(string runId, string phase);

    private sealed record CurveFile(IReadOnlyList<RerankCurveRow>? Rows);
}
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~EvaluationRunServiceTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/EvaluationRunService.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/EvaluationRunServiceTests.cs
git commit -m "feat(eval): resumable evaluation run service chaining the protocol" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 18: Hub surface and DI

Five `IEvaluationHub` methods with their DTOs, `EvaluationRunApi` mapping state and commands, the hub partial, and DI of the grading service, the run service (hosted) and the API.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/EvaluationRunApi.cs`
- Create: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.EvaluationRun.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs`
- Create: `src/Cortex.Contained.Contracts/Hub/EvaluationRunTypes.cs`
- Modify: `src/Cortex.Contained.Contracts/Hub/IEvaluationHub.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/EvaluationRunApiTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/EvaluationRunApiTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Orchestration;

public sealed class EvaluationRunApiTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"run-api-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
    private readonly EvaluationJobService jobs;
    private readonly EvaluationRunService runs;
    private readonly GradingService grading;
    private readonly EvaluationRunApi api;

    public EvaluationRunApiTests()
    {
        var workspaces = new Dictionary<string, string> { ["history-eval"] = Path.Combine(this.root, "hybrid"), ["memory-eval"] = Path.Combine(this.root, "memory") };
        Directory.CreateDirectory(workspaces["history-eval"]);
        Directory.CreateDirectory(workspaces["memory-eval"]);
        var commands = new Dictionary<string, EvaluationCommand>(StringComparer.Ordinal)
        {
            ["history-eval"] = (_, _) => Task.FromResult(0),
            ["memory-eval"] = (_, _) => Task.FromResult(0),
        };
        this.jobs = new EvaluationJobService(commands, () => this.protection, TimeProvider.System, NullLogger<EvaluationJobService>.Instance);
        this.grading = new GradingService(workspaces, () => this.protection);
        var environment = new RunEnvironment(workspaces["history-eval"], workspaces["memory-eval"], Path.Combine(this.root, "eval"), () => this.protection, () => false, () => new LabellingOptions());
        this.runs = new EvaluationRunService(this.jobs, this.grading, environment, TimeProvider.System, NullLogger<EvaluationRunService>.Instance);
        this.api = new EvaluationRunApi(this.runs, this.grading, () => new LabellingOptions());
        KappaSampleFile.Write(workspaces["history-eval"], this.protection,
            [new KappaItem { Tool = "history-eval", QueryId = "h1", CandidateId = "7", LlmGrade = 2, Query = "zqquery", Candidate = "zqcandidate" }, new KappaItem { Tool = "history-eval", QueryId = "h2", CandidateId = "8", LlmGrade = 0, Query = "zqquery2", Candidate = "zqcandidate2" }]);
    }

    public void Dispose()
    {
        this.runs.Dispose();
        this.jobs.Dispose();
        Directory.Delete(this.root, recursive: true);
    }

    [Fact]
    public void Run_NoRunYet_ShowsTheConfiguredModels()
    {
        var run = this.api.Run();

        Assert.False(run.Exists);
        Assert.Equal(("gpt-6.1-sol", "claude-opus-5.5"), (run.QueryModel, run.JudgeModel));
    }

    [Fact]
    public void Control_Start_RunsAndListsThePhases()
    {
        var result = this.api.Control(new EvaluationRunCommandDto { Action = EvaluationRunActions.Start });

        Assert.True(result.Ok);
        Assert.Equal(RunStatus.Running, result.Run.Status);
        Assert.Equal(("prepare", "current"), (result.Run.Phases[0].Name, result.Run.Phases[0].State));
        Assert.Equal("pending", result.Run.Phases[^1].State);
        Assert.Equal("history-eval init", result.Run.CurrentJob);
        Assert.DoesNotContain(result.Run.Phases, p => p.Name == EvaluationRunPlan.Done);
    }

    [Fact]
    public void Control_WrongMomentOrUnknownAction_ExplainsWithoutChangingTheRun()
    {
        var export = this.api.Control(new EvaluationRunCommandDto { Action = EvaluationRunActions.Export, Passphrase = "long enough passphrase" });
        var unknown = this.api.Control(new EvaluationRunCommandDto { Action = "skip-kappa" });

        Assert.False(export.Ok);
        Assert.Contains("not waiting", export.Error, StringComparison.Ordinal);
        Assert.Contains("Unknown action", unknown.Error, StringComparison.Ordinal);
        Assert.False(unknown.Run.Exists);
    }

    [Fact]
    public void ItemAndGrade_MapTheSampleAndProgress()
    {
        var item = this.api.Item(0);
        var missing = this.api.Item(2);
        this.api.Grade(new GradeRequestDto(0, 2));
        var progress = this.api.Grade(new GradeRequestDto(1, 0));

        Assert.Equal((true, "zqquery", "zqcandidate", 2), (item.Exists, item.Query, item.Candidate, item.Progress.Total));
        Assert.False(missing.Exists);
        Assert.True(progress.Complete);
        Assert.Equal(1.0, progress.Kappa);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
```

Expected: build FAILS — CS0246 `EvaluationRunApi` and the DTOs (`EvaluationRunCommandDto`, …) not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs` — apply:

```diff
@@ -1,5 +1,6 @@
 using Cortex.Contained.Agent.Host.Evaluation.Frozen;
 using Cortex.Contained.Agent.Host.Evaluation.Labelling;
+using Cortex.Contained.Agent.Host.Evaluation.Orchestration;
 using Cortex.Contained.Agent.Host.History;
 using Cortex.Contained.Agent.Host.History.Evaluation;
 using Cortex.Contained.Agent.Host.Llm;
@@ -16,7 +17,7 @@ namespace Cortex.Contained.Agent.Host.Evaluation;
 /// <summary>DI registration of the in-process evaluation jobs and workspace files (called from <c>Program.cs</c>).</summary>
 public static class EvaluationRegistration
 {
-    /// <summary>Registers <see cref="EvaluationJobService"/>, <see cref="EvaluationFiles"/> and the labelling options.</summary>
+    /// <summary>Registers <see cref="EvaluationJobService"/>, <see cref="EvaluationFiles"/>, the labelling options and the evaluation run.</summary>
     public static void AddEvaluation(IServiceCollection services, StorageEncryptionContext encryption, IConfiguration configuration)
     {
         ArgumentNullException.ThrowIfNull(services);
@@ -60,6 +61,32 @@ public static class EvaluationRegistration
         services.AddSingleton(sp => new EvaluationFiles(
             Workspaces(sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue, encryption.Layout),
             EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>())));
+        services.AddSingleton(sp => new GradingService(
+            Workspaces(sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue, encryption.Layout),
+            EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>())));
+        services.AddSingleton(sp =>
+        {
+            var labelling = sp.GetRequiredService<IOptionsMonitor<LabellingOptions>>();
+            var reranker = sp.GetRequiredService<RerankerAccess>();
+            var environment = new RunEnvironment(
+                sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue.EvalArtifactsDirectory,
+                MemoryEvalWorkspace.DefaultDirectory,
+                encryption.Layout.EvaluationDirectory,
+                EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>()),
+                () => reranker.Enabled,
+                () => labelling.CurrentValue);
+            return new EvaluationRunService(
+                sp.GetRequiredService<EvaluationJobService>(),
+                sp.GetRequiredService<GradingService>(),
+                environment,
+                sp.GetRequiredService<TimeProvider>(),
+                sp.GetRequiredService<ILogger<EvaluationRunService>>());
+        });
+        services.AddHostedService(sp => sp.GetRequiredService<EvaluationRunService>());
+        services.AddSingleton(sp => new EvaluationRunApi(
+            sp.GetRequiredService<EvaluationRunService>(),
+            sp.GetRequiredService<GradingService>(),
+            () => sp.GetRequiredService<IOptionsMonitor<LabellingOptions>>().CurrentValue));
     }
 
     /// <summary>What <c>label</c> runs with: the configured models through the agent's provider, the main model, the live stores.</summary>
```

`src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/EvaluationRunApi.cs` (new):

```csharp
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>The hub surface of the evaluation run: maps state to DTOs and commands to <see cref="EvaluationRunService"/>.</summary>
public sealed class EvaluationRunApi(EvaluationRunService runs, GradingService grading, Func<Labelling.LabellingOptions> options)
{
    /// <summary>Steps shown in the run's log.</summary>
    public const int LogEntries = 30;

    /// <summary>The run as the Recall page shows it.</summary>
    public EvaluationRunDto Run()
    {
        var state = runs.Snapshot();
        var labelling = options();
        if (state is null)
        {
            return new EvaluationRunDto { Exists = false, QueryModel = labelling.QueryModel, JudgeModel = labelling.JudgeModel };
        }

        GradingProgressDto? progress = null;
        try
        {
            progress = Progress(grading.Progress());
        }
        catch (Storage.StorageLockedException)
        {
            // The sample is sealed and the key has not arrived: progress is shown once it has.
        }

        var current = Array.IndexOf(EvaluationRunPlan.Phases.ToArray(), state.Phase);
        var jobs = state.JobId is null ? null : EvaluationRunPlan.Jobs(state);
        return new EvaluationRunDto
        {
            Exists = true,
            RunId = state.RunId,
            Status = state.Status,
            Phase = state.Phase,
            Waiting = state.Waiting,
            Message = state.Message,
            StartedAt = state.StartedAt,
            UpdatedAt = state.UpdatedAt,
            FinishedAt = state.FinishedAt,
            CurrentJob = jobs is not null && state.Step < jobs.Count ? jobs[state.Step].Tool + " " + jobs[state.Step].Command : null,
            Phases = EvaluationRunPlan.Phases
                .Where(p => p != EvaluationRunPlan.Done)
                .Select((p, i) => new EvaluationRunPhaseDto(p, PhaseState(state, p, i, current)))
                .ToList(),
            Grading = progress,
            Kappa = state.Kappa,
            HistoryKappa = state.HistoryKappa,
            MemoryKappa = state.MemoryKappa,
            Selected = state.Selected,
            RerankConfigs = state.RerankConfigs,
            Version = state.Version,
            FreezeRounds = state.FreezeRounds,
            Leaks = state.Leaks,
            Reports = state.Reports.Keys.Order(StringComparer.Ordinal).ToList(),
            Log = state.Log.TakeLast(LogEntries).Select(e => new EvaluationRunLogDto(e.At, e.Phase, e.Step, e.ExitCode, e.Note)).ToList(),
            QueryModel = labelling.QueryModel,
            JudgeModel = labelling.JudgeModel,
        };
    }

    /// <summary>Carries out <paramref name="command"/>; the result holds the run afterwards.</summary>
    public EvaluationRunCommandResultDto Control(EvaluationRunCommandDto command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var error = command.Action switch
        {
            EvaluationRunActions.Start => runs.Start(command.Fresh),
            EvaluationRunActions.Cancel => runs.Cancel(),
            EvaluationRunActions.Resume => runs.Resume(),
            EvaluationRunActions.ApproveDictionary => runs.ApproveDictionary((command.Terms ?? []).Select(t => new DictionaryTerm(t.Term, t.Kind, t.Entity)).ToList()),
            EvaluationRunActions.Export => runs.Export(command.Passphrase ?? string.Empty),
            _ => $"Unknown action '{command.Action}'.",
        };
        if (error is null && command.Action != EvaluationRunActions.Cancel)
        {
            runs.Tick();
        }

        return new EvaluationRunCommandResultDto { Ok = error is null, Error = error, Run = this.Run() };
    }

    /// <summary>Item <paramref name="index"/> of the κ sample.</summary>
    public GradingItemDto Item(int index)
    {
        var item = grading.Item(index);
        var progress = Progress(grading.Progress());
        return item is null
            ? new GradingItemDto { Exists = false, Index = index, Total = progress.Total, Progress = progress }
            : new GradingItemDto
            {
                Exists = true,
                Index = item.Index,
                Total = item.Total,
                Tool = item.Tool,
                Query = item.Query,
                Candidate = item.Candidate,
                HumanGrade = item.HumanGrade,
                Progress = progress,
            };
    }

    /// <summary>Stores a grade; the run continues on its next tick once every item is graded.</summary>
    public GradingProgressDto Grade(GradeRequestDto grade)
    {
        ArgumentNullException.ThrowIfNull(grade);
        return Progress(grading.Grade(grade.Index, grade.Grade));
    }

    /// <summary>A recorded report, or null.</summary>
    public string? Report(string key) => runs.Report(key);

    private static GradingProgressDto Progress(GradingProgress progress) => new()
    {
        Total = progress.Total,
        Graded = progress.Graded,
        Complete = progress.Complete,
        Kappa = progress.Kappa,
        HistoryKappa = progress.HistoryKappa,
        MemoryKappa = progress.MemoryKappa,
    };

    private static string PhaseState(EvaluationRunState state, string phase, int index, int current)
    {
        var skipped = phase switch
        {
            EvaluationRunPlan.RerankCurve or EvaluationRunPlan.SelectRerank => index < current && !state.Reranker,
            EvaluationRunPlan.GateR => index < current && state.RerankConfigs.Count == 0,
            _ => false,
        };
        return skipped ? "skipped"
            : state.Status == RunStatus.Completed || index < current ? "done"
            : index == current ? "current"
            : "pending";
    }
}
```

`src/Cortex.Contained.Agent.Host/Hubs/AgentHub.EvaluationRun.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary><see cref="IEvaluationHub"/>: the evaluation run and the owner's κ grading.</summary>
public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task<EvaluationRunDto> GetEvaluationRun() => Task.FromResult(this.EvaluationRun.Run());

    /// <inheritdoc />
    public Task<EvaluationRunCommandResultDto> ControlEvaluationRun(EvaluationRunCommandDto command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Task.FromResult(Unlocked(() => this.EvaluationRun.Control(command)));
    }

    /// <inheritdoc />
    public Task<GradingItemDto> GetGradingItem(int index) => Task.FromResult(Unlocked(() => this.EvaluationRun.Item(index)));

    /// <inheritdoc />
    public Task<GradingProgressDto> SubmitGrade(GradeRequestDto grade)
    {
        ArgumentNullException.ThrowIfNull(grade);
        try
        {
            return Task.FromResult(Unlocked(() => this.EvaluationRun.Grade(grade)));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new HubException(ex.Message.Split(" (Parameter", StringSplitOptions.None)[0]);
        }
    }

    /// <inheritdoc />
    public Task<string?> ReadEvaluationRunReport(string key) => Task.FromResult(this.EvaluationRun.Report(key));

    private Evaluation.Orchestration.EvaluationRunApi EvaluationRun =>
        this.evaluationRun ?? throw new HubException("The evaluation run is not available on this agent.");

    private static T Unlocked<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (StorageLockedException)
        {
            throw new HubException("The storage key has not arrived from the Bridge yet.");
        }
    }
}
```

`src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs` — apply:

```diff
@@ -50,6 +50,7 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
     private readonly Cortex.Contained.Agent.Host.Evaluation.EvaluationJobService? evaluationJobs;
     private readonly Cortex.Contained.Agent.Host.Evaluation.EvaluationFiles? evaluationFiles;
     private readonly Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore? frozenCorpora;
+    private readonly Cortex.Contained.Agent.Host.Evaluation.Orchestration.EvaluationRunApi? evaluationRun;
     private readonly Cortex.Contained.Agent.Host.Mcp.McpToolStore mcpToolStore;
     private readonly IHttpClientFactory httpClientFactory;
     private readonly AgentMetrics metrics;
@@ -91,7 +92,8 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
         Storage.StorageEncryptionService? storageEncryption = null,
         Cortex.Contained.Agent.Host.Evaluation.EvaluationJobService? evaluationJobs = null,
         Cortex.Contained.Agent.Host.Evaluation.EvaluationFiles? evaluationFiles = null,
-        Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore? frozenCorpora = null)
+        Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore? frozenCorpora = null,
+        Cortex.Contained.Agent.Host.Evaluation.Orchestration.EvaluationRunApi? evaluationRun = null)
     {
         this.runtime = runtime;
         this.llmClient = llmClient;
@@ -121,6 +123,7 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
         this.evaluationJobs = evaluationJobs;
         this.evaluationFiles = evaluationFiles;
         this.frozenCorpora = frozenCorpora;
+        this.evaluationRun = evaluationRun;
         this.mcpToolStore = mcpToolStore;
         this.httpClientFactory = httpClientFactory;
         this.metrics = metrics;
```

`src/Cortex.Contained.Contracts/Hub/EvaluationRunTypes.cs` (new):

```csharp
namespace Cortex.Contained.Contracts.Hub;

/// <summary>String values of <see cref="EvaluationRunCommandDto.Action"/>.</summary>
public static class EvaluationRunActions
{
    /// <summary>Starts a run (<see cref="EvaluationRunCommandDto.Fresh"/> deletes a previous evaluation's workspace files first).</summary>
    public const string Start = "start";

    public const string Cancel = "cancel";

    /// <summary>Continues a stopped or cancelled run.</summary>
    public const string Resume = "resume";

    /// <summary>Adds <see cref="EvaluationRunCommandDto.Terms"/> to the dictionary and continues with the freeze.</summary>
    public const string ApproveDictionary = "approve-dictionary";

    /// <summary>Exports the frozen version with <see cref="EvaluationRunCommandDto.Passphrase"/>.</summary>
    public const string Export = "export";
}

/// <summary>Body of <see cref="IEvaluationHub.ControlEvaluationRun"/>.</summary>
public sealed record EvaluationRunCommandDto
{
    /// <summary>One of <see cref="EvaluationRunActions"/>.</summary>
    public required string Action { get; init; }

    public bool Fresh { get; init; }

    public IReadOnlyList<DictionaryTermDto>? Terms { get; init; }

    /// <summary>Package passphrase for <see cref="EvaluationRunActions.Export"/>; held for the job only, never stored or echoed.</summary>
    public string? Passphrase { get; init; }
}

/// <summary>A dictionary term the owner accepted or added (person, place, org, handle, other, public-host).</summary>
public sealed record DictionaryTermDto(string Term, string Kind, string? Entity = null);

/// <summary>Outcome of <see cref="IEvaluationHub.ControlEvaluationRun"/>.</summary>
public sealed record EvaluationRunCommandResultDto
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public required EvaluationRunDto Run { get; init; }
}

/// <summary>One phase of the run as the Recall page lists it.</summary>
/// <param name="Name">Phase name (e.g. <c>label-history</c>).</param>
/// <param name="State"><c>done</c>, <c>current</c>, <c>pending</c> or <c>skipped</c>.</param>
public sealed record EvaluationRunPhaseDto(string Name, string State);

/// <summary>One finished step of the run (ids and numbers only).</summary>
public sealed record EvaluationRunLogDto(DateTimeOffset At, string Phase, string Step, int? ExitCode, string Note);

/// <summary>The evaluation run (ids, numbers, phases and report keys only).</summary>
public sealed record EvaluationRunDto
{
    /// <summary>False when no run was ever started.</summary>
    public bool Exists { get; init; }

    public string? RunId { get; init; }

    /// <summary><c>running</c>, <c>waiting</c>, <c>stopped</c>, <c>cancelled</c> or <c>completed</c>.</summary>
    public string? Status { get; init; }

    public string? Phase { get; init; }

    /// <summary><c>grading</c>, <c>dictionary</c> or <c>passphrase</c> while the run waits for the owner.</summary>
    public string? Waiting { get; init; }

    /// <summary>Why the run stopped and what to do.</summary>
    public string? Message { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>The command running now (tool and command), or null.</summary>
    public string? CurrentJob { get; init; }

    public IReadOnlyList<EvaluationRunPhaseDto> Phases { get; init; } = [];

    public GradingProgressDto? Grading { get; init; }

    public double? Kappa { get; init; }

    public double? HistoryKappa { get; init; }

    public double? MemoryKappa { get; init; }

    /// <summary>Holdout configuration chosen on dev, per tool.</summary>
    public IReadOnlyDictionary<string, string> Selected { get; init; } = new Dictionary<string, string>();

    /// <summary>Reranked configuration of Gate R, per tool.</summary>
    public IReadOnlyDictionary<string, string> RerankConfigs { get; init; } = new Dictionary<string, string>();

    public string? Version { get; init; }

    public int FreezeRounds { get; init; }

    /// <summary>Leakage findings of the last refused freeze (location, kind, count).</summary>
    public IReadOnlyList<string> Leaks { get; init; } = [];

    /// <summary>Keys of the reports the run recorded (read with <see cref="IEvaluationHub.ReadEvaluationRunReport"/>).</summary>
    public IReadOnlyList<string> Reports { get; init; } = [];

    /// <summary>The latest steps, newest last.</summary>
    public IReadOnlyList<EvaluationRunLogDto> Log { get; init; } = [];

    public string? QueryModel { get; init; }

    public string? JudgeModel { get; init; }
}

/// <summary>Progress of the owner's hand grading.</summary>
public sealed record GradingProgressDto
{
    public int Total { get; init; }

    public int Graded { get; init; }

    public bool Complete { get; init; }

    public double? Kappa { get; init; }

    public double? HistoryKappa { get; init; }

    public double? MemoryKappa { get; init; }
}

/// <summary>One item of the κ sample (decrypted text: shown once, never cached or logged).</summary>
public sealed record GradingItemDto
{
    public bool Exists { get; init; }

    public int Index { get; init; }

    public int Total { get; init; }

    public string Tool { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public string Candidate { get; init; } = string.Empty;

    public int? HumanGrade { get; init; }

    public required GradingProgressDto Progress { get; init; }
}

/// <summary>Body of <see cref="IEvaluationHub.SubmitGrade"/>: a grade 0–2, or null to clear it.</summary>
public sealed record GradeRequestDto(int Index, int? Grade);
```

`src/Cortex.Contained.Contracts/Hub/IEvaluationHub.cs` — apply:

```diff
@@ -33,6 +33,21 @@ public interface IEvaluationHub
 
     /// <summary>Records that the owner deleted the off-machine package of <paramref name="version"/> from SafeExchange.</summary>
     Task<bool> AcknowledgeOffsiteDeletion(string version);
+
+    /// <summary>The evaluation run ("Run evaluation" on the Recall page): phases, waits, κ, verdict report keys.</summary>
+    Task<EvaluationRunDto> GetEvaluationRun();
+
+    /// <summary>Starts, cancels or resumes the run, approves the dictionary or supplies the export passphrase.</summary>
+    Task<EvaluationRunCommandResultDto> ControlEvaluationRun(EvaluationRunCommandDto command);
+
+    /// <summary>Item <paramref name="index"/> of the owner's κ sample (decrypted query and candidate text, without the judge's grade).</summary>
+    Task<GradingItemDto> GetGradingItem(int index);
+
+    /// <summary>Stores the owner's grade of one κ sample item; κ is computed when the last one is graded.</summary>
+    Task<GradingProgressDto> SubmitGrade(GradeRequestDto grade);
+
+    /// <summary>A report the run recorded (ids and numbers only), or null.</summary>
+    Task<string?> ReadEvaluationRunReport(string key);
 }
 
 /// <summary>String values of <see cref="EvaluationJobDto.State"/>.</summary>
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Agent.Host.Tests -v q
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~EvaluationRunApiTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 4`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs src/Cortex.Contained.Agent.Host/Evaluation/Orchestration/EvaluationRunApi.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.EvaluationRun.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs src/Cortex.Contained.Contracts/Hub/EvaluationRunTypes.cs src/Cortex.Contained.Contracts/Hub/IEvaluationHub.cs tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Orchestration/EvaluationRunApiTests.cs
git commit -m "feat(eval): evaluation run and grading on the agent hub" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 19: Bridge endpoints and grading grants

The Bridge relays: run status and commands (password again, `reauth`), reports, a session-bound grading grant issued for the password, and the per-item grading routes with the grant header, `no-store` and their own rate limit (L11). An expired grant answers 403, not 401 (the web UI logs out on 401).

**Files:**
- Create: `src/Cortex.Contained.Bridge/Evaluation/EvaluationRunEndpoints.cs`
- Create: `src/Cortex.Contained.Bridge/Evaluation/GradingGrants.cs`
- Modify: `src/Cortex.Contained.Bridge/Evaluation/IEvaluationAgentClient.cs`
- Create: `src/Cortex.Contained.Bridge/Hub/HubClient.EvaluationRun.cs`
- Modify: `src/Cortex.Contained.Bridge/Program.cs`
- Create: `tests/Cortex.Contained.Bridge.Tests/Evaluation/EvaluationRunEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Bridge.Tests/Evaluation/EvaluationRunEndpointsTests.cs` (new):

```csharp
using Cortex.Contained.Bridge.Evaluation;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Evaluation;

/// <summary>"Run evaluation": password for the commands, a session-bound grant for the decrypted grading items.</summary>
public sealed class EvaluationRunEndpointsTests
{
    private const string Password = "owner-password";

    private readonly IEvaluationAgentClient agent = Substitute.For<IEvaluationAgentClient>();
    private readonly FakeTimeProvider time = new();

    public EvaluationRunEndpointsTests()
    {
        this.agent.ControlRunAsync(Arg.Any<EvaluationRunCommandDto>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationRunCommandResultDto { Ok = true, Run = new EvaluationRunDto { Exists = true, Status = "running" } });
        this.agent.GetGradingItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new GradingItemDto { Exists = true, Query = "zqquery", Candidate = "zqcandidate", Progress = new GradingProgressDto { Total = 75 } });
    }

    private static bool Verify(string? password) => password == Password;

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    [Fact]
    public void Routes_RequireAuthorization_PasswordRoutesAndGradingRoutesAreRateLimited()
    {
        var app = WebApplication.CreateBuilder().Build();
        app.MapEvaluationRunEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();

        Assert.Equal(10, endpoints.Count);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<IAuthorizeData>()));
        string? Policy(string route) => endpoints.Single(e => e.RoutePattern.RawText == route).Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        Assert.Equal("reauth", Policy("/api/tenants/{tenantId}/eval/run/start"));
        Assert.Equal("reauth", Policy("/api/tenants/{tenantId}/eval/run/export"));
        Assert.Equal("reauth", Policy("/api/tenants/{tenantId}/eval/grading/session"));
        Assert.Equal("eval-grading", Policy("/api/tenants/{tenantId}/eval/grading/items/{index:int}"));
        Assert.Equal("eval-grading", Policy("/api/tenants/{tenantId}/eval/grading/items/{index:int}/grade"));
    }

    [Fact]
    public async Task Control_WrongPassword_ForbiddenAndNothingRelayed()
    {
        var result = await EvaluationRunEndpoints.ControlAsync(this.agent, Verify, EvaluationRunActions.Start, new RunCommandRequest("wrong", Fresh: true), CancellationToken.None);

        Assert.Equal(403, StatusOf(result));
        await this.agent.DidNotReceive().ControlRunAsync(Arg.Any<EvaluationRunCommandDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Control_Export_RelaysPassphraseAndTerms()
    {
        var result = await EvaluationRunEndpoints.ControlAsync(
            this.agent, Verify, EvaluationRunActions.Export, new RunCommandRequest(Password, Passphrase: "correct horse battery", Terms: [new DictionaryTermDto("PersonA", "person")]), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        await this.agent.Received(1).ControlRunAsync(
            Arg.Is<EvaluationRunCommandDto>(c => c.Action == "export" && c.Passphrase == "correct horse battery" && c.Terms!.Single().Term == "PersonA"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Control_RefusedByTheRun_Conflict()
    {
        this.agent.ControlRunAsync(Arg.Any<EvaluationRunCommandDto>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationRunCommandResultDto { Ok = false, Error = "in progress", Run = new EvaluationRunDto() });

        Assert.Equal(409, StatusOf(await EvaluationRunEndpoints.ControlAsync(this.agent, Verify, EvaluationRunActions.Start, new RunCommandRequest(Password), CancellationToken.None)));
    }

    [Fact]
    public void Grants_BoundToTheSessionAndExpireWhenIdle()
    {
        var grants = new GradingGrants(this.time);
        var session = EvaluationRunEndpoints.Session(Verify, grants, "cookie-a", new PasswordBody(Password));
        Assert.Equal(403, StatusOf(EvaluationRunEndpoints.Session(Verify, grants, "cookie-a", new PasswordBody("wrong"))));
        Assert.Equal(200, StatusOf(session));
        var token = grants.Issue("cookie-a");

        Assert.True(grants.Validate(token, "cookie-a"));
        Assert.False(grants.Validate(token, "cookie-b"));
        Assert.False(grants.Validate("forged", "cookie-a"));
        this.time.Advance(TimeSpan.FromMinutes(14));
        Assert.True(grants.Validate(token, "cookie-a"));
        this.time.Advance(GradingGrants.IdleTimeout + TimeSpan.FromSeconds(1));
        Assert.False(grants.Validate(token, "cookie-a"));
    }

    [Fact]
    public async Task Item_WithoutGrant_ForbiddenAndNothingDecrypted()
    {
        Assert.Equal(403, StatusOf(await EvaluationRunEndpoints.ItemAsync(this.agent, granted: false, 0, CancellationToken.None)));
        await this.agent.DidNotReceive().GetGradingItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        Assert.Equal(200, StatusOf(await EvaluationRunEndpoints.ItemAsync(this.agent, granted: true, 0, CancellationToken.None)));
    }

    [Fact]
    public async Task Grade_OutOfRange_BadRequest()
    {
        Assert.Equal(400, StatusOf(await EvaluationRunEndpoints.GradeAsync(this.agent, granted: true, 0, new GradeRequest(3), CancellationToken.None)));
        await this.agent.DidNotReceive().SubmitGradeAsync(Arg.Any<GradeRequestDto>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

```powershell
dotnet build tests/Cortex.Contained.Bridge.Tests -v q
```

Expected: build FAILS — CS0103 `EvaluationRunEndpoints` / CS0246 `GradingGrants` not found.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Bridge/Evaluation/EvaluationRunEndpoints.cs` (new):

```csharp
using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Mvc;

namespace Cortex.Contained.Bridge.Evaluation;

/// <summary>Body of the run commands that need the Bridge password again.</summary>
public sealed record RunCommandRequest(string? Password, bool Fresh = false, IReadOnlyList<DictionaryTermDto>? Terms = null, string? Passphrase = null);

/// <summary>Body of <c>POST …/eval/grading/items/{index}/grade</c>: 0, 1, 2 or null (clear).</summary>
public sealed record GradeRequest(int? Grade);

/// <summary>
/// "Run evaluation" API (all require authorization): <c>/api/tenants/{tenantId}/eval/run</c> (GET),
/// <c>/eval/run/start</c>, <c>/resume</c>, <c>/dictionary</c>, <c>/export</c> (POST, password again, rate limited),
/// <c>/eval/run/cancel</c> (POST), <c>/eval/run/reports/{key}</c> (GET, ids and numbers only),
/// <c>/eval/grading/session</c> (POST, password again: issues a grading grant) and
/// <c>/eval/grading/items/{index}</c> and <c>…/grade</c> (POST with the grant header, rate limited, no-store: the
/// item carries decrypted text). The agent does the work; the Bridge relays.
/// </summary>
internal static class EvaluationRunEndpoints
{
    /// <summary>Maps the endpoints onto <paramref name="app"/>.</summary>
    public static void MapEvaluationRunEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tenants/{tenantId}/eval/run", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            RunAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), ct)).RequireAuthorization();

        foreach (var action in new[] { EvaluationRunActions.Start, EvaluationRunActions.Resume, EvaluationRunActions.Export, "dictionary" })
        {
            var command = action == "dictionary" ? EvaluationRunActions.ApproveDictionary : action;
            app.MapPost($"/api/tenants/{{tenantId}}/eval/run/{action}", (string tenantId, RunCommandRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
                ControlAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), command, body, ct))
                .RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);
        }

        app.MapPost("/api/tenants/{tenantId}/eval/run/cancel", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            ControlAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), _ => true, EvaluationRunActions.Cancel, new RunCommandRequest(null), ct))
            .RequireAuthorization();

        app.MapGet("/api/tenants/{tenantId}/eval/run/reports/{key}", (string tenantId, string key, [FromServices] TenantRouter router, CancellationToken ct) =>
            ReportAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), key, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/eval/grading/session", (PasswordBody body, HttpContext http, [FromServices] SessionManager sessions, [FromServices] GradingGrants grants) =>
            Session(Reauthenticate(sessions, http), grants, SessionCookie(http), body))
            .RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);

        app.MapPost("/api/tenants/{tenantId}/eval/grading/items/{index:int}", async (string tenantId, int index, HttpContext http, [FromServices] TenantRouter router, [FromServices] GradingGrants grants, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await ItemAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Granted(grants, http), index, ct).ConfigureAwait(false);
        }).RequireAuthorization().RequireRateLimiting(GradingRateLimit.PolicyName);

        app.MapPost("/api/tenants/{tenantId}/eval/grading/items/{index:int}/grade", (string tenantId, int index, GradeRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] GradingGrants grants, CancellationToken ct) =>
            GradeAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Granted(grants, http), index, body, ct))
            .RequireAuthorization().RequireRateLimiting(GradingRateLimit.PolicyName);
    }

    internal static Task<IResult> RunAsync(IEvaluationAgentClient? agent, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RecallEndpoints.RelayAsync(() => agent.GetRunAsync(ct), Results.Ok);

    internal static async Task<IResult> ControlAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, string action, RunCommandRequest? body, CancellationToken ct)
    {
        if (!verifyPassword(body?.Password))
        {
            return Results.Json(new { error = "The password is not correct." }, statusCode: 403);
        }

        if (agent is null)
        {
            return NotConnected();
        }

        var command = new EvaluationRunCommandDto { Action = action, Fresh = body?.Fresh ?? false, Terms = body?.Terms, Passphrase = body?.Passphrase };
        return await RecallEndpoints.RelayAsync(
            () => agent.ControlRunAsync(command, ct),
            result => result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 409)).ConfigureAwait(false);
    }

    internal static Task<IResult> ReportAsync(IEvaluationAgentClient? agent, string key, CancellationToken ct) =>
        agent is null
            ? Task.FromResult(NotConnected())
            : RecallEndpoints.RelayAsync(
                () => agent.ReadRunReportAsync(key, ct),
                text => text is null ? Results.Json(new { error = "No such report." }, statusCode: 404) : Results.Text(text, "text/plain; charset=utf-8"));

    internal static IResult Session(Func<string?, bool> verifyPassword, GradingGrants grants, string? session, PasswordBody? body) =>
        verifyPassword(body?.Password)
            ? Results.Ok(new { grant = grants.Issue(session), idleMinutes = GradingGrants.IdleTimeout.TotalMinutes })
            : Results.Json(new { error = "The password is not correct." }, statusCode: 403);

    internal static async Task<IResult> ItemAsync(IEvaluationAgentClient? agent, bool granted, int index, CancellationToken ct)
    {
        if (!granted)
        {
            return GrantExpired();
        }

        return agent is null ? NotConnected() : await RecallEndpoints.RelayAsync(() => agent.GetGradingItemAsync(index, ct), Results.Ok).ConfigureAwait(false);
    }

    internal static async Task<IResult> GradeAsync(IEvaluationAgentClient? agent, bool granted, int index, GradeRequest? body, CancellationToken ct)
    {
        if (!granted)
        {
            return GrantExpired();
        }

        if (body?.Grade is < 0 or > 2)
        {
            return Results.Json(new { error = "Grades are 0, 1 or 2." }, statusCode: 400);
        }

        return agent is null
            ? NotConnected()
            : await RecallEndpoints.RelayAsync(() => agent.SubmitGradeAsync(new GradeRequestDto(index, body?.Grade), ct), Results.Ok).ConfigureAwait(false);
    }

    private static string? SessionCookie(HttpContext http) =>
        http.Request.Cookies.TryGetValue(CortexSessionAuthHandler.CookieName, out var token) ? token : null;

    private static bool Granted(GradingGrants grants, HttpContext http) =>
        grants.Validate(http.Request.Headers[GradingGrants.HeaderName].ToString(), SessionCookie(http));

    // Failed re-authentications are counted per session; the third invalidates it (see SessionManager.VerifyPassword).
    private static Func<string?, bool> Reauthenticate(SessionManager sessions, HttpContext http) =>
        password => sessions.VerifyPassword(password, SessionCookie(http));

    // 403, not 401: a 401 makes the web UI log the owner out.
    private static IResult GrantExpired() => Results.Json(new { error = "The grading session expired: enter the Bridge password again.", grantExpired = true }, statusCode: 403);

    private static IResult NotConnected() => Results.Json(new { error = "Agent not connected" }, statusCode: 503);
}
```

`src/Cortex.Contained.Bridge/Evaluation/GradingGrants.cs` (new):

```csharp
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Cortex.Contained.Bridge.Auth;

namespace Cortex.Contained.Bridge.Evaluation;

/// <summary>
/// Short-lived grading grants: the owner enters the Bridge password once to grade the κ sample, and the grading
/// screen then fetches one decrypted item at a time with the grant instead of the password. A grant is bound to the
/// session cookie that requested it, expires after <see cref="IdleTimeout"/> without use (and after
/// <see cref="MaxLifetime"/> in any case), and lives in memory only.
/// </summary>
public sealed class GradingGrants(TimeProvider time)
{
    /// <summary>A grant unused for this long expires.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>No grant lives longer.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(2);

    /// <summary>Request header carrying the grant.</summary>
    public const string HeaderName = "X-Grading-Grant";

    private readonly ConcurrentDictionary<string, Grant> grants = new(StringComparer.Ordinal);

    /// <summary>A new grant for <paramref name="session"/> (the session cookie value).</summary>
    public string Issue(string? session)
    {
        var now = time.GetUtcNow();
        foreach (var expired in this.grants.Where(g => g.Value.Expired(now)).Select(g => g.Key).ToList())
        {
            this.grants.TryRemove(expired, out _);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        this.grants[token] = new Grant(Hash(session), now, now);
        return token;
    }

    /// <summary>True when <paramref name="token"/> is a live grant of <paramref name="session"/>; extends its idle time.</summary>
    public bool Validate(string? token, string? session)
    {
        if (string.IsNullOrEmpty(token) || !this.grants.TryGetValue(token, out var grant))
        {
            return false;
        }

        var now = time.GetUtcNow();
        if (grant.Expired(now) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(grant.Session), Encoding.ASCII.GetBytes(Hash(session))))
        {
            return false;
        }

        this.grants[token] = grant with { LastUsed = now };
        return true;
    }

    private static string Hash(string? session) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session ?? string.Empty)));

    private sealed record Grant(string Session, DateTimeOffset Issued, DateTimeOffset LastUsed)
    {
        public bool Expired(DateTimeOffset now) => now - this.LastUsed > IdleTimeout || now - this.Issued > MaxLifetime;
    }
}

/// <summary>Rate limit of the grading item endpoints (each returns decrypted text): generous for one person grading, tight for a script.</summary>
internal static class GradingRateLimit
{
    public const string PolicyName = "eval-grading";

    public const int PermitLimit = 60;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimitPartition<string> Partition(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var token = context.Request.Cookies.TryGetValue(CortexSessionAuthHandler.CookieName, out var value) ? value : string.Empty;
        var session = string.IsNullOrEmpty(token) ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{context.Connection.RemoteIpAddress}|{session}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = Window });
    }
}
```

`src/Cortex.Contained.Bridge/Evaluation/IEvaluationAgentClient.cs` — apply:

```diff
@@ -23,6 +23,16 @@ public interface IEvaluationAgentClient
     Task<EvaluationPackageChunkDto> ReadPackageAsync(string name, long offset, int count, CancellationToken cancellationToken);
 
     Task<bool> AcknowledgeOffsiteDeletionAsync(string version, CancellationToken cancellationToken);
+
+    Task<EvaluationRunDto> GetRunAsync(CancellationToken cancellationToken);
+
+    Task<EvaluationRunCommandResultDto> ControlRunAsync(EvaluationRunCommandDto command, CancellationToken cancellationToken);
+
+    Task<GradingItemDto> GetGradingItemAsync(int index, CancellationToken cancellationToken);
+
+    Task<GradingProgressDto> SubmitGradeAsync(GradeRequestDto grade, CancellationToken cancellationToken);
+
+    Task<string?> ReadRunReportAsync(string key, CancellationToken cancellationToken);
 }
 
 /// <summary>Adapts a connected <see cref="HubClient"/> to <see cref="IEvaluationAgentClient"/>.</summary>
@@ -49,4 +59,14 @@ public sealed class HubEvaluationAgentClient(HubClient client) : IEvaluationAgen
         client.ReadEvaluationPackageAsync(name, offset, count, cancellationToken);
 
     public Task<bool> AcknowledgeOffsiteDeletionAsync(string version, CancellationToken cancellationToken) => client.AcknowledgeOffsiteDeletionAsync(version, cancellationToken);
+
+    public Task<EvaluationRunDto> GetRunAsync(CancellationToken cancellationToken) => client.GetEvaluationRunAsync(cancellationToken);
+
+    public Task<EvaluationRunCommandResultDto> ControlRunAsync(EvaluationRunCommandDto command, CancellationToken cancellationToken) => client.ControlEvaluationRunAsync(command, cancellationToken);
+
+    public Task<GradingItemDto> GetGradingItemAsync(int index, CancellationToken cancellationToken) => client.GetGradingItemAsync(index, cancellationToken);
+
+    public Task<GradingProgressDto> SubmitGradeAsync(GradeRequestDto grade, CancellationToken cancellationToken) => client.SubmitGradeAsync(grade, cancellationToken);
+
+    public Task<string?> ReadRunReportAsync(string key, CancellationToken cancellationToken) => client.ReadEvaluationRunReportAsync(key, cancellationToken);
 }
```

`src/Cortex.Contained.Bridge/Hub/HubClient.EvaluationRun.cs` (new):

```csharp
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>The evaluation run and the owner's κ grading in the agent (<see cref="IEvaluationHub"/>).</summary>
public sealed partial class HubClient
{
    /// <summary>The evaluation run.</summary>
    public async Task<EvaluationRunDto> GetEvaluationRunAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationRunDto>(nameof(IAgentHub.GetEvaluationRun), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts, cancels or resumes the run, approves the dictionary or supplies the export passphrase.</summary>
    public async Task<EvaluationRunCommandResultDto> ControlEvaluationRunAsync(EvaluationRunCommandDto command, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationRunCommandResultDto>(nameof(IAgentHub.ControlEvaluationRun), command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One item of the κ sample.</summary>
    public async Task<GradingItemDto> GetGradingItemAsync(int index, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<GradingItemDto>(nameof(IAgentHub.GetGradingItem), index, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the owner's grade of one item.</summary>
    public async Task<GradingProgressDto> SubmitGradeAsync(GradeRequestDto grade, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<GradingProgressDto>(nameof(IAgentHub.SubmitGrade), grade, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A report the run recorded, or null.</summary>
    public async Task<string?> ReadEvaluationRunReportAsync(string key, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<string?>(nameof(IAgentHub.ReadEvaluationRunReport), key, cancellationToken).ConfigureAwait(false);
    }
}
```

`src/Cortex.Contained.Bridge/Program.cs` — apply:

```diff
@@ -151,6 +151,7 @@ builder.Services.AddSingleton<Cortex.Contained.Bridge.RemoteServices.RemoteServi
 
 // --- Authentication ---
 builder.Services.AddSingleton<SessionManager>();
+builder.Services.AddSingleton(new Cortex.Contained.Bridge.Evaluation.GradingGrants(TimeProvider.System));
 builder.Services.AddAuthentication(CortexSessionAuthHandler.SchemeName)
     .AddScheme<AuthenticationSchemeOptions, CortexSessionAuthHandler>(
         CortexSessionAuthHandler.SchemeName, _ => { });
@@ -168,6 +169,7 @@ builder.Services.AddRateLimiter(options =>
                 Window = TimeSpan.FromMinutes(1),
             }));
     options.AddPolicy(ReauthRateLimit.PolicyName, ReauthRateLimit.Partition);
+    options.AddPolicy(Cortex.Contained.Bridge.Evaluation.GradingRateLimit.PolicyName, Cortex.Contained.Bridge.Evaluation.GradingRateLimit.Partition);
     options.OnRejected = async (ctx, ct) =>
     {
         ctx.HttpContext.Response.StatusCode = 429;
@@ -1221,6 +1223,7 @@ Cortex.Contained.Bridge.Storage.StorageEndpoints.MapStorageEndpoints(app, cortex
 
 // --- Evaluation API (jobs run inside the agent process; workspace files; re-authentication for content) ---
 Cortex.Contained.Bridge.Evaluation.EvaluationEndpoints.MapEvaluationEndpoints(app);
+Cortex.Contained.Bridge.Evaluation.EvaluationRunEndpoints.MapEvaluationRunEndpoints(app);
 
 // --- Connector Plugin System endpoint (loopback-only, no WebUI dependency) ---
 app.MapConnectorEndpoint();
```

- [ ] **Step 4: Run the tests to see them pass**

```powershell
dotnet build tests/Cortex.Contained.Bridge.Tests -v q
dotnet test tests/Cortex.Contained.Bridge.Tests --no-build --filter "FullyQualifiedName~EvaluationRunEndpointsTests"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 7`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Bridge/Evaluation/EvaluationRunEndpoints.cs src/Cortex.Contained.Bridge/Evaluation/GradingGrants.cs src/Cortex.Contained.Bridge/Evaluation/IEvaluationAgentClient.cs src/Cortex.Contained.Bridge/Hub/HubClient.EvaluationRun.cs src/Cortex.Contained.Bridge/Program.cs tests/Cortex.Contained.Bridge.Tests/Evaluation/EvaluationRunEndpointsTests.cs
git commit -m "feat(bridge): evaluation run endpoints and session-bound grading grants" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 20: Recall page: Run evaluation, grading, dictionary, export, results

One Alpine component (`evalRunPanel`) in its own file and a card on the Recall page: the button, phase list, stop/resume, the grading screen (keys 0/1/2, →, ←, Esc), the dictionary review, the passphrase form, the results table and the report viewer. All data text via `x-text`.

**Files:**
- Modify: `src/Cortex.Contained.Bridge/wwwroot/app.html`
- Create: `src/Cortex.Contained.Bridge/wwwroot/js/components/eval-run.js`

- [ ] **Step 1: Make the change**

`src/Cortex.Contained.Bridge/wwwroot/app.html` — apply:

```diff
@@ -1570,6 +1570,186 @@
                     </div>
                 </div>
             </div>
+
+            <!-- ═══ Evaluation (Gate H, κ, sanitised copy): one button, three owner inputs ═══ -->
+            <div class="card mb-3" x-data="evalRunPanel()" @keydown.window="onKey($event)">
+                <div class="card-body">
+                    <h6 class="text-uppercase text-secondary border-bottom pb-2 mb-3">Evaluation</h6>
+                    <div x-show="error" class="alert alert-warning py-2 small" x-text="error"></div>
+
+                    <!-- Start -->
+                    <template x-if="run && !active">
+                    <div class="mb-3">
+                        <p class="small text-secondary mb-2">
+                            Runs the whole quality evaluation of history and memory search: writes and judges the test queries
+                            (<span x-text="run.queryModel"></span> writes, <span x-text="run.judgeModel"></span> judges), asks you to grade a
+                            small sample, pre-registers, runs the held-out comparison on raw data and on the sanitised copy, and exports the
+                            encrypted package. It runs for several hours; you can close this page.
+                        </p>
+                        <div class="d-flex flex-wrap gap-2 align-items-center">
+                            <input type="password" class="form-control form-control-sm" style="max-width: 220px;" placeholder="Bridge password" autocomplete="current-password" x-model="start.password">
+                            <div class="form-check mb-0">
+                                <input class="form-check-input" type="checkbox" id="eval-fresh" x-model="start.fresh">
+                                <label class="form-check-label small" for="eval-fresh">Start fresh (deletes the previous evaluation's datasets)</label>
+                            </div>
+                            <button class="btn btn-primary btn-sm" :disabled="busy || !start.password" @click="startRun()">
+                                <i class="bi bi-play-fill me-1"></i>Run evaluation
+                            </button>
+                        </div>
+                    </div>
+                    </template>
+
+                    <!-- Status and phases -->
+                    <template x-if="run && run.exists">
+                    <div>
+                        <div class="d-flex align-items-center gap-2 mb-2">
+                            <span class="badge" :class="{ 'bg-primary': run.status === 'running', 'bg-warning text-dark': run.status === 'waiting', 'bg-danger': run.status === 'stopped', 'bg-secondary': run.status === 'cancelled', 'bg-success': run.status === 'completed' }" x-text="run.status"></span>
+                            <span class="small" x-text="phaseLabel(run.phase)"></span>
+                            <span class="small text-secondary" x-show="run.currentJob" x-text="'· ' + run.currentJob"></span>
+                            <button class="btn btn-outline-danger btn-sm ms-auto" x-show="active || run.status === 'stopped'" :disabled="busy" @click="cancelRun()">Cancel</button>
+                        </div>
+                        <div x-show="run.status === 'stopped' || run.status === 'cancelled'" class="alert alert-danger py-2 small">
+                            <div x-text="run.message || 'The run was cancelled.'"></div>
+                            <div class="d-flex gap-2 mt-2">
+                                <input type="password" class="form-control form-control-sm" style="max-width: 220px;" placeholder="Bridge password" x-model="resume.password">
+                                <button class="btn btn-sm btn-light" :disabled="busy || !resume.password" @click="resumeRun()">Resume</button>
+                            </div>
+                        </div>
+                        <ol class="list-unstyled small mb-3 row row-cols-2 g-0">
+                            <template x-for="p in run.phases" :key="p.name">
+                                <li class="col"><i class="bi me-1" :class="phaseIcon(p.state)"></i><span x-text="phaseLabel(p.name)"></span></li>
+                            </template>
+                        </ol>
+
+                        <!-- Wait: κ grading -->
+                        <div x-show="run.waiting === 'grading'" class="border rounded p-2 mb-3">
+                            <div class="fw-semibold mb-1">Grade the evaluation sample
+                                <span class="text-secondary fw-normal" x-text="'(' + (run.grading?.graded ?? 0) + ' of ' + (run.grading?.total ?? 0) + ' graded)'"></span></div>
+                            <p class="small text-secondary mb-2">For each query and found message or memory: <b>2</b> answers it, <b>1</b> partly or as needed context, <b>0</b> not relevant. The judge's grade is hidden. Keys: 0 1 2, → skip, ← back, Esc close.</p>
+                            <template x-if="!grading.item">
+                                <div class="d-flex gap-2">
+                                    <input type="password" class="form-control form-control-sm" style="max-width: 220px;" placeholder="Bridge password" x-model="grading.password">
+                                    <button class="btn btn-sm btn-primary" :disabled="grading.busy || !grading.password" @click="openGrading()">Start grading</button>
+                                </div>
+                            </template>
+                            <template x-if="grading.item">
+                                <div>
+                                    <div class="small text-secondary mb-1" x-text="'Item ' + (grading.item.index + 1) + ' of ' + grading.item.total + ' · ' + (grading.item.tool === 'memory-eval' ? 'memory' : 'history')"></div>
+                                    <div class="mb-2"><span class="text-secondary small">Query</span><div class="fw-semibold" x-text="grading.item.query"></div></div>
+                                    <div class="mb-2"><span class="text-secondary small">Found</span><div class="border rounded p-2 small" style="white-space: pre-wrap; max-height: 260px; overflow: auto;" x-text="grading.item.candidate"></div></div>
+                                    <div class="d-flex gap-2 align-items-center">
+                                        <template x-for="g in [0, 1, 2]" :key="g">
+                                            <button class="btn btn-sm" :class="grading.item.humanGrade === g ? 'btn-primary' : 'btn-outline-primary'" :disabled="grading.busy" @click="grade(g)" x-text="g"></button>
+                                        </template>
+                                        <button class="btn btn-sm btn-outline-secondary" :disabled="grading.busy || grading.item.index === 0" @click="showItem(grading.item.index - 1)">Back</button>
+                                        <button class="btn btn-sm btn-outline-secondary" :disabled="grading.busy" @click="showItem((grading.item.index + 1) % grading.item.total)">Skip</button>
+                                        <button class="btn btn-sm btn-link ms-auto" @click="closeGrading()">Close</button>
+                                    </div>
+                                </div>
+                            </template>
+                            <div x-show="grading.error" class="text-danger small mt-1" x-text="grading.error"></div>
+                        </div>
+
+                        <!-- Wait: dictionary -->
+                        <div x-show="run.waiting === 'dictionary'" class="border rounded p-2 mb-3">
+                            <div class="fw-semibold mb-1">Approve dictionary terms</div>
+                            <p class="small text-secondary mb-2">Accept the names, places, organisations and handles the sanitised copy must replace; add spellings that are missing.</p>
+                            <div x-show="run.leaks.length > 0" class="alert alert-warning py-1 small">
+                                The last freeze was refused (<span x-text="run.freezeRounds"></span>): personal values remained.
+                                <template x-for="leak in run.leaks" :key="leak"><div x-text="leak"></div></template>
+                            </div>
+                            <div class="d-flex gap-2 mb-2">
+                                <input type="password" class="form-control form-control-sm" style="max-width: 220px;" placeholder="Bridge password" x-model="dict.password">
+                                <button class="btn btn-sm btn-outline-primary" x-show="!dict.loaded" :disabled="dict.busy || !dict.password" @click="loadSuggestions()">Show suggestions</button>
+                            </div>
+                            <template x-if="dict.loaded">
+                                <div>
+                                    <div style="max-height: 300px; overflow: auto;">
+                                        <table class="table table-sm small mb-2">
+                                            <thead><tr><th></th><th>Term</th><th>Kind</th><th>Same person as</th><th>Seen</th></tr></thead>
+                                            <tbody>
+                                                <template x-for="s in dict.suggestions" :key="s.term + s.source">
+                                                    <tr>
+                                                        <td><input type="checkbox" class="form-check-input" x-model="s.accept"></td>
+                                                        <td x-text="s.term"></td>
+                                                        <td><select class="form-select form-select-sm" x-model="s.kind"><template x-for="k in kinds" :key="k"><option :value="k" x-text="k"></option></template></select></td>
+                                                        <td><input class="form-control form-control-sm" x-model="s.entity" placeholder="optional"></td>
+                                                        <td x-text="s.count"></td>
+                                                    </tr>
+                                                </template>
+                                            </tbody>
+                                        </table>
+                                    </div>
+                                    <div class="d-flex gap-2 mb-2">
+                                        <input class="form-control form-control-sm" placeholder="Add a term" x-model="dict.draft.term" @keydown.enter.prevent="addTerm()">
+                                        <select class="form-select form-select-sm" style="max-width: 140px;" x-model="dict.draft.kind"><template x-for="k in kinds" :key="k"><option :value="k" x-text="k"></option></template></select>
+                                        <input class="form-control form-control-sm" placeholder="same person as (optional)" x-model="dict.draft.entity">
+                                        <button class="btn btn-sm btn-outline-secondary" @click="addTerm()">Add</button>
+                                    </div>
+                                    <div class="small text-secondary mb-2" x-show="dict.extra.length > 0" x-text="'Added: ' + dict.extra.map(e => e.term).join(', ')"></div>
+                                    <button class="btn btn-sm btn-primary" :disabled="busy || !dict.password" @click="approveDictionary()" x-text="'Approve ' + acceptedTerms.length + ' terms and freeze'"></button>
+                                </div>
+                            </template>
+                        </div>
+
+                        <!-- Wait: passphrase -->
+                        <div x-show="run.waiting === 'passphrase'" class="border rounded p-2 mb-3">
+                            <div class="fw-semibold mb-1">Package passphrase</div>
+                            <p class="small text-secondary mb-2">The frozen copy <span x-text="run.version"></span> is exported encrypted with this passphrase (at least 12 characters, not the recovery kit's). Keep it with your offline secrets; it is not stored.</p>
+                            <div class="d-flex flex-wrap gap-2">
+                                <input type="password" class="form-control form-control-sm" style="max-width: 200px;" placeholder="Bridge password" x-model="exp.password">
+                                <input type="password" class="form-control form-control-sm" style="max-width: 200px;" placeholder="Package passphrase" autocomplete="new-password" x-model="exp.passphrase">
+                                <input type="password" class="form-control form-control-sm" style="max-width: 200px;" placeholder="Repeat passphrase" autocomplete="new-password" x-model="exp.confirm">
+                                <button class="btn btn-sm btn-primary" :disabled="busy || !exp.password || passphraseProblem" @click="exportPackage()">Export</button>
+                            </div>
+                            <div class="small text-secondary mt-1" x-show="exp.passphrase && passphraseProblem" x-text="passphraseProblem"></div>
+                        </div>
+
+                        <!-- Results -->
+                        <template x-if="summary">
+                        <div class="border rounded p-2 mb-3">
+                            <div class="fw-semibold mb-2">Results <span class="text-secondary fw-normal small" x-text="'(frozen ' + (summary.version || '—') + ', κ ' + number(summary.kappa?.all) + ' on ' + summary.kappa?.items + ' items)'"></span></div>
+                            <table class="table table-sm small mb-2">
+                                <thead><tr><th></th><th>Chosen</th><th>Δ nDCG@10 raw [95% CI]</th><th>Gate H (non-inferior)</th><th>Δ sanitised</th><th>raw − sanitised</th><th>Gate R</th></tr></thead>
+                                <tbody>
+                                    <template x-for="tool in ['history', 'memory']" :key="tool">
+                                        <tr>
+                                            <td x-text="tool"></td>
+                                            <td x-text="summary[tool]?.raw?.config || '—'"></td>
+                                            <td x-text="delta(summary[tool]?.raw)"></td>
+                                            <td><span class="badge" :class="summary[tool]?.raw?.nonInferior ? 'bg-success' : 'bg-danger'" x-text="summary[tool]?.raw ? (summary[tool].raw.nonInferior ? 'pass' : 'fail') : '—'"></span></td>
+                                            <td x-text="delta(summary[tool]?.sanitised)"></td>
+                                            <td x-text="difference(summary[tool]?.raw, summary[tool]?.sanitised)"></td>
+                                            <td x-text="summary[tool]?.gateR ? (summary[tool].gateR.passed ? 'pass' : 'fail') : 'not run'"></td>
+                                        </tr>
+                                    </template>
+                                </tbody>
+                            </table>
+                            <p class="small text-secondary mb-1">Gate H is your decision; a run on a mostly pre-event-log history is provisional.</p>
+                        </div>
+                        </template>
+                        <div class="small" x-show="run.reports.length > 0">
+                            <span class="text-secondary">Reports:</span>
+                            <template x-for="key in run.reports" :key="key">
+                                <a href="#" class="me-2" @click.prevent="openReport(key)" x-text="key"></a>
+                            </template>
+                        </div>
+                        <template x-if="report.key">
+                            <div class="mt-2">
+                                <div class="d-flex"><span class="small fw-semibold" x-text="report.key"></span><button class="btn-close ms-auto" @click="report = { key: '', text: '' }"></button></div>
+                                <pre class="small border rounded p-2 mb-0" style="max-height: 360px; overflow: auto;" x-text="report.text"></pre>
+                            </div>
+                        </template>
+                        <details class="small mt-2" x-show="run.log.length > 0">
+                            <summary class="text-secondary">Steps</summary>
+                            <template x-for="(entry, i) in run.log" :key="i">
+                                <div><span class="text-secondary" x-text="formatDateTime(entry.at)"></span> <span x-text="entry.step"></span> <span class="text-secondary" x-text="entry.exitCode === null ? '' : '(exit ' + entry.exitCode + ')'"></span> <span x-text="entry.note"></span></div>
+                            </template>
+                        </details>
+                    </div>
+                    </template>
+                </div>
+            </div>
         </div>
         </template>
 
@@ -3146,6 +3326,7 @@
 <script src="/js/components/layout.js"></script>
 <script src="/js/components/channel-helpers.js"></script>
 <script src="/js/components/recall-purge.js"></script>
+<script src="/js/components/eval-run.js"></script>
 <script src="/js/components/restart-modal.js"></script>
 
 <!-- Page scripts (Alpine x-data functions) -->
```

`src/Cortex.Contained.Bridge/wwwroot/js/components/eval-run.js` (new):

```javascript
"use strict";

/**
 * "Run evaluation" on the Recall page. One button starts the automated evaluation (labelling, κ, dev ablations,
 * pre-registration, holdout, sanitised copy, comparison, export); the owner only enters the Bridge password, grades
 * the κ sample, approves dictionary terms and gives the package passphrase. Text from the data (queries, candidates,
 * suggested terms) is rendered with x-text only, fetched one item at a time and never cached or logged.
 */
const evalRunPhaseLabels = {
    "prepare": "Prepare workspaces",
    "label-history": "Write and judge history queries",
    "label-memory": "Write and judge memory queries",
    "validate": "Validate datasets",
    "guard": "Paraphrase guard",
    "grading": "Your κ grading",
    "kappa": "LLM-vs-human κ",
    "plan": "Pre-registered analysis plan",
    "ablate-dev": "Dev ablations",
    "select": "Choose configurations (dev only)",
    "rerank-curve": "Rerank curve (dev)",
    "select-rerank": "Choose rerank budget",
    "register": "Register (pre-registration)",
    "holdout-raw": "Holdout on raw data (Gate H)",
    "gate-r": "Gate R",
    "suggest": "Suggest dictionary terms",
    "dictionary": "Your dictionary approval",
    "freeze": "Freeze sanitised copy",
    "holdout-frozen": "Holdout on the sanitised copy",
    "compare": "Raw vs sanitised comparison",
    "passphrase": "Your package passphrase",
    "export": "Export package",
    "summary": "Summary",
};

const evalRunKinds = ["person", "place", "org", "handle", "other", "public-host"];

function evalRunPanel() {
    return {
        get tenantId() { return Alpine.store("router").tenantId; },
        get base() { return `/api/tenants/${encodeURIComponent(this.tenantId)}/eval`; },

        run: null,
        error: "",
        busy: false,
        timer: null,
        kinds: evalRunKinds,
        start: { password: "", fresh: false },
        resume: { password: "" },
        grading: { password: "", grant: "", item: null, busy: false, error: "" },
        dict: { password: "", suggestions: [], extra: [], draft: { term: "", kind: "person", entity: "" }, loaded: false, busy: false },
        exp: { password: "", passphrase: "", confirm: "" },
        summary: null,
        report: { key: "", text: "" },

        async init() {
            await this.load();
            this.timer = setInterval(() => this.load(), 5000);
        },

        destroy() {
            if (this.timer) clearInterval(this.timer);
            this.forget();
        },

        async load() {
            try {
                this.run = await api.get(`${this.base}/run`);
                this.error = "";
                if (this.run?.reports?.includes("summary") && !this.summary) await this.loadSummary();
            } catch (e) {
                this.error = e.message;
            }
        },

        phaseLabel(name) { return evalRunPhaseLabels[name] || name; },

        phaseIcon(state) {
            return state === "done" ? "bi-check-circle-fill text-success"
                : state === "current" ? "bi-arrow-right-circle-fill text-primary"
                : state === "skipped" ? "bi-dash-circle text-secondary"
                : "bi-circle text-secondary";
        },

        get active() { return this.run?.status === "running" || this.run?.status === "waiting"; },

        async control(action, body, done) {
            this.busy = true;
            try {
                const result = await api.post(`${this.base}/run/${action}`, body);
                this.run = result.run;
                if (done) Alpine.store("toast").success(done);
                return true;
            } catch (e) {
                if (e.body?.run) this.run = e.body.run;
                Alpine.store("toast").error(e.body?.error || e.message);
                return false;
            } finally {
                this.busy = false;
            }
        },

        async startRun() {
            if (this.start.fresh && !confirm("Delete the previous evaluation's datasets, registrations and runs (frozen versions and the dictionary stay)?")) return;
            this.summary = null;
            await this.control("start", { password: this.start.password, fresh: this.start.fresh }, "Evaluation started");
            this.start.password = "";
        },

        async cancelRun() {
            if (!confirm("Cancel the evaluation run? Its progress is kept; Resume continues it.")) return;
            await this.control("cancel", {}, "Evaluation cancelled");
        },

        async resumeRun() {
            await this.control("resume", { password: this.resume.password }, "Evaluation resumed");
            this.resume.password = "";
        },

        // ── κ grading ───────────────────────────────────────────
        async gradingCall(path, body) {
            const resp = await fetch(`${this.base}/grading/${path}`, {
                method: "POST",
                headers: { "Content-Type": "application/json", "X-Grading-Grant": this.grading.grant },
                body: JSON.stringify(body ?? {}),
                cache: "no-store",
            });
            const data = await resp.json().catch(() => ({}));
            if (!resp.ok) {
                if (data.grantExpired) this.grading.grant = "";
                throw new Error(data.error || `HTTP ${resp.status}`);
            }
            return data;
        },

        async openGrading() {
            this.grading.busy = true;
            this.grading.error = "";
            try {
                const session = await api.post(`${this.base}/grading/session`, { password: this.grading.password });
                this.grading.grant = session.grant;
                this.grading.password = "";
                await this.showItem(Math.min(this.run?.grading?.graded ?? 0, Math.max(0, (this.run?.grading?.total ?? 1) - 1)));
            } catch (e) {
                this.grading.error = e.body?.error || e.message;
            }
            this.grading.busy = false;
        },

        async showItem(index) {
            if (index < 0) return;
            this.grading.busy = true;
            try {
                const item = await this.gradingCall(`items/${index}`);
                this.grading.item = item.exists ? item : null;
                this.grading.error = "";
            } catch (e) {
                this.grading.item = null;
                this.grading.error = e.message;
            }
            this.grading.busy = false;
        },

        async grade(value) {
            const item = this.grading.item;
            if (!item || this.grading.busy) return;
            this.grading.busy = true;
            try {
                const progress = await this.gradingCall(`items/${item.index}/grade`, { grade: value });
                if (this.run) this.run.grading = progress;
                this.grading.busy = false;
                if (progress.complete) {
                    this.closeGrading();
                    Alpine.store("toast").success("Grading complete: the run continues with κ");
                    await this.load();
                } else {
                    await this.showItem(item.index + 1 < item.total ? item.index + 1 : 0);
                }
            } catch (e) {
                this.grading.error = e.message;
                this.grading.busy = false;
            }
        },

        closeGrading() {
            this.grading.item = null;
            this.grading.grant = "";
        },

        onKey(event) {
            const item = this.grading.item;
            if (!item || ["INPUT", "TEXTAREA", "SELECT"].includes(event.target?.tagName)) return;
            if (["0", "1", "2"].includes(event.key)) { event.preventDefault(); this.grade(Number(event.key)); }
            else if (event.key === "ArrowRight" || event.key === "s") { event.preventDefault(); this.showItem((item.index + 1) % item.total); }
            else if (event.key === "ArrowLeft" || event.key === "b") { event.preventDefault(); this.showItem(item.index - 1); }
            else if (event.key === "Escape") { this.closeGrading(); }
        },

        // ── Dictionary ──────────────────────────────────────────
        async loadSuggestions() {
            this.dict.busy = true;
            try {
                const file = await api.post(`${this.base}/files/read`, { password: this.dict.password, tool: "eval-corpus", name: "dictionary.suggested.jsonl" });
                this.dict.suggestions = (file.content || "").split("\n").filter((l) => l.trim()).map((l) => {
                    const s = JSON.parse(l);
                    return { term: s.term, kind: evalRunKinds.includes(s.kind) ? s.kind : "person", count: s.count, source: s.source, accept: false, entity: "" };
                });
                this.dict.loaded = true;
            } catch (e) {
                Alpine.store("toast").error("Suggestions: " + (e.body?.error || e.message));
            }
            this.dict.busy = false;
        },

        addTerm() {
            const term = this.dict.draft.term.trim();
            if (!term) return;
            this.dict.extra.push({ term, kind: this.dict.draft.kind, entity: this.dict.draft.entity.trim() });
            this.dict.draft = { term: "", kind: this.dict.draft.kind, entity: "" };
        },

        get acceptedTerms() {
            return this.dict.suggestions.filter((s) => s.accept).concat(this.dict.extra)
                .map((s) => ({ term: s.term, kind: s.kind, entity: s.entity || null }));
        },

        async approveDictionary() {
            const ok = await this.control("dictionary", { password: this.dict.password, terms: this.acceptedTerms },
                `${this.acceptedTerms.length} terms added; freezing the sanitised copy`);
            if (ok) this.dict = { password: "", suggestions: [], extra: [], draft: { term: "", kind: "person", entity: "" }, loaded: false, busy: false };
        },

        // ── Export ──────────────────────────────────────────────
        get passphraseProblem() {
            if (this.exp.passphrase.length < 12) return "At least 12 characters.";
            if (this.exp.passphrase !== this.exp.confirm) return "The two entries differ.";
            return "";
        },

        async exportPackage() {
            if (this.passphraseProblem) return;
            await this.control("export", { password: this.exp.password, passphrase: this.exp.passphrase }, "Exporting the package");
            this.exp = { password: "", passphrase: "", confirm: "" };
        },

        // ── Results ─────────────────────────────────────────────
        async fetchReport(key) {
            const resp = await fetch(`${this.base}/run/reports/${encodeURIComponent(key)}`, { cache: "no-store" });
            if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
            return resp.text();
        },

        async loadSummary() {
            try {
                this.summary = JSON.parse(await this.fetchReport("summary"));
            } catch {
                this.summary = null;
            }
        },

        async openReport(key) {
            try {
                this.report = { key, text: await this.fetchReport(key) };
            } catch (e) {
                Alpine.store("toast").error("Report: " + e.message);
            }
        },

        delta(v) {
            if (!v) return "—";
            const f = (x) => (x >= 0 ? "+" : "") + Number(x).toFixed(3);
            return `${f(v.mean)} [${f(v.low)}, ${f(v.high)}]`;
        },

        difference(raw, sanitised) {
            return raw && sanitised ? ((raw.mean - sanitised.mean >= 0 ? "+" : "") + (raw.mean - sanitised.mean).toFixed(3)) : "—";
        },

        number(x, digits = 3) { return x === null || x === undefined ? "—" : Number(x).toFixed(digits); },

        forget() {
            this.start.password = "";
            this.resume.password = "";
            this.closeGrading();
            this.dict.password = "";
            this.exp = { password: "", passphrase: "", confirm: "" };
        },
    };
}
```

- [ ] **Step 2: Check it**

```powershell
dotnet build tests/Cortex.Contained.Bridge.Tests -v q
node -e "new Function(require('fs').readFileSync('src/Cortex.Contained.Bridge/wwwroot/js/components/eval-run.js','utf8')); console.log('ok')"
```

Expected: `0 Warning(s)`, `0 Error(s)`; `ok`. The replay also checked the card with Alpine in a headless browser against a stubbed API (grading: password → item 1 → key `2` → item 2 with the grant header; dictionary: suggestions load, tick + add → `terms` sent; results table renders verdicts, CIs and the raw − sanitised difference). After deploying, open the Recall page and check the *Evaluation* card shows *Run evaluation*.

- [ ] **Step 3: Commit**

```powershell
git add src/Cortex.Contained.Bridge/wwwroot/app.html src/Cortex.Contained.Bridge/wwwroot/js/components/eval-run.js
git commit -m "feat(ui): Run evaluation, grading, dictionary approval, export and results on the Recall page" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 21: Documentation

The runbook's owner steps become five bullets with what runs automatically, the settings, cost, stop reasons and privacy; the manual commands stay as "Advanced"; the API reference and architecture list the new routes and files.

**Files:**
- Modify: `docs/api-reference.md`
- Modify: `docs/architecture.md`
- Modify: `docs/evaluation-runbook.md`

- [ ] **Step 1: Make the change**

`docs/api-reference.md` — apply:

```diff
@@ -67,6 +67,16 @@
 | GET | `/api/tenants/{tenantId}/eval/frozen` | Yes | `{ versions[], packages[], pendingOffsiteDeletions[] }` (ids, counts, file names) |
 | POST | `/api/tenants/{tenantId}/eval/frozen/packages/{name}` | Yes + password | Body `{ password }`. Streams a passphrase-encrypted `*.cxpkg` package (`application/octet-stream`, `Cache-Control: no-store`); `404` unknown, `400` not a package name. Rate limited |
 | POST | `/api/tenants/{tenantId}/eval/frozen/{version}/offsite-deleted` | Yes + password | Body `{ password }`. Records that the owner deleted the version's attachment from SafeExchange; `404` when none is pending. Rate limited |
+| GET | `/api/tenants/{tenantId}/eval/run` | Yes | The evaluation run: `{ exists, runId, status, phase, waiting, message, currentJob, phases[{name,state}], grading{total,graded,complete,kappa,historyKappa,memoryKappa}, kappa, selected, rerankConfigs, version, freezeRounds, leaks[], reports[], log[], queryModel, judgeModel }` (`status`: `running`, `waiting`, `stopped`, `cancelled`, `completed`; `waiting`: `grading`, `dictionary`, `passphrase`). Ids and numbers only |
+| POST | `/api/tenants/{tenantId}/eval/run/start` | Yes + password | Body `{ password, fresh }`. Starts the run (`fresh` deletes a previous evaluation's workspace files first). `409` with `{ ok, error, run }` when refused. Rate limited (`reauth`) |
+| POST | `/api/tenants/{tenantId}/eval/run/resume` | Yes + password | Body `{ password }`. Continues a stopped or cancelled run. Rate limited |
+| POST | `/api/tenants/{tenantId}/eval/run/dictionary` | Yes + password | Body `{ password, terms: [{ term, kind, entity? }] }`. Adds the accepted terms to the sealed dictionary and continues with the freeze. Rate limited |
+| POST | `/api/tenants/{tenantId}/eval/run/export` | Yes + password | Body `{ password, passphrase }` (≥ 12 characters; held for the export job only). Rate limited |
+| POST | `/api/tenants/{tenantId}/eval/run/cancel` | Yes | Cancels the run and its running job (progress is kept) |
+| GET | `/api/tenants/{tenantId}/eval/run/reports/{key}` | Yes | A report the run recorded (`summary`, `history-eval:holdout`, `memory-eval:compare`, …) as text; ids and numbers only; `404` unknown |
+| POST | `/api/tenants/{tenantId}/eval/grading/session` | Yes + password | Body `{ password }`. `{ grant, idleMinutes }`: a grading grant bound to the session cookie (15 min idle, 2 h at most, memory only). Rate limited |
+| POST | `/api/tenants/{tenantId}/eval/grading/items/{index}` | Yes + `X-Grading-Grant` | One item of the κ sample: `{ exists, index, total, tool, query, candidate, humanGrade, progress }` (decrypted text, `Cache-Control: no-store`; the judge's grade is not included). `403` when the grant expired. Rate limited (`eval-grading`, 60/min) |
+| POST | `/api/tenants/{tenantId}/eval/grading/items/{index}/grade` | Yes + `X-Grading-Grant` | Body `{ grade }` (0, 1, 2 or null to clear). Returns the progress; κ once every item is graded. Rate limited (`eval-grading`) |
 | POST | `/api/channels/{type}` | Yes | Update channel configuration |
 | GET | `/api/mcp/actions` | Yes | List approval-gated MCP mutation actions (filter by `serverKey`, `toolName`, `state`, `workerId`; paged with `before`/`limit`) |
 | GET | `/api/mcp/actions/{actionId}` | Yes | One action, including its canonical arguments (for review) and result |
```

`docs/architecture.md` — apply:

```diff
@@ -244,10 +244,11 @@ SQLite-backed (`/app/state/scheduler/tasks.db`). Tick interval: 15 seconds. Recu
 | Messages | `/app/state/messages.db` | SQLite (WAL, `secure_delete`; SQLite3MC-encrypted with `Storage:EncryptAtRest`), incl. `Events`, `HistoryJournal`, `Meta`, memory-quarantine tables |
 | History index | `/app/state/history/history-index.<gen>.db` + `history-index.current` | Contentless FTS5 plus chunk vectors (derived, no message text; vectors erased and encrypted with the generation) |
 | Smoke-eval workspace | `/app/state/eval/smoke` | Local only, never committed; `runs` destroyed by overlapping purges |
-| History-eval workspace | `/app/state/eval/hybrid` | Local only, never committed; `runs` (corpus copies, encrypted with the evaluation key on encrypted installs) destroyed by overlapping purges; `dataset.jsonl` sealed |
+| History-eval workspace | `/app/state/eval/hybrid` | Local only, never committed; `runs` (corpus copies, encrypted with the evaluation key on encrypted installs) destroyed by overlapping purges; `dataset.jsonl`, `label-state.json` and `kappa-sample.json` sealed (memory-eval likewise) |
 | Memory-eval workspace | `/app/state/eval/memory` | Local only; the store copy of each command is deleted when it ends; `dataset.jsonl` and `seeds.jsonl` sealed on encrypted installs |
 | Frozen evaluation corpora | `/app/state/eval/frozen/vN` | Sanitised history and memories, datasets, plans, registrations (`manifest.json` with hashes); evaluation key; destroyed by overlapping purges (`offsite-deletions.json` lists exported ones) |
 | Evaluation packages | `/app/state/eval/packages/*.cxpkg` | Passphrase-encrypted exports of frozen corpora (off-machine backup in SafeExchange) |
+| Evaluation run | `/app/state/eval/run/run.json`, `summary.json` | The one-button evaluation's state machine and result summary (phases, ids, numbers, report paths; no text, no passphrase) |
 | Memories | `/app/state/memory/memory.db` | SQLite + sqlite-vec + FTS5; SQLite3MC-encrypted with encryption at rest |
 | Memory content | `/app/state/memory/memories/*.memory.data` | Text files; AES-256-GCM with encryption at rest |
 | Session snapshot | `/app/state/sessions-snapshot.json` | Live sessions saved at shutdown; AES-256-GCM with encryption at rest |
```

`docs/evaluation-runbook.md` — apply:

```diff
@@ -1,6 +1,6 @@
 # Evaluation runbook (Gate H, Gate R, sanitised corpus)
 
-This is the owner's step-by-step guide for the quality gates of episodic history and curated memory on an install
+This is the owner's guide for the quality gates of episodic history and curated memory on an install
 with **encryption at rest** (it works the same without encryption). Design:
 [encrypted evaluation and sanitised corpus](superpowers/specs/2026-11-23-encrypted-eval-sanitised-corpus-design.md).
 
@@ -21,10 +21,70 @@ files in the container; read them with `docker exec cortex-agent cat <path print
 > Never paste dataset text, pools or seeds into chats, issues or commits. Files you `-Pull` to disk are plaintext:
 > keep them under your user profile and delete them when you are done.
 
-## 1. History search — Gate H
+## Run the evaluation (one button)
+
+Design: [automated labelling and one-click evaluation](superpowers/specs/2026-11-23-automated-labelling-design.md).
+Your part is five steps; everything else runs inside the agent, resumable across restarts:
+
+1. **Recall page → Evaluation → Bridge password → Run evaluation.** Tick *Start fresh* if an earlier evaluation
+   exists (its datasets, registrations and runs are deleted; frozen versions and the dictionary stay).
+2. **Grade the evaluation sample** when the page asks: enter the password once, then grade each item blind — **2**
+   answers the query, **1** partly or as needed context, **0** not relevant (keys `0` `1` `2`, `→` skip, `←` back).
+   75 items by default.
+3. **Approve dictionary terms** when asked: tick the names, places, organisations and handles to replace, add missing
+   spellings (*same person as* groups nicknames), *Approve and freeze*. If the leakage check still finds personal
+   values, the page lists where (location, kind, count) and asks again.
+4. **Package passphrase** when asked: at least 12 characters, twice (not the recovery kit's), *Export*. Then download the
+   package and attach it to SafeExchange as in A6, steps 2–5.
+5. **Read the results**: per tool the chosen configuration, Δ nDCG@10 with its 95 % CI, the Gate H verdict
+   (non-inferior or not), the sanitised-copy result and the raw − sanitised difference, κ, Gate R when it ran, and the
+   reports. Gate H is your decision.
+
+What runs automatically, in order: workspaces → `label` (history, then memory: stratified samples of your messages and
+memories plus your real past recall requests as the co-primary stratum; `Evaluation:Labelling:QueryModel` writes each
+query and its oracle filters without copying the wording — paraphrase guard and copy check enforced, rewritten on
+failure; the agent's main model writes its own version of the search; the union of the top 10 of every configuration
+plus the source is pooled and `JudgeModel` grades it 0–2 in batches) → `validate` → `guard` → *your grading* → κ gate
+(≥ 0.6 over the whole sample) → the analysis plan with the selection rule is written → `ablate --split dev` → the
+configuration is chosen by that rule on dev only (`holdout` in `ablation.json`; with the reranker on, the rerank curve
+and a Gate R configuration too) → `register` → `holdout` (raw) → `gate-r` → `eval-corpus suggest --llm` → *your
+dictionary approval* → `freeze` → `holdout --corpus frozen:vN` → `compare` → *your passphrase* → `export` → summary.
+
+| Setting (`Evaluation:Labelling:…`) | Default | |
+|---|---|---|
+| `QueryModel` / `JudgeModel` | `gpt-6.1-sol` / `claude-opus-5.5` | Different model families are required; both must be listed by a configured provider (the Bridge's `llmProviders`) |
+| `HistoryQueries` / `MemoryQueries` | 520 / 270 | Drafted; a few are dropped after judging (Gate H for memory needs ≥ 250) |
+| `RealShare` | 0.3 | Largest share of real recall requests |
+| `KappaSample` | 75 | Items you grade, split by query count (50 history, 25 memory) |
+| `PoolDepth`, `JudgeBatchSize`, `MaxCandidatesPerQuery` | 10, 10, 60 | judged@10 coverage and judge batching |
+| `MaxCalls`, `MinCallIntervalMs`, `MaxAttempts` | 5000, 500, 4 | Hard cap per label job, pacing, retries with backoff |
+
+Set them like any agent option (environment `Evaluation__Labelling__MaxCalls=8000`, or the agent's configuration file).
+`… -Tool history-eval -- label --estimate` prints the expected calls and hours. With the defaults a full run makes about 4,200 LLM
+calls (≈ 1,030 query-writer, ≈ 550 main-model and ≈ 2,600 judge calls; more judge calls when the pools are larger, up to the cap)
+and takes about 13–14 hours of LLM time plus the embedding runs: start it in the evening.
+
+**When it stops** the page says why and what to do; *Resume* (password) continues from the same step:
+- *κ below 0.6*: the judge disagrees with you too often; look at a few items, then start fresh with another
+  `JudgeModel` (or correct your grades and Resume).
+- *MaxCalls reached*: raise the cap and Resume; the label job continues where it stopped (its checkpoint is sealed).
+- *A command failed*: the last lines of its output (ids and numbers) are shown; fix the cause and Resume.
+- After an agent restart the run continues by itself once the storage key has arrived; an interrupted command runs again.
+  Only the passphrase must be entered again.
+
+Privacy: snippets, queries and candidates go only to the configured provider (D5), as in normal chat. Everything with
+text on disk is sealed (`label-state.json`, `dataset.jsonl`, `kappa-sample.json`, the dictionary); the run state,
+reports and logs hold ids and numbers only. Grading items are decrypted one at a time for a grading grant bound to your
+session (15 minutes idle).
+
+## Advanced: commands one by one
+
+The run above chains these commands. Run them yourself to repeat one step or to label by hand.
+
+### A1. History search — Gate H
 
 1. **Workspace:** `.\scripts\Invoke-CortexEval.ps1 -Tool history-eval -- init`
-2. **Queries and labels** (§4.2 protocol): write `dataset.jsonl` with your assistant —
+2. **Queries and labels** (§4.2 protocol): `… -Tool history-eval -- label` writes and judges it automatically (see above); or write `dataset.jsonl` with your assistant —
    `.\scripts\Edit-CortexEvalFile.ps1 -Tool history-eval -Name dataset.jsonl -Pull -Path .\work\history-dataset.jsonl`,
    label (graded 0–2 per message, query and judge model families differ), then
    `-Push -Path .\work\history-dataset.jsonl`. Delete the local copy afterwards.
@@ -38,13 +98,13 @@ files in the container; read them with `docker exec cortex-agent cat <path print
 8. **Reranker (Gate R, optional):** `… -- rerank-curve --condition normal` (also `embedder-busy`, `cold`), then
    `… -- gate-r --config <name>`. The Bridge must have the reranker switched on (Recall page).
 
-## 2. Curated memory — Gate H for memory
+### A2. Curated memory — Gate H for memory
 
 Same steps with `-Tool memory-eval`: `init`, `sample --count 300` (writes sealed `seeds.jsonl`; pull it to write
 queries, delete the pulled copy), write `dataset.jsonl` (≥ 250 queries), `pool` (ids only, for the judge), labels,
 `validate`, `guard`, `kappa`, `ablate --split dev`, `register`, `holdout`, optionally `rerank-curve` / `gate-r`.
 
-## 3. The sanitiser dictionary
+### A3. The sanitiser dictionary
 
 The sanitised copy replaces names, places, organisations and handles that are listed in the dictionary, plus every
 e-mail, phone number, CPR number, IBAN, card number, GPS position, URL path, non-public URL host and number with
@@ -75,7 +135,7 @@ e-mail, phone number, CPR number, IBAN, card number, GPS position, URL path, non
    Other stem changes (`Лев` → `Льва`, `Любовь` → `Любви`) and irregular forms are not found: add them as their own
    lines with the same `entity`. The leakage check uses the same rules, so a form the sanitiser missed is reported.
 
-## 4. Freeze a version
+### A4. Freeze a version
 
 `.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -- freeze`
 
@@ -86,7 +146,7 @@ like `leak: messages.Content dictionary-term ×3`, nothing was kept: add the mis
 
 When the dictionary grows later, re-check older versions: `… -- check --version vN`.
 
-## 5. Raw vs sanitised comparison (§4.2)
+### A5. Raw vs sanitised comparison (§4.2)
 
 1. `… -Tool history-eval -- holdout --corpus frozen:vN` (and `-Tool memory-eval -- holdout --corpus frozen:vN`).
 2. `… -Tool eval-corpus -- compare --version vN` (add `--tool memory-eval` for memory). The report
@@ -95,7 +155,7 @@ When the dictionary grows later, re-check older versions: `… -- check --versio
    with the gate result. **The gate decision uses the raw run.** Results on a mostly pre-event-log snapshot are
    provisional (re-run after ≥ 4 weeks of live capture).
 
-## 6. Off-machine backup in SafeExchange
+### A6. Off-machine backup in SafeExchange
 
 Packages are attached to the **existing secret `cortex-recovery-kit`** (it also holds the storage recovery kit). Never
 create, list or read any other secret for this.
@@ -118,7 +178,7 @@ create, list or read any other secret for this.
 5. **Record it:** `… -Tool eval-corpus -- uploaded --version vN --content-id <contentId> --sha256 <sha256>`.
 6. Delete the host copy of the package once it is uploaded (it is encrypted, but there is no reason to keep it).
 
-## 7. When history is deleted (D3)
+### A7. When history is deleted (D3)
 
 A deletion whose period overlaps a frozen version destroys that version on this machine (its runs and package too).
 The deletion confirmation says so and names the attachment, and the Recall page then shows
@@ -127,14 +187,14 @@ The deletion confirmation says so and names the attachment, and the Recall page
 1. With your assistant: `secret_get cortex-recovery-kit`, find the file `cortex-eval-corpus-vN.cxpkg` (or the
    recorded contentId), `file_delete` it (`confirm: true`). Check with `secret_get` and `audit_read`.
 2. On the Recall page, enter the Bridge password and press **I deleted it**.
-3. Freeze a new version (§4); labels that pointed to deleted messages must be removed and the workspace registered
+3. Freeze a new version (A4); labels that pointed to deleted messages must be removed and the workspace registered
    again first (the freeze tells you how many). Versions are never renumbered.
 
 To drop a version yourself: `… -Tool eval-corpus -- delete --version vN` (an exported or imported one is queued for
 off-machine deletion the same way). The pending list, the version counter and a log of completed deletions are kept in
 `/app/state/eval-ledger`, outside `eval/`, so erasing the evaluation artifacts never loses them.
 
-## 8. Evaluating elsewhere (import)
+### A8. Evaluating elsewhere (import)
 
 On another Cortex install (it may be unencrypted): copy the package into the agent's packages folder,
 `docker cp cortex-eval-corpus-vN.cxpkg cortex-agent:/app/state/eval/packages/`, then
@@ -145,7 +205,7 @@ mapped back to real names. An import is refused while the version is still on th
 when the package's corpus range overlaps a history deletion that already happened (the purged messages must not come
 back from an old copy); delete such a package instead.
 
-## 9. Without encryption at rest
+### A9. Without encryption at rest
 
 The in-container commands keep working (`docker exec cortex-agent dotnet Cortex.Contained.Agent.Host.dll history-eval …`),
 and so do the scripts. On an encrypted install, `docker exec` runs only `kappa` and the synthetic `history-smoke`
```

- [ ] **Step 2: Check it**

Expected: the runbook starts with *Run the evaluation (one button)* (five steps), the manual sections are `A1`–`A9` under *Advanced: commands one by one*, and `api-reference.md` lists the ten new routes. No tests (documentation only).

- [ ] **Step 3: Commit**

```powershell
git add docs/api-reference.md docs/architecture.md docs/evaluation-runbook.md
git commit -m "docs: one-button evaluation in the runbook, API reference and architecture" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 22: Final verification

- [ ] **Step 1: Build the solution**

```powershell
dotnet build cortex-contained.sln -v q
```

Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 2: Run the three affected test projects**

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-build
dotnet test tests/Cortex.Contained.Bridge.Tests --no-build
dotnet test tests/Cortex.Contained.Contracts.Tests --no-build
```

Expected (replay): `Passed!  - Failed: 0, Passed: 3085` (Agent.Host), `Passed: 2031` (Bridge), `Passed: 267` (Contracts). A
failure in one of the load-flaky classes of gotcha 13 (`SubAgentSetGoalToolTests`, `MaintenanceStoreTests`,
`MessageStoreJournalTests`) is not caused by this plan: rerun that class alone.

- [ ] **Step 3: Check hygiene**

```powershell
git --no-pager diff c04d2b5 --stat
git --no-pager diff c04d2b5 --name-only | Select-String version.json
git --no-pager grep -n -i -E "zqsecret|zqquery|zqcandidate" -- src
```

Expected: only the files of the file-structure table changed; no `version.json`; the `zq…` markers appear only under
`tests/`. Read the new log templates (`EvaluationRunService`): ids, phases, tools, commands and exit codes only.

- [ ] **Step 4: Smoke test on a dev install (optional, makes real LLM calls)**

With credentials configured: `.\scripts\Invoke-CortexEval.ps1 -Tool history-eval -- label --estimate` prints the
estimate without calls. Then on the Recall page press *Run evaluation* and watch the first phases; *Cancel* stops it
(progress is kept). No commit.

## Spec coverage

| Spec item | Task(s) |
|---|---|
| L1 models, families, pinned provider | 1, 2, 13 |
| L2 `label` as a job, copy deleted | 13 |
| L3 stratified sampling, real requests (co-primary), recall echoes excluded, `until` clamp | 4, 5, 10, 11, 12 |
| L4 writer checks: copy, paraphrase guard, temporal filters, identifier, tags; rewrite with reason | 6 |
| L5 model-written queries (main model / recorded args) | 7, 10 |
| L6 pooling across the grid + gold, judged@10 | 10, 11, 12 |
| L7 batched judge, strict JSON, retries, backoff, splitting | 2, 8 |
| L8 pre-declared finalisation rules | 9, 15 (stated in the plan) |
| L9 checkpoints, cumulative cap, estimate | 3, 10, 13 |
| L10 κ sample (75, by grade, blind), agreement files, κ gate over all items | 9, 14, 17 |
| L11 grading access: password once, session grant, rate limit, no-store, x-text, no judge grade | 18, 19, 20 |
| L12 run state machine, jobs, waits, stop/resume, restart, passphrase never stored | 16, 17, 18 |
| L13 analysis plan before dev ablation, rule-based selection on dev, register before holdout, Gate R cell | 15, 16, 17 |
| L14 fresh start | 17, 20 |
| L15 dictionary approval and freeze loop | 17, 20 |
| L16 results summary, verdicts, CIs, raw vs sanitised, reports | 17, 18, 19, 20 |
| §5 privacy (provider only, sealed files, ids-only logs) | 2, 3, 9, 10, 13, 14, 17, 19 |
| §6 owner workflow / runbook | 21 |
| §7 cost and time estimate | 10, 13, 21 |
