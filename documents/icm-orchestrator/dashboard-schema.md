# Dashboard Schema

Version 1 of the dashboard is agent-created: Cortex reads the structured `portfolio/` and
per-incident files from its own workspace (schema:
[`incident-state-schema.md`](incident-state-schema.md)) and writes a static
`dashboard/index.html` plus the `dashboard/data.json` it renders from. There is no product-side
dashboard backend yet — this file documents the aggregation contract Cortex should follow so a
later Bridge-integrated dashboard (Phase 5 of the proposal) can consume the same shape.

```
/app/data/icm-orchestrator/
  portfolio/
    active.json
    queue.json
    metrics.json
    coordinator-checkpoint.json
  dashboard/
    index.html
    data.json
```

`dashboard/data.json` writes use temp-file plus rename, exactly like every other workspace file
(see [`incident-state-schema.md`](incident-state-schema.md)) — the dashboard's own HTML polls or
reloads `data.json`, so a partially-written file must never be observable.

## Aggregation contract

`dashboard/data.json` is populated by aggregating each active/queued incident's `checkpoint.json`
(full field list in
[`incident-state-schema.md`](incident-state-schema.md#incident-checkpoint):
`schemaVersion`, `incidentId`, `state`, `severity`, `owningTeamId`, `assignedWorkerIds`,
`lastRefreshUtc`, `lastProgressUtc`, `currentHypotheses`, `confirmedFindings`, `openQuestions`,
`nextActions`, `pendingApprovals`, `remoteActionIds`, `lessonStatus`) across every incident listed
in `portfolio/active.json` and `portfolio/queue.json`.

Recommended top-level shape:

```json
{
  "generatedAtUtc": "2026-07-13T00:00:00Z",
  "portfolio": {
    "activeCount": 0,
    "queuedCount": 0,
    "bySeverity": {},
    "byState": {},
    "byTeam": {}
  },
  "incidents": [],
  "workers": {
    "active": [],
    "stale": [],
    "failed": []
  },
  "pendingApprovals": [],
  "recentActions": [],
  "lessons": {
    "drafted": 0,
    "promoted": 0
  }
}
```

- `incidents` — one entry per tracked incident: the checkpoint fields above, plus incident age
  and `now - lastProgressUtc` (the staleness measure the coordinator's supervision loop already
  computes — see [`coordinator-system-prompt.md`](coordinator-system-prompt.md#worker-supervision)).
- `workers` — derived from live subagent state. When the coordinator has agent access, call
  `sub_agent_read` per `assignedWorkerIds`; when a Bridge-side refresh script is used instead
  (Phase 5), the equivalent authenticated source is
  `GET /api/tenants/{tenantId}/operations/subagents` (query params `limit`, `includeTerminal`,
  `staleAfterSeconds`; returns `503` while the agent is disconnected). Both surfaces are
  content-free — no prompt, message, or result text — by design.
- `pendingApprovals` / `recentActions` — mirror each incident's `checkpoint.json.pendingApprovals`
  / `remoteActionIds` (and `actions.jsonl` for narrative), cross-checked against the authoritative
  Bridge-side source `GET /api/operations/mcp-actions` (query params `tenantId`, `beforeId`,
  `limit`, `serverKey`, `toolName`, `outcome`, `workerTaskId`) — this endpoint stays available
  even while the agent is disconnected, since it is Bridge-local, unlike the subagent snapshot.
  Do not surface `canonicalArgumentsJson`, `resultContent`, or `error` on the dashboard from this
  path; those are deliberately omitted from the content-free projection and are visible only via
  the authenticated `GET /api/mcp/actions/{actionId}`.
- `lessons` — rolled up from each incident's `checkpoint.json.lessonStatus`.

## Views

- portfolio summary by severity/state/team;
- incident age and time since last progress;
- active/queued/stale/failed workers;
- current hypotheses and confidence;
- confirmed findings and evidence links;
- pending approvals;
- external actions and outcomes;
- MCP/provider failures;
- lessons drafted/published;
- coordinator and worker throughput statistics.

`GET /health` is a useful cross-check for the last two: it returns
`{ healthy, timestamp, version, metrics, mcpActions }`, where `metrics.subagents` is the
agent-reported pool aggregate and `mcpActions` is the Bridge-local action-state aggregate — both
optional and independently degrading to `null` on a probe failure, never flipping `healthy`. A
dashboard refresh script can poll `/health` as a cheap top-of-page status strip before pulling the
full operations endpoints.

## Controls (version 1: copyable commands)

Version 1 controls are copyable commands or messages to Cortex, not clickable buttons — the
dashboard is a read surface until Phase 5 adds a Bridge-integrated write path:

- focus incident;
- nudge worker (`sub_agent_send`);
- stop/restart worker (`sub_agent_stop`, then a fresh `sub_agent_start` from the checkpoint);
- approve/reject action (`POST /api/mcp/actions/{actionId}/approve` or `.../reject` — an operator
  action, not something the dashboard itself performs);
- pause/resume monitoring;
- mark finding reviewed.

## Version 2 (future): Bridge-integrated dashboard

If the local tool proves useful, the same aggregation described above moves into the existing
Bridge UI with authenticated APIs and live updates, retaining the workspace files as
export/recovery artifacts. Recommended additional metrics at that point: time to first triage and
first evidence; worker queue depth, duration, failure/restart count; tool calls and failures by
MCP server; Kusto query duration/result size; proposed/approved/rejected/succeeded/failed/unknown
action counts; escalation/transfer/mitigation outcomes; lessons created and reused. This is
tracked as Phase 5 in `documents/cortex-icm-orchestrator-proposal.md`, not part of this
configuration package.
