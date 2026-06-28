# Design: Agent-sent image attachments (Discord)

**Date:** 2026-06-28
**Status:** Approved (pending implementation)
**Branch:** `feat/discord-image-attachments`

## Goal

Let the agent attach one or more images (from its `/app/data` sandbox) to a message
it sends to a channel — primarily Discord — alongside optional caption text.
Implemented by extending the existing `send_message` tool and threading attachment
bytes through the existing proactive → channel upload path.

## Background — what already exists

The channel side of media is **already built**; the gap is entirely upstream.

- `DiscordChannel.SendWithAttachmentsAsync` (`src/Cortex.Contained.Channels.Discord/DiscordChannel.cs:926`)
  already uploads files via `MediaAttachment.Data` (bytes) and `.Url`.
- `DiscordChannel.Capabilities` already declares `SupportsMedia = true` and lists
  `image/png`, `image/jpeg`, `image/gif`, `image/webp` (and more) in `SupportedMediaTypes`.
- `OutboundMessage.Content.Attachments` (`MessageContent.Attachments`) is honored end-to-end on the channel side.
- The **inbound** path (user → agent images) already exists via `HubInboundMessage.Attachments`.

What is missing: **nothing the agent can do ever populates outbound attachments.**

- Regular reply path: `ResponseCompleteMessage` carries `FullText` only;
  `HubMessageDispatcher.OnAgentResponseCompleteAsync` builds `OutboundMessage` with text only.
- Proactive path: `ProactiveMessage` (the DTO the `send_message` tool uses) has only a
  `Text` field — no attachments.
- The agent has **no tool** to reference or send an image.

## Decisions (from brainstorming)

1. **Image source:** sandbox file path. The agent points at a file it created/downloaded
   into `/app/data`; the tool reads the bytes. (Base64-in-text does not work: Discord only
   renders real file uploads or plain image URLs it can unfurl — base64 is just the wire
   encoding of the bytes, not a user-visible mechanism.)
2. **Tool shape:** extend the existing `send_message` tool with an optional `attachments`
   param (array of sandbox-relative paths). Text + image travel in one call. This also covers
   "reply with an image" — the agent simply calls `send_message` during its turn.
3. **Limits / validation:** per-file cap **8 MB** (safe for non-boosted Discord), validate
   MIME against an image whitelist (`png/jpeg/gif/webp`), allow up to **4** attachments per
   message. Limits are hardcoded (not YAML-configurable).
4. **Channel scope:** Discord is the verified target. The plumbing is channel-agnostic, so any
   media-capable channel benefits, but only Discord is verified. WebChat UI rendering is out of scope.

## Data flow

```
LLM → send_message({text, channel, attachments:["chart.png"]})
  → SendMessageTool: resolve+validate each path, read bytes → MediaAttachment[]
  → IProactiveMessageDispatcher.DispatchAsync(channel, text, attachments, ctx)
  → ProactiveMessage { Text, ChannelId, Attachments }                 ← NEW field
  → [SignalR] IAgentHubClient.OnProactiveMessage
  → Bridge HubMessageDispatcher: copy Attachments → OutboundMessage.Content.Attachments  ← NEW mapping
  → DiscordChannel.SendMessageAsync → SendWithAttachmentsAsync → SendFileAsync   ← already exists
```

Everything from the channel inward already works; the new code is the agent-side
load/validate, one new DTO field, and one mapping in the Bridge.

## Components

### 1. `AttachmentLoader` (new — `src/Cortex.Contained.Agent.Host/Tools/`)

Isolated, testable unit. Input: sandbox root + relative path. Behavior:

- `SandboxPathResolver.ResolveAndVerify(sandboxRoot, path)` — traversal-safe (reuses the
  existing resolver used by `FileReadTool`).
- exists check
- size ≤ **8 MB**
- MIME inferred from file extension, validated against the image whitelist (`png/jpeg/gif/webp`)
- returns a `MediaAttachment { MimeType, FileName, Data, SizeBytes }` on success, or a typed
  failure reason on any check.

Keeps `SendMessageTool` thin and makes the validation rules independently unit-testable.

### 2. `SendMessageTool` (extend — `Tools/BuiltIn/SendMessageTool.cs`)

- New optional `attachments` param: array of sandbox-relative paths, **max 4**.
- Loads each via `AttachmentLoader`; any failure → `AgentToolResult.Fail` with a clear message
  the LLM relays.
- **Relaxes** the "text required" check: empty text is allowed when ≥1 attachment is present.
- Needs `sandboxRoot` injected (via `Program.cs`).
- Description updated to mention image-sending.

### 3. `ProactiveMessage` DTO (extend — `Contracts/Hub/HubTypes.cs`)

Add `IReadOnlyList<MediaAttachment>? Attachments`. Additive; older consumers ignore it.

### 4. `IProactiveMessageDispatcher` / `ProactiveMessageDispatcher` (extend)

- Add an optional `attachments` param (default `null`, so `transfer_session` and other callers
  are unaffected).
- Forward onto `ProactiveMessage.Attachments`.
- Persist a history marker (e.g. `text + " [image: chart.png]"`) so the chat UI and the agent's
  deferred-injection record reflect that an image was sent.

### 5. Bridge `HubMessageDispatcher.OnProactiveMessageAsync` (extend)

Set `OutboundMessage.Content.Attachments = message.Attachments`. One line.

### 6. DI wiring (`Program.cs`)

Inject `sandboxRoot` (already available as a local at `Program.cs:89`) into `SendMessageTool`
(directly or via `AttachmentLoader`).

## Error handling

File-not-found, outside-sandbox, oversize, and unsupported-type each return a distinct
`AgentToolResult.Fail` reason. Bridge/channel send failures surface through the existing
`ProactiveDispatchResult.Error` path.

## Scope & non-goals

- Discord is the verified target; plumbing is channel-agnostic. WebChat UI rendering is out of scope.
- Images only (not arbitrary files), bytes-over-SignalR (no URL fetching), no AI image generation.
- Limits hardcoded (8 MB / 4 images / image MIME whitelist), not YAML-configurable.

## Testing

- `AttachmentLoaderTests` — traversal rejected, missing file, oversize, bad type, happy path
  (correct MIME + bytes).
- `SendMessageToolTests` — attachments parsed; empty-text-with-attachment allowed; validation
  failures return `Fail`; dispatcher receives the `MediaAttachment[]`.
- `ProactiveMessageDispatcherTests` — attachments forwarded onto `ProactiveMessage`; history
  marker persisted.
- Bridge `HubMessageDispatcher` test — `ProactiveMessage.Attachments` →
  `OutboundMessage.Content.Attachments`.
- Manual post-deploy verify: the agent actually posts an image to Discord.

## Files to change

| File | Change |
|------|--------|
| `src/Cortex.Contained.Contracts/Hub/HubTypes.cs` | add `Attachments` to `ProactiveMessage` |
| `src/Cortex.Contained.Agent.Host/Tools/AttachmentLoader.cs` | **new** — load + validate |
| `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SendMessageTool.cs` | parse attachments, build via loader, relax empty-text, accept sandboxRoot |
| `src/Cortex.Contained.Agent.Host/Tools/IProactiveMessageDispatcher.cs` | add optional `attachments` param |
| `src/Cortex.Contained.Agent.Host/Tools/ProactiveMessageDispatcher.cs` | forward attachments; history marker |
| `src/Cortex.Contained.Agent.Host/Program.cs` | inject sandboxRoot into `SendMessageTool` |
| `src/Cortex.Contained.Bridge/Channels/HubMessageDispatcher.cs` | map `ProactiveMessage.Attachments` → outbound |
| `tests/**` | the test suites listed above |
