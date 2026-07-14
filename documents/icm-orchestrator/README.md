# IcM Orchestrator Configuration Package

This is the "Initial Prompt Package" (§20) of
[`documents/cortex-icm-orchestrator-proposal.md`](../cortex-icm-orchestrator-proposal.md): the
agent-managed layer — prompts, skills, and schemas — that makes a Cortex agent behave as a local
IcM incident orchestrator. It is configuration and documentation only. No product code lives here,
and none of these files are executed directly; they are authored here, then deployed onto a
running Cortex install through the surfaces described below.

Batches A/B/C (Tasks 1-11 of the proposal's reliability implementation plan) shipped the product
code this package depends on: durable subagent execution and restart recovery, configurable 1-50
concurrency, MCP invocation identity with explicit `OutcomeUnknown` outcomes,
administrator-configured mutation classification with exact-canonical-argument approval, the
encrypted Bridge-side MCP action ledger/outbox and its REST API, MCP telemetry redaction and
result bounds, and the generic content-free subagent/action operations endpoints. This package is
Phase 0/1 of the proposal's implementation phases (§19): validate the local foundation, then run a
read-only shadow orchestrator using exactly the surfaces below. Phase 2 (supervised mutations) is
already viable on day one because the approval mechanism shipped ahead of this package — see
[`approval-policy.md`](approval-policy.md).

## Files

| File | Purpose |
|---|---|
| [`coordinator-system-prompt.md`](coordinator-system-prompt.md) | The coordinator's identity, state machine, responsibilities, supervision loop, and recovery procedure. Short and stable by design. |
| [`subagent-instructions.md`](subagent-instructions.md) | The shared contract every incident worker runs under, regardless of which skill it's given. |
| [`approval-policy.md`](approval-policy.md) | Autonomous vs. approval-required operations, and exactly how the shipped exact-argument approval flow works. |
| [`incident-state-schema.md`](incident-state-schema.md) | The `/app/data/icm-orchestrator/` workspace layout and the `checkpoint.json` schema. |
| [`dashboard-schema.md`](dashboard-schema.md) | How portfolio/incident state becomes `dashboard/data.json`, and which live endpoints back it. |
| [`skills/icm-investigation/SKILL.md`](skills/icm-investigation/SKILL.md) | Step-by-step investigation procedure. |
| [`skills/kusto-evidence/SKILL.md`](skills/kusto-evidence/SKILL.md) | Bounded, cited Kusto querying. |
| [`skills/incident-communications/SKILL.md`](skills/incident-communications/SKILL.md) | Drafting and sending IcM/Teams/Mail communications. |
| [`skills/memory-lesson/SKILL.md`](skills/memory-lesson/SKILL.md) | BC MemoryMcp recall and sanitized lesson publication. |

Read `coordinator-system-prompt.md` first — everything else is referenced from it.

## MCP topology (the six servers)

Configure these as native Cortex host-side MCP servers in the Bridge (`mcpServers` in
`cortex.yml`):

| Key | Launch model | Purpose |
|---|---|---|
| `agency-teams` | `agency mcp teams` | read discussions, find people/channels, propose or send escalation messages |
| `agency-mail` | `agency mcp mail` | search related mail, draft or send escalation/follow-up mail |
| `agency-icm` | `agency mcp icm` | search, inspect, acknowledge, comment, mitigate, transfer, and resolve incidents as supported |
| `agency-enghub` | `agency mcp enghub` | retrieve TSGs, service docs, ownership, and operating procedures |
| `bc-memory` | installed MemoryMcp client | retrieve prior lessons and publish sanitized learnings |
| `kusto` | Agency Kusto MCP if available, otherwise a separately installed stdio Kusto MCP | telemetry investigation |

All incident subagents use this same unified MCP set — role differences come from the task brief,
not a different tool allowlist (see
[`coordinator-system-prompt.md`](coordinator-system-prompt.md#mcp-topology)). Each server also
needs an explicit `toolAllowList` (never empty — an empty list exposes every tool) and, for any
tool that mutates a remote system, a `mutationToolAllowList` entry that routes it through the
shipped approval flow. See [`docs/mcp-plugin-system.md`](../../docs/mcp-plugin-system.md) for the
full server config shape and
[`docs/security.md`](../../docs/security.md#mcp-mutation-approval-and-durable-reliability) for why
that gate is a security boundary, not just a prompt convention.

## Deploying this package

None of these files are read by the Agent Host container directly from `documents/`. Each has a
specific deployment target on a running install:

- **`coordinator-system-prompt.md`** → the tenant's **personality** text
  (`PUT /api/tenants/{tenantId}/personality`), which renders into the `{{personality}}`
  placeholder of the main system-prompt template.
- **`subagent-instructions.md`** → the tenant's `subagentInstructions` field
  (`PUT /api/tenants/{tenantId}/system-prompt`), which renders into the `{{instructions}}`
  placeholder of the subagent template and is included for every `sub_agent_start`-spawned
  worker automatically.
- **`skills/*/SKILL.md`** → copied (or written by Cortex itself with `file_write`) to
  `/app/data/skills/<name>/SKILL.md` inside the container — the exact location and YAML
  frontmatter format (`name:`, `description:`) the built-in `SkillRegistry` scans. This is a
  *different* directory from the incident workspace below; do not deploy skills under
  `/app/data/icm-orchestrator/`.
- **`approval-policy.md`, `incident-state-schema.md`, `dashboard-schema.md`** → reference
  documents. Their content is realized as actual server config (`mutationToolAllowList` entries
  in `cortex.yml`) and actual workspace files under `/app/data/icm-orchestrator/` that the
  coordinator creates and maintains at runtime — these markdown files are the schema/policy
  the agent is instructed to follow, not files it reads at runtime. Point the coordinator at them
  once (e.g. paste this README's summary into its first bootstrap message, or have it
  `file_read` a copy placed in its own workspace) if you want it to treat them as living
  reference rather than one-time setup instructions.

## Verified against shipped code

Every tool name, message string, and endpoint this package references was checked against the
current source, not assumed from the proposal text:

- The exact-approval message is `"Awaiting exact-argument approval. Do not repeat this
  mutation."` (`McpActionService.AwaitingApprovalMessage`,
  `src/Cortex.Contained.Bridge/Mcp/Actions/McpActionService.cs`).
- `mcp_action_status(action_id)` and `mcp_action_cancel(action_id, arguments_hash)` are real
  agent tools (`src/Cortex.Contained.Agent.Host/Mcp/McpActionStatusTool.cs`,
  `McpActionCancelTool.cs`).
- `sub_agent_start`, `sub_agent_send`, `sub_agent_stop`, `sub_agent_read` are the real built-in
  subagent tools (`src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SubAgent*.cs`).
- `POST /api/mcp/actions/{actionId}/approve|reject|cancel|reconcile` and
  `GET /api/mcp/actions`, `GET /api/mcp/actions/{actionId}` are the real approval endpoints
  (`src/Cortex.Contained.Bridge/Endpoints/McpActionEndpoints.cs`).
- `GET /api/tenants/{tenantId}/operations/subagents` and `GET /api/operations/mcp-actions` are
  the real content-free observability endpoints, and `GET /health` returns
  `{ healthy, timestamp, version, metrics, mcpActions }`
  (`src/Cortex.Contained.Bridge/Endpoints/OperationsEndpoints.cs`, confirmed against
  `docs/api-reference.md`).
- The action state machine (`proposed -> approved -> dispatching -> succeeded | failed |
  outcome_unknown -> reconciled_succeeded | reconciled_failed`, plus `rejected`/`cancelled`/
  `expired`) matches `McpActionState`/`McpActionWireStatus`
  (`src/Cortex.Contained.Bridge/Mcp/Actions/`).
- `MaxConcurrentSubagents`/`maxConcurrentSubagents` is configurable 1-50, rejected (never
  clamped) outside that range, confirmed in `docs/security.md`, `docs/api-reference.md`, and
  `docs/setup-guide.md`.
- The skills directory convention (`/app/data/skills/<name>/SKILL.md`, YAML frontmatter with
  `name`/`description`) matches `src/Cortex.Contained.Agent.Host/Agent/SkillRegistry.cs`.
- The file sandbox root (`/app/data`) matches `SandboxPathResolver` and is the same root the
  proposal's `/app/data/icm-orchestrator/` workspace lives under.
- `personality` and `subagentInstructions` as the deployment targets for the coordinator/subagent
  prompts match `SystemPromptDefaults`/`SystemPromptConfig`
  (`src/Cortex.Contained.Contracts/SystemPrompt/`) and the
  `/api/tenants/{tenantId}/personality` and `/api/tenants/{tenantId}/system-prompt` endpoints
  (`src/Cortex.Contained.Bridge/Tenants/TenantEndpoints.cs`).
- Built-in tool names referenced throughout (`file_read`, `file_write`, `file_edit`, `file_list`,
  `file_find`, `file_delete`, `grep`, `run_command`, `memory_search`/`memory_get`/
  `memory_ingest`/`memory_update`/`memory_delete`, `self_notes_read`/`self_notes_write`,
  `schedule_task`, `history_read`) match `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/`.

`memory_*` built-in tools are Cortex's own internal personal memory (backed by the
`lib/memory-mcp` submodule) and are explicitly distinct from BC MemoryMcp, the external
`bc-memory` MCP server this package's `skills/memory-lesson/SKILL.md` targets — the two are never
interchangeable in these files.
