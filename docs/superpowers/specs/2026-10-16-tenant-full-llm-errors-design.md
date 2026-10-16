# Per-tenant "show full LLM provider errors" — design

## Problem

When an LLM provider call fails, the channel (Discord, webchat) only sees a sanitized
line from `LlmErrorPresenter.ToUserMessage`, e.g.
`The model provider returned an error (HTTP 400).` The actual cause (e.g. Copilot's
`{"error":{"code":"model_not_supported",...}}`) is only in the agent container logs, so
diagnosing a failure needs `docker logs`. The operator wants the provider's raw error
text shown in the chat — but only for tenants where that is appropriate.

## Behaviour

- New per-tenant boolean `showFullLlmErrors` (nullable in config).
- Unset → resolves to `true` for the default tenant and `false` for every other tenant.
  An explicit value always wins.
- When it resolves to `true` and the agent supplied provider detail, the message
  delivered to the channel is:

  ```
  <sanitized message>

  Provider response: <raw provider error text, max 1500 chars, "…" appended if cut>
  ```

- When it resolves to `false`, or no detail exists, delivery is unchanged.
- The agent's persisted chat history keeps only the sanitized message (unchanged).

## Design

Decision is made **Bridge-side at delivery time**: the Bridge already receives errors
per tenant (`HubMessageDispatcher.OnAgentErrorAsync(agentError, tenantId)`) and owns
tenant config, so no new per-tenant config push to the agent is needed and a toggle
takes effect on the next error.

### Contracts
- `AgentErrorMessage` gains `string? Detail` — raw provider error text. Optional, so
  older agents/Bridges stay wire-compatible.
- `TenantConfig` gains `bool? ShowFullLlmErrors` plus
  `bool ResolveShowFullLlmErrors()` => `ShowFullLlmErrors ?? Default`.

### Agent.Host
- `TurnResponseDelivery.PersistLlmErrorAsync` sets `Detail = errorMessage` (the raw
  `"HTTP {status}: {body}"` / transport message it already receives). `Message` and the
  persisted history entry are unchanged.

### Bridge
- New static `LlmErrorDetailFormatter.Compose(string message, string? detail, bool showDetail)`
  (pure, unit-tested): returns `message` unless `showDetail` and `detail` is non-blank;
  otherwise appends the detail block, trimmed and truncated to 1500 chars.
- `TenantRouter.ShouldShowFullLlmErrors(string tenantId)` → registry lookup +
  `ResolveShowFullLlmErrors()`, `false` for an unknown tenant.
- `HubMessageDispatcher.OnAgentErrorAsync` uses the formatter for the outbound text.
- `TenantStatus` / `GET /api/tenants` expose the resolved `showFullLlmErrors`.
- `CreateTenantRequest` / `UpdateTenantRequest` accept `bool? ShowFullLlmErrors`;
  the PUT merge sets it when provided.
- `BridgeSettingsWriter` persists `showFullLlmErrors: true|false` only when explicitly
  set (null stays absent so the default-tenant rule keeps applying).

### Web UI
- Tenant settings page (`app.html` + `js/pages/tenant-settings.js`): new
  "Diagnostics" card with a switch "Show full LLM provider errors in chat", loaded from
  the resolved value and saved via the existing `PUT /api/tenants/{id}` on change,
  with a one-line hint that the raw provider response may contain technical details.

## Security note

Detail is the provider's response body only — never request headers — so it carries
no credentials (the separate header-redaction fix covers logs). Non-default tenants
default to off because a raw body can expose internal details to an end user.

## Testing
- `TenantConfig.ResolveShowFullLlmErrors`: default/non-default × null/true/false.
- `LlmErrorDetailFormatter.Compose`: off, on without detail, on with detail,
  truncation at 1500.
- `HubMessageDispatcher` error path (if an existing test seam allows) — flag on/off.
- `TurnResponseDelivery.PersistLlmErrorAsync` sends `Detail` = raw message while
  `Message` stays sanitized.
- `BridgeSettingsWriter` round-trip writes the explicit value and omits null.
- Tenant PUT merge of `showFullLlmErrors`: verified manually through the UI toggle
  (no `/api/tenants` endpoint test harness exists; the merge is a one-line assignment).
