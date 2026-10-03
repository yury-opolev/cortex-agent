# Voice timer channel knowledge implementation plan

> **For agentic workers:** Use superpowers:executing-plans task-by-task with test-driven-development, verification-before-completion, and a whole-diff review.

**Goal:** Tell voice turns and deferred rest-cue runs their channel, persist scheduled origin, and preserve voice presentation context without changing tool channel contracts.

**Architecture:** Keep existing ToolExecutionContext and send validation. Persist scheduled OriginChannelId separately from the optional explicit target ChannelId. Fired runs receive origin in context and in their instructions; explicit targets remain intact. Use existing catalog identity for voice classification; no new routing infrastructure.

**Tech Stack:** C#/.NET 10, xUnit, NSubstitute, FakeTimeProvider, SQLite.

**Spec:** `docs/channel-delivery-design.md`, constrained by the user's latest narrowed implementation request. This plan supersedes its earlier origin-default implementation scope.

## Constraints and rulings

- Do NOT change send_message: omitted channel still fails; explicit valid targets work.
- Do NOT change schedule_task channel argument handling, schema optionality, or target semantics. Persist separate origin metadata only.
- No outbox, registry rewrite, leases, playback receipts, or deployment/restart.
- Preserve the existing user modification of `version.json`; never stage it.
- User has approved implementation and PR creation/push/merge. Execute inline without another design approval round.
- Dedicated branch in the existing checkout rather than another worktree: initialized native/submodule build dependencies are available here. Preserve initial working-tree changes; include accepted design, exclude version.
- Record red/green outputs under ignored `artifacts/channel-routing-*.log` and progress below.

## Review focus

1. Canonical discord-voice ID must appear in the rendered current-channel sentence, not just an ambiguous label.
2. Origin and explicit task target may differ; preserve both without rewriting arguments or permissions.
3. Schema migration must preserve existing pending schedules and leave historical origin unknown.
4. Internal/legacy schedules lacking origin must continue to work without invented webchat origin.
5. Composer and isolated runtime tests must exercise actual fired messages, prompts, and tool contexts, not only string helpers.

## Task 1 — Channel prompt and async modality

Files: `Tools/ChannelCatalog.cs`, `Reminders/SessionTimerService.cs`, `Scheduler/SchedulerService.cs`; tests `SystemPromptCharacterizationTests.cs`, `SessionTimerServiceTests.cs`, `SchedulerServiceTests.cs`.

- [x] RED: rendered prompt theory for all catalog channels and plugin; Discord voice sentence must name `discord-voice`.
- [x] GREEN: supply missing catalog label (audit other entries), rerun.
- [x] RED: fired-message theories, Discord/local voice => IsVoice true; webchat/DM => false, origin retained.
- [x] GREEN: derive IsVoice from fired channel identity; rerun.

## Task 2 — Persist scheduled origin and expose it to the fired run

Files: `Tools/BuiltIn/ScheduleTaskTool.cs`, `Scheduler/ScheduledTask.cs`, `Scheduler/SqliteTaskStore.cs`, `Scheduler/SchedulerService.cs`; tests `SchedulerToolTests.cs`, new `SqliteTaskStoreOriginTests.cs`.

- [x] RED: create/reload/fire from voice context with omitted channel and explicit webchat target; verify separate origin and destination through rendered task context.
- [x] RED: non-destructive schema upgrade preserves old schedules without guessing origin.
- [x] GREEN: pass creation context, add OriginChannelId and additive SQLite migration, expose origin in enriched instructions and fired context.
- [x] RED/GREEN: inspection shows unknown target honestly instead of claiming webchat fallback.

## Task 3 — Runtime and contract regressions

Files: `TimerComposerRuntimeTests.cs`, `SendMessageToolTests.cs`.

- [x] RED before modality fixes: real voice timer -> composer prompt identifies channel and voice mode; tool receives same channel.
- [x] RED before schedule fixes: actual created/reloaded task -> isolated run receives origin and voice mode; explicit target remains in instructions.
- [x] Add voice-origin send omission rejection regression; expected to pass unchanged, so no production edits to send_message.

## Task 4 — Review, verify, publish

- [x] Review complete production/test diff; address findings test-first.
- [x] Run full solution tests and build; report any environmental/live-provider failures explicitly.
- [ ] Commit selected files, push, open PR with root cause/TDD/results, verify checks and merge without bypassing policy.
- [ ] Pull merged main preserving version change; rebuild, report running-install deployment/restart needs without restarting it.

## Execution ledger

- Baseline Agent Host suite: passed 2040/2040; `artifacts/channel-routing-baseline.log`.
- Branch: `fix/voice-timer-origin-routing`, base `b082e47`, equal to fetched origin/main.
- Initial prompt RED: Discord voice lacked channel sentence (1 failed, 10 passed). Label fix GREEN: 22 passed including catalog tests.
- User narrowed scope before any send/schedule production changes. No tool-contract changes to revert. Strengthen prompt regression to require canonical `discord-voice` in the sentence, then continue narrowed plan.

### Verified red/green evidence

| Cycle | RED (expected failure observed) | GREEN |
|---|---|---|
| Missing voice prompt label | 1 failed: current-channel sentence absent | 22 prompt/catalog tests passed |
| Canonical ID in voice label | 1 failed: sentence did not name discord-voice | 22 passed |
| Async modality + timer composer | 5 failed: timer/scheduler voice flags false and composer voice-mode block absent; 4 text cases passed | 85 affected tests passed |
| Scheduled origin persistence and isolated runtime | 4 failed: Origin channel missing after SQLite reload/in isolated instructions | 84 scheduler/runtime/migration tests passed |
| Version-3 upgrade/new-origin round trip | 1 failed: origin missing after persistence; historical row retained | Included in the 84-test green run |
| Misleading inspection fallback | 1 failed: expected honest unspecified target; unchanged voice send omission regression passed | 71 scheduler/send tests passed |
| Review finding: export/import lost origin | 1 failed: restored origin null instead of discord-voice | 9 mapper tests passed |

Ruling: the existing composer/runtime already propagates AgentMessage.ChannelId into prompt and tool context. Tests exercise the real runtime and demonstrate this; no gratuitous production changes to IntentComposer or AgentRuntime were needed.

### Self-review and fixes

- Reviewed all production/test changes against the latest narrowed scope. Neither SendMessageTool production code nor either tool schema changed. Existing unknown/inactive/omission validation suites remain green.
- Caught origin loss through export/import: added a red round-trip test, then an optional DTO field and both mapping directions. This is preservation of the same metadata, not a new routing contract.
- Reviewed SQLite evolution: schema 3 is upgraded additively/transactionally; existing target, recurrence, state, and execution count survive; unknown historical origin stays null. Existing pre-v3 legacy reset behavior is unchanged.
- Strengthened new persistence/runtime fixtures to actually omit the channel property rather than serialize a null value. Retained target/origin distinction assertions. Refactored migration fixture from JSON into the now-available typed property after verified RED/GREEN.
- Fixed a test-fixture cleanup issue found during RED: SQLite pooled connections held the temporary database open on Windows, masking the intended runtime assertion. Clear only that fixture's pool before deleting its directory; reran RED to verify missing-origin failures.
- Audited catalog: discord-voice was the only null prompt label. All built-in channels plus a plugin channel are exercised through rendered prompts.
- A fresh subagent review was attempted but could not start due to provider context-limit failure. It produced no review; the recorded review here is the implementer's own full-diff pass.

### Full verification (before PR)

- `dotnet test tests/Cortex.Contained.Agent.Host.Tests --no-restore`: **2062 passed**, 0 failed/skipped (baseline 2040; 22 added cases).
- `dotnet test cortex-contained.sln`: **5202 passed** across 10 normal test projects; **28 live-evaluation setup failures** (26 Evals: no EVAL_LLM_API_KEY; 2 ScenarioEvals: no SCENARIO_EVAL_BRIDGE_PASSWORD). This command was not green; no production credentials were substituted.
- Normal repository test gate: every `tests/*.Tests` project rerun with `--no-build --no-restore`: **5202 passed**, 0 failed/skipped across 10 projects. This matches the credential-free gate documented in CLAUDE.md and docs/self-update.md.
- `dotnet build cortex-contained.sln --no-restore`: succeeded, **0 warnings / 0 errors**.
- `git diff --check`: clean. Full output retained locally in ignored artifacts/channel-routing-*.log.

Live-evaluation setup failures by test display name (all environmental, not hidden):
- Failed Scenario(scenarioFile: "priya-sharma.json") [1 ms]
- Failed Scenario(scenarioFile: "mateo-ruiz.json") [1 ms]
- Failed Slicer identifies a clear topic shift and summarizes the earlier topic [1 ms]
- Failed Slicer works in reverse direction (voice ΓåÆ text) with a clear topic shift [1 ms]
- Failed China: Extracts personal life for Wei [1 ms]
- Failed All personas: No trivial extraction from small talk [1 ms]
- Failed China: Extracts career facts for Wei [1 ms]
- Failed Australia: Extracts personal life for Mia [1 ms]
- Failed China: Multi-turn all aspects for Wei, no duplication [1 ms]
- Failed China: Extracts hobbies for Wei [1 ms]
- Failed Canada: Extracts hobbies for Priya [1 ms]
- Failed Australia: Extracts hobbies for Mia [1 ms]
- Failed Canada: Multi-turn cross-aspect dedup for Priya [1 ms]
- Failed All personas: Seeded facts are searchable by content [1 ms]
- Failed Argentina: Extracts dev environment for Mateo [1 ms]
- Failed Argentina: Extracts career facts for Mateo [1 ms]
- Failed Argentina: Career update replaces stale info for Mateo [1 ms]
- Failed Argentina: Extracts personal life for Mateo [1 ms]
- Failed Canada: Extracts personal life for Priya [1 ms]
- Failed Australia: Extracts career facts for Mia [1 ms]
- Failed Canada: Extracts career facts for Priya [1 ms]
- Failed Australia: Hobby update replaces stale info for Mia [1 ms]
- Failed Within-batch facts about same topic are merged, not duplicated [1 ms]
- Failed Updates existing memory when new related info arrives [1 ms]
- Failed Extracts basic user facts from conversation [1 ms]
- Failed Does not create duplicate memories from repeated mentions [1 ms]
- Failed Does not extract facts from trivial/greeting conversations [1 ms]
- Failed Contradicting information updates or replaces old memory [1 ms]
