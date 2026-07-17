# Copilot Response Routing Design

## Goal

Make every Cortex GitHub Copilot path support models such as `gpt-5.6-sol`
that are unavailable on `/chat/completions`. Live Copilot `/models` metadata is
the routing source of truth, matching Coda v0.1.64.

## Scope

This change covers both independent Copilot paths:

1. The Agent Host's direct LLM client used by the main agent, memory extraction,
   compaction, evaluation, and provider fallback.
2. The bundled Coda coding engine shipped inside the Cortex launcher MSIX.

The change does not replace Cortex's provider abstraction with Coda libraries,
and it does not proxy normal Cortex LLM calls through Coda.

## Endpoint Policy

For a Copilot model, Cortex selects the best supported HTTP endpoint using this
fixed priority:

1. `/responses` or `/v1/responses`
2. `/v1/messages`
3. `/chat/completions` or `/v1/chat/completions`

Unknown or missing endpoint metadata falls back to the existing
`/chat/completions` behavior. This preserves compatibility for configured
models whose metadata is unavailable.

WebSocket-only entries such as `ws:/responses` are ignored.

## Metadata Flow

The Bridge already fetches authenticated Copilot model data during setup.
That path will retain `supported_endpoints` instead of discarding it.

The metadata flows through the existing model configuration chain:

1. `SetupHelpers.CopilotModelEntry`
2. `AvailableModel`
3. `LlmModelDefinition` in persisted Bridge configuration
4. `LlmModelMetadata` in the Bridge-to-Agent credential contract
5. `CredentialsPusher`
6. `ProviderState` and Agent Host routing

`SupportedEndpoints` is an optional string list at every persisted/contract
boundary. Existing configurations without it remain valid and use Chat
Completions.

Live setup refreshes replace endpoint metadata alongside context and output
limits. The Bridge remains responsible for authenticated model discovery; the
Agent Host does not make a second `/models` request.

## Direct Agent Protocol Architecture

The existing `DirectLlmClient` continues to own:

- provider/model routing
- same-provider retry
- provider failover
- credential lifecycle

The OpenAI provider layer continues to own HTTP request construction and
response parsing.

Focused protocol components will be added under
`Llm/Providers/OpenAi`:

- a model endpoint resolver
- Responses request DTO/mapping
- Responses non-streaming response DTO/mapping
- Responses SSE parsing

`OpenAiCompatibleApiClient` selects the protocol from the current model's
metadata. Chat Completions remains unchanged for ordinary OpenAI-compatible
providers and Copilot models that advertise only Chat.

Anthropic Messages support may reuse the existing `AnthropicApiClient` by
dispatching the Copilot provider through that wire protocol while retaining
Copilot authentication/base URL. If the existing client cannot accept
provider-specific authentication without unsafe coupling, Messages remains a
small Copilot-specific path in the OpenAI provider layer. Responses support is
mandatory; Messages support must not delay it.

## Responses Request Mapping

The Responses request maps Cortex's provider-neutral request as follows:

- system prompt -> `instructions`
- user text/image content -> `input_text` / `input_image`
- assistant text -> assistant `output_text`
- assistant tool calls -> `function_call`
- tool results -> `function_call_output`
- tool definitions -> flat Responses `function` tools
- reasoning effort -> `reasoning.effort`
- model and streaming flags -> standard Responses fields

No hard-coded model IDs determine the protocol.

## Responses Parsing

Streaming parsing handles:

- `response.output_text.delta`
- `response.output_item.added`
- `response.function_call_arguments.delta`
- `response.output_item.done`
- `response.completed`
- `response.incomplete`
- `response.failed`
- top-level `error`

Tool arguments are accumulated by output index and finalized exactly once.
Usage maps to the existing Cortex token usage contract. A stream that ends
without a terminal event is an error and must not execute a partial tool call.

Non-streaming parsing maps the Responses output array into the existing
`LlmCompletionResult`, including text, function calls, finish reason, usage,
and provider errors.

The current 401 bearer refresh, transient retry, and provider failover rules
remain unchanged and operate around the selected protocol.

## Coda Submodule

Advance `lib/coda-cli` from `6650275` to Coda merge commit `a1f5398`
(v0.1.64). `scripts/Build-Launcher.ps1` will then bundle the fixed Coda
executable into the MSIX.

## Error Handling

- Missing metadata: use Chat Completions.
- Unsupported endpoint strings: ignore them and continue priority resolution.
- HTTP provider errors: preserve the existing clean Cortex error/failover path.
- Transient transport/5xx/429 failures: preserve same-provider retry.
- Responses failure events: surface the nested provider message.
- Truncated Responses streams: fail explicitly.
- Invalid function-call JSON remains a model/provider error; do not silently
  coerce it into a successful tool call.

## Testing

All production behavior is implemented red/green.

Required tests:

- Copilot `/models` parsing preserves endpoint metadata.
- Bridge configuration and credential contracts propagate endpoint lists.
- Endpoint resolver applies Responses -> Messages -> Chat priority.
- Missing metadata retains Chat compatibility.
- Responses request maps text, images, tools, calls, results, and effort.
- Non-streaming Responses parsing maps text, tools, usage, and errors.
- Streaming Responses parsing maps text, tool deltas, usage, terminal reasons,
  nested failures, and truncated-stream errors.
- Existing Chat Completions tests remain green.
- Direct LLM retry and failover behavior remains green.
- Coda submodule points to `a1f5398`.

Run the complete `cortex-contained.sln` test suite before review and again on
the final committed branch.

## Review and Delivery

Use a highest-quality Opus reviewer after implementation. Fix all Critical and
Important findings before delivery.

Commit and push the feature branch. Then:

1. Run `scripts/Build-All.ps1` with certificate thumbprint
   `F578A5879BE57511D40288B6DA3A0F383BD74EEE`.
2. Force-install the generated MSIX locally.
3. Relaunch the packaged launcher.
4. Verify Bridge `/health` reports the new version and healthy state.
5. Verify the running Agent Host container uses the newly built image.
6. Verify the bundled Coda version and a live `gpt-5.6-sol` request.

