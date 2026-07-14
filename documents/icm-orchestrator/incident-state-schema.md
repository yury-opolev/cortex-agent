# Incident State Schema

Cortex creates and owns a durable workspace under its persistent `/app/data` volume (the same
sandbox root every built-in file tool — `file_read`, `file_write`, `file_edit`, `file_list`,
`file_find`, `file_delete` — resolves paths against):

```
/app/data/icm-orchestrator/
  config/
    teams.json
    policy.md
    query-catalog.json
    ownership-map.json
  incidents/
    <incident-id>/
      incident.json
      brief.md
      plan.md
      checkpoint.json
      findings.jsonl
      evidence.jsonl
      actions.jsonl
      communications.md
      lessons.md
      final-report.md
  portfolio/
    active.json
    queue.json
    metrics.json
    coordinator-checkpoint.json
  dashboard/
    index.html
    data.json
  logs/
    coordinator.jsonl
```

This is a *data* workspace, separate from the agent's *skills* directory
(`/app/data/skills/<name>/SKILL.md` — see [`README.md`](README.md#deploying-this-package)). Do
not confuse the two: skills are reusable procedure the agent loads with `file_read` before
starting a task; the tree above is per-incident and portfolio-wide state.

The files are machine-readable first and human-readable second. Markdown provides operator
context; JSON/JSONL provides deterministic recovery and dashboard input.

**Writes use temp-file plus rename** (write to `<file>.tmp`, then rename over the original) so a
crash mid-write never leaves a partially-written file for the coordinator or a replacement worker
to read. JSONL records are append-only — never rewritten in place. Every JSONL record carries a
UTC timestamp, incident ID, worker/task ID, source system, and a stable correlation ID.

## Configuration files

- `config/teams.json` — the team scopes the coordinator polls, and per-team defaults (severity
  floor, pre-authorization eligibility).
- `config/policy.md` — the local, human-editable policy document referenced by
  [`approval-policy.md`](approval-policy.md#optional-pre-authorization): pre-authorization rules,
  team/action/severity/content bounds, and anything the pilot has decided to override from the
  package defaults. Read this before [`approval-policy.md`](approval-policy.md) when the two
  disagree — this file is the operator's live word.
- `config/query-catalog.json` — the bounded, pre-approved Kusto query templates from
  [`skills/kusto-evidence/SKILL.md`](skills/kusto-evidence/SKILL.md#query-catalog).
- `config/ownership-map.json` — team/service ownership used to verify a transfer target before
  proposing one (`subagent-instructions.md`: "Verify ownership before proposing a transfer.").

## Per-incident files

Each `incidents/<incident-id>/` directory is one incident's complete local record:

| File | Content |
|---|---|
| `incident.json` | Raw/normalized snapshot of the last-fetched remote IcM incident. |
| `brief.md` | The coordinator's investigation brief: facts, hypotheses, queries, safety constraints, success criteria. |
| `plan.md` | The current investigation/mitigation plan, updated as it evolves. |
| `checkpoint.json` | The recovery source of truth — see schema below. |
| `findings.jsonl` | Append-only structured findings (observation/hypothesis/confidence/recommended action). |
| `evidence.jsonl` | Append-only cited evidence records (source system, query, time range, identifiers). |
| `actions.jsonl` | Append-only mirror of MCP mutation actions proposed for this incident (`actionId`, `argumentsHash`, status) — a local index into the real Bridge action ledger, not the ledger itself. |
| `communications.md` | Drafted and sent Teams/Mail/IcM-discussion content, per [`skills/incident-communications/SKILL.md`](skills/incident-communications/SKILL.md). |
| `lessons.md` | The sanitized lesson draft, per [`skills/memory-lesson/SKILL.md`](skills/memory-lesson/SKILL.md#lesson-creation). |
| `final-report.md` | Written once the incident reaches `resolved`/`mitigated` and its conclusion is understood. |

## Incident checkpoint

`checkpoint.json` is the recovery source of truth. The native subagent task store is an execution
aid, not the incident record — after a restart, the coordinator rebuilds its understanding of
where an incident stands from this file, not from worker conversation history.

Each `checkpoint.json` should include at least:

```json
{
  "schemaVersion": 1,
  "incidentId": 123456789,
  "state": "investigating",
  "severity": 2,
  "owningTeamId": 30164,
  "assignedWorkerIds": [],
  "lastRefreshUtc": "2026-07-13T00:00:00Z",
  "lastProgressUtc": "2026-07-13T00:00:00Z",
  "currentHypotheses": [],
  "confirmedFindings": [],
  "openQuestions": [],
  "nextActions": [],
  "pendingApprovals": [],
  "remoteActionIds": [],
  "lessonStatus": "not-ready"
}
```

Field notes:

- `state` — one of the coordinator state machine values in
  [`coordinator-system-prompt.md`](coordinator-system-prompt.md#state-machine).
- `assignedWorkerIds` — the `sub_agent_start` task IDs (`sa-<guid>`) currently or most recently
  assigned to this incident; used to match native subagent state back to the incident during
  recovery.
- `lastRefreshUtc` — last time the remote IcM incident was re-fetched.
- `lastProgressUtc` — last time any finding, evidence record, or checkpoint field changed;
  staleness is `now - lastProgressUtc`, the trigger for a supervision nudge.
- `pendingApprovals` — `actionId`s currently `proposed`/`approved`/`dispatching` in the real MCP
  action ledger, per [`approval-policy.md`](approval-policy.md#how-approval-actually-works-shipped-mechanism).
- `remoteActionIds` — `actionId`s that reached a terminal state (`succeeded`, `failed`,
  `reconciled_succeeded`, `reconciled_failed`, `rejected`, `cancelled`, `expired`) for this
  incident; kept for audit even after `pendingApprovals` no longer references them.
- `lessonStatus` — one of `not-ready`, `drafted`, `promoted`; see
  [`skills/memory-lesson/SKILL.md`](skills/memory-lesson/SKILL.md#lesson-creation).

## Portfolio files

- `portfolio/active.json` — the list of incidents currently tracked (up to the 50-incident
  portfolio ceiling), each with a pointer to its `incidents/<incident-id>/` directory and a
  denormalized summary (state, severity, `lastProgressUtc`) for fast scans without opening every
  checkpoint.
- `portfolio/queue.json` — incidents discovered but not yet assigned a worker slot, ordered by the
  priority policy in [`coordinator-system-prompt.md`](coordinator-system-prompt.md#responsibilities)
  (Sev0/1/2 immediate or preempting, Sev3 normal queue, Sev4/noise background queue).
- `portfolio/metrics.json` — rolling counters consumed by [`dashboard-schema.md`](dashboard-schema.md):
  discovery delay, time to first triage/evidence, worker throughput, action outcome counts.
- `portfolio/coordinator-checkpoint.json` — the coordinator's own recovery state: last poll time
  per team scope, last reconciliation run, in-flight portfolio-level decisions.

## Logs

- `logs/coordinator.jsonl` — append-only log of coordinator decisions (dispatch, nudge, stop,
  replace, escalate, propose) with the same UTC/incident/worker/correlation-ID discipline as the
  per-incident JSONL files. This is what "Record recovery decisions in the incident and
  coordinator logs" (`coordinator-system-prompt.md`) writes to at the portfolio level.
