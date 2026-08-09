# Connector Plugin System

How third-party developers build Cortex **channels (connectors)** — in any language, out of tree,
with no fork — by connecting over a documented **WebSocket + JSON protocol**, getting approved once
through a pairing flow, and behaving like any first-party channel from that point on.

- Spec: [`docs/superpowers/specs/2026-08-07-connector-plugin-system-design.md`](superpowers/specs/2026-08-07-connector-plugin-system-design.md)
- Reference implementation: [`examples/connectors/echo`](../examples/connectors/echo) — single-file, zero-dependency Node.js
- Shipped: (unreleased)

> **Stability.** The protocol is additive: new optional fields and new error codes may appear, but
> existing field names, semantics and frame types will not change or be removed without a
> deprecation period. Build defensively — ignore unknown fields, and treat an unrecognised
> non-fatal error code as "back off and continue" — and your connector will keep working.

## The one principle: the Bridge is the connector host *and* the credential boundary

The agent never sees a connector credential. It only ever sends and receives `InboundMessage` /
`OutboundMessage` over the existing `/hub/agent` SignalR connection — exactly as it does with every
first-party channel. The Bridge owns everything the container cannot safely do: accepting connector
sockets, pairing approval, DPAPI token storage, revocation, and rate limiting.

> **Direction note — this is the opposite of MCP:** MCP servers sit _on the host_ and the Bridge
> dials _out_ to them. Connectors are _external processes_ that dial _in_ to the Bridge. The agent
> is entirely unaware of either.

Connectors are never reached from inside the container. Only message content crosses the container
boundary — never a connector credential, socket, or address.

## Component / deployment view

```mermaid
flowchart LR
    subgraph container["🐳 Docker container (sandboxed, no stored secrets)"]
        LLM["LLM loop / AgentRuntime"]
        HUB["AgentHub<br/>(SignalR hub @ :5100/hub/agent)"]
        LLM <-->|"InboundMessage / OutboundMessage<br/>(existing hub methods)"| HUB
    end

    subgraph host["🪟 Windows host (the Bridge)"]
        EP["ConnectorEndpoint<br/>ws://127.0.0.1:5080/connector<br/>(loopback-only WebSocket)"]
        SESSION["ConnectorSession<br/>(protocol state machine)"]
        AUTH["ConnectorPairingService<br/>(DPAPI tokens, rate limit)"]
        REGISTRY["ConnectorRegistry<br/>(attached channels)"]
        REPLAY["HubHistoryConnectorReplaySource<br/>(missed messages)"]
        UI["Web UI :5080<br/>Global Settings → Connectors"]
        HC["HubClient (Bridge side)"]
        EP --> SESSION
        SESSION --> AUTH
        SESSION --> REGISTRY
        SESSION --> REPLAY
        UI --> AUTH
        HC <-->|"SignalR<br/>(127.0.0.1:5100)"| HUB
        REGISTRY --> HC
    end

    subgraph connector["🔌 Connector (external process, any language)"]
        PROC["Connector process<br/>(dials in on start)"]
    end

    PROC == "ws://127.0.0.1:5080/connector<br/>(connector dials IN)" ==> EP
```

## Protocol reference

Every message on the wire is a JSON object with exactly two top-level fields:

```json
{ "type": "<frame-type>", "payload": { ... } }
```

`payload` is always an object; for frames with no payload (e.g. `ping`) it is an empty object `{}`.
The serialiser uses **camelCase** field names and omits null fields.

### Transport

| Property | Value |
|---|---|
| Endpoint | `ws://127.0.0.1:5080/connector` — loopback only, port follows `webUi.port` |
| WebSocket message type | **Text**. A binary message closes the session. |
| Encoding | UTF-8 |
| Framing | **One WebSocket message = exactly one JSON frame.** Never split a frame across messages or pack two into one. |
| Fragmentation | Continuation frames are reassembled by the Bridge before parsing, so you may fragment a single large message. |
| Max message size | 1 MiB accumulated across continuations. Exceeding it is fatal. |
| Field handling | Unknown fields are ignored. Field names are matched case-insensitively. |
| Ordering | The Bridge processes your frames strictly in order, one at a time. You may send while a generation is in flight — that is how `abort` works. |
| Concurrency | Do not open two sockets with the same `key`+`instanceId`; the second is refused with `duplicate_connector`. |
| Duplicate `messageId` | Not deduplicated. The Bridge treats a repeated id as a new message, so do not retry a send after a timeout unless you intend it to be processed twice. |

### Protocol versioning

There is currently **no protocol version negotiation**. `hello.version` is your *connector's* own
version string — it is stored for display and the Bridge does not act on it. The protocol evolves
additively: new optional fields may appear on Bridge→Connector frames, and new error codes may be
introduced. Implement accordingly:

- Ignore fields you do not recognise.
- Treat an unknown non-fatal error code as "back off and continue".
- Do not depend on field ordering or on the absence of a field.

### Frame type summary

| Frame | Direction | Purpose |
|---|---|---|
| `hello` | Connector → Bridge | Open the session; declare identity and capabilities |
| `inbound` | Connector → Bridge | Send a user message |
| `abort` | Connector → Bridge | Cancel an in-flight generation |
| `pong` | Connector → Bridge | Respond to a `ping` |
| `pairing_required` | Bridge → Connector | Pairing needed; includes code and expiry |
| `paired` | Bridge → Connector | Pairing approved; includes token and channel id |
| `pairing_denied` | Bridge → Connector | Pairing or auth denied; session closes after |
| `ready` | Bridge → Connector | Accepted; includes channel id and replay count |
| `typing` | Bridge → Connector | Agent is composing a reply |
| `stream` | Bridge → Connector | Streaming text delta |
| `outbound` | Bridge → Connector | Final agent response (also used for replay) |
| `error` | Bridge → Connector | Protocol or policy error |
| `ping` | Bridge → Connector | Liveness probe |

> **Fatal vs. recoverable:** a `pairing_denied` or any `error` that causes the Bridge to close the
> socket is fatal for that session. `rate_limited`, `invalid_payload`, and `message_too_long` are
> sent as error frames but the session remains open — the connector should back off and retry.
> See [Limits and error handling](#limits-and-error-handling) for the full table.

---

### `hello` (Connector → Bridge)

Sent as the **first frame** after connecting. Must arrive within **10 seconds** of the socket
being opened, or the Bridge sends `protocol_violation` and closes.

```json
{
  "type": "hello",
  "payload": {
    "key": "echo",
    "displayName": "Echo Connector",
    "version": "1.0.0",
    "instanceId": "default",
    "token": "dpapi:...",
    "sinceCursor": "2026-08-06T14:00:00.0000000Z",
    "capabilities": {
      "streaming": true,
      "richText": true,
      "media": false,
      "maxMessageLength": 100000
    }
  }
}
```

| Field | Type | Required? | Meaning |
|---|---|---|---|
| `key` | string | **Yes** | Connector type key. 1–64 chars, `[a-z0-9_-]`. Normalised to lowercase. |
| `displayName` | string | No | Human name shown in the Web UI. Defaults to `key`. Truncated at 256 chars. |
| `version` | string | No | Connector software version; informational only. |
| `instanceId` | string | No | Allows one `key` to serve multiple channels. Defaults to `"default"`. Same charset/length as `key`. |
| `token` | string | No | DPAPI token from a prior `paired` frame. Omit on first connect. |
| `sinceCursor` | string | No | ISO-8601 cursor for replay. **Omit (or absent/unparseable) → no replay.** |
| `capabilities` | object | No | See sub-fields below. |
| `capabilities.streaming` | bool | No | Set `true` to receive `stream` and `typing` frames. Default `false`. |
| `capabilities.richText` | bool | No | `true` if the connector renders Markdown. |
| `capabilities.media` | bool | No | Set `true` to send and receive attachments. Default `false`. See [Media attachments](#media-attachments). |
| `capabilities.maxMessageLength` | int | No | Max chars the connector accepts. Clamped to [1, 100 000]. Default 100 000. |

The **channel id** is derived deterministically: `plugin:<key>:<instanceId>`.

### Conversations

`conversationId` is **yours to choose**. The Bridge does not interpret it — it is an opaque routing
key that identifies a thread within your channel. Rules that matter:

- Omit it and it defaults to your channel id. Most connectors with a single conversation (a
  terminal, a CLI) can simply never send it.
- The agent keeps separate context per conversation, so distinct ids give you distinct threads.
- You may only `abort` a conversation you have previously sent an `inbound` for in the **current**
  session. A reconnect resets that set.
- At most **256** distinct ids are tracked per session; beyond that, further conversations cannot be
  aborted (fail-closed).
- Replayed `outbound` frames carry your **channel id**, not the original conversation id.

---

### `inbound` (Connector → Bridge)

A user message from the connector's channel.

```json
{
  "type": "inbound",
  "payload": {
    "conversationId": "my-thread-42",
    "messageId": "msg-0001",
    "sender": {
      "id": "user-99",
      "displayName": "Alice"
    },
    "content": {
      "text": "Hello, Cortex!",
      "isMarkdown": false
    }
  }
}
```

| Field | Type | Required? | Meaning |
|---|---|---|---|
| `conversationId` | string | No | Identifies the thread. Max 128 chars — **rejected**, not truncated, if longer. Defaults to the channel id. |
| `messageId` | string | No | Connector-assigned id. Max 128 chars — **rejected** if longer. Auto-generated if absent. |
| `sender.id` | string | No | Sender identifier. Max 128 chars — **rejected** if longer. Defaults to `"connector"`. |
| `sender.displayName` | string | No | Sender name. **Truncated** at 256 chars. |
| `content.text` | string | Conditional | Message text. Required **unless** `content.attachments` is non-empty — an attachment-only message is legal. |
| `content.isMarkdown` | bool | No | `true` when text contains Markdown. |
| `content.attachments` | array | No | Media attachments. Requires `capabilities.media: true`. See [Media attachments](#media-attachments). |

> **Lengths are UTF-16 code units, not characters.** `maxMessageLength` and every "max N chars"
> limit counts .NET `string` length. A non-BMP character (most emoji) counts as **2**. Truncation
> never splits a surrogate pair.

Rate-limited: see [Limits and error handling](#limits-and-error-handling).

---

### `abort` (Connector → Bridge)

Cancels an in-flight agent generation. The connector may only abort conversations it originated
(i.e. conversations for which it previously sent an `inbound` frame). Attempting to abort another
connector's conversation returns a non-fatal `invalid_payload` error and the session continues.

```json
{
  "type": "abort",
  "payload": {
    "conversationId": "my-thread-42"
  }
}
```

| Field | Type | Required? | Meaning |
|---|---|---|---|
| `conversationId` | string | No | Conversation to cancel. Omit (or null) to cancel the channel's default conversation. |

---

### `pong` (Connector → Bridge)

Reply to a `ping`. Payload is empty (`{}`). Must be sent promptly — the Bridge closes the session
if no frame of any kind arrives within **90 seconds** of the previous one.

```json
{ "type": "pong", "payload": {} }
```

---

### `pairing_required` (Bridge → Connector)

Sent when the connector has no stored token or when the stored token is invalid. The connector must
wait; a human must approve in the Web UI before the session continues.

```json
{
  "type": "pairing_required",
  "payload": {
    "code": "A3F7-2QT",
    "expiresAt": "2026-08-07T14:05:00.0000000+00:00"
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `code` | string | Pairing code to display, always **8 characters** in the shape `XXXX-XXX`. Drawn from an alphabet that excludes `0`, `1`, `I` and `O` so it cannot be misread aloud. The human compares it against the code shown in the Web UI. Size your UI for 8 characters. |
| `expiresAt` | string (ISO-8601) | UTC timestamp when the code expires. Codes expire after **5 minutes**. |

---

### `paired` (Bridge → Connector)

Sent immediately after a successful pairing approval. **Save the token** — present it in every
future `hello` to skip the pairing flow.

```json
{
  "type": "paired",
  "payload": {
    "token": "dpapi:AQAAANCMnd8...",
    "channelId": "plugin:echo:default"
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `token` | string | DPAPI-encrypted token; treat as opaque. |
| `channelId` | string | The assigned channel id for this `key`+`instanceId`. |

A `ready` frame follows immediately after `paired`.

---

### `pairing_denied` (Bridge → Connector)

Sent when pairing is refused. The Bridge closes the socket immediately after. Do not reconnect
automatically — see [Pairing](#pairing) for the full list of denial reasons.

```json
{
  "type": "pairing_denied",
  "payload": {
    "reason": "connector_disabled"
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `reason` | string | Machine-readable denial reason (see [Pairing](#pairing)). |

---

### `ready` (Bridge → Connector)

The Bridge has accepted the connector and is about to enter steady state. Sent **before** any
replay frames, so the connector knows the total count in advance.

```json
{
  "type": "ready",
  "payload": {
    "channelId": "plugin:echo:default",
    "replayCount": 3
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `channelId` | string | Confirmed channel id for this session. |
| `replayCount` | int | Number of `outbound` replay frames that will follow immediately. `0` means no replay. |

---

### `typing` (Bridge → Connector)

Sent only when `capabilities.streaming` was `true` in `hello`. Indicates the agent is composing
a reply for the given conversation.

```json
{
  "type": "typing",
  "payload": {
    "conversationId": "my-thread-42"
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `conversationId` | string | Conversation for which the typing indicator is active. |

---

### `stream` (Bridge → Connector)

A partial text delta. Sent only when `capabilities.streaming` was `true`. Accumulate deltas in
order; a final `outbound` frame closes the response.

```json
{
  "type": "stream",
  "payload": {
    "conversationId": "my-thread-42",
    "delta": "Hello, "
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `conversationId` | string | Conversation receiving the streaming text. |
| `delta` | string | Partial text to append to the current response. |

---

### `outbound` (Bridge → Connector)

The final agent response, or a replay frame for a message the connector missed while offline.
**Always save the `cursor` value** from the most recent `outbound` you receive, and send it back
as `sinceCursor` on your next `hello` to receive missed messages.

```json
{
  "type": "outbound",
  "payload": {
    "conversationId": "my-thread-42",
    "messageId": "f3a1b2c4d5e6",
    "content": {
      "text": "Hello! How can I help?",
      "isMarkdown": true
    },
    "isThinking": false,
    "cursor": "2026-08-07T14:00:01.2345678Z"
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `conversationId` | string | Target conversation. |
| `messageId` | string | Agent-assigned message id. |
| `content.text` | string | Response text. |
| `content.isMarkdown` | bool | Whether the text should be rendered as Markdown. The agent does not currently flag its responses, so this is `false` today for both live and replayed messages; treat agent text as Markdown if your surface can render it. |
| `content.attachments` | array | Media attachments. Present only when you negotiated `capabilities.media: true` and the message carries media. Identical shape to the inbound direction — see [Media attachments](#media-attachments). |
| `isThinking` | bool | `true` for pre-tool narration (thinking aloud), `false` for the final answer. |
| `cursor` | string | ISO-8601 timestamp for replay. Omitted rather than sent as `null` if unset, since the serialiser drops null fields. |

> **`conversationId` on replayed frames is the CHANNEL id, not the original conversation.** Replay
> is reconstructed from the agent's message history, which records the channel a message was
> delivered to rather than the connector's own thread id. If you route by `conversationId`, expect
> replayed messages to arrive on your channel-level default thread.

---

### `error` (Bridge → Connector)

A protocol or policy error. Some are fatal (socket closes after); others are recoverable (session
continues). See [Limits and error handling](#limits-and-error-handling).

```json
{
  "type": "error",
  "payload": {
    "code": "rate_limited",
    "message": "Message rate limit exceeded."
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `code` | string | Machine-readable code; one of the values in [Limits and error handling](#limits-and-error-handling). |
| `message` | string | Human-readable description. |

---

### `ping` (Bridge → Connector)

Liveness probe sent every **30 seconds**. Reply with `pong` immediately.

```json
{ "type": "ping", "payload": {} }
```

---

## Lifecycle

### First-run pairing

```mermaid
sequenceDiagram
    participant C as Connector
    participant B as Bridge

    C->>B: WebSocket connect to ws://127.0.0.1:5080/connector
    C->>B: hello { key, displayName, capabilities }
    B-->>C: pairing_required { code, expiresAt }
    Note over B: waiting for human approval in Web UI<br/>( up to 5 minutes )
    Note over C: display code prominently; do not send frames

    B-->>C: paired { token, channelId }
    Note over C: persist token + channelId
    B-->>C: ready { channelId, replayCount: 0 }
    Note over B,C: steady state — ping/pong every 30 s
    C->>B: inbound { ... }
    B-->>C: typing { conversationId }
    B-->>C: stream { conversationId, delta } (×N)
    B-->>C: outbound { ..., cursor }
    Note over C: save cursor for next connect
```

### Reattach with token (and replay)

```mermaid
sequenceDiagram
    participant C as Connector
    participant B as Bridge

    C->>B: WebSocket connect
    C->>B: hello { key, token, sinceCursor, capabilities }
    Note over B: token matches stored record → approved
    B-->>C: ready { channelId, replayCount: 3 }
    B-->>C: outbound { ..., cursor }  (replay × replayCount)
    B-->>C: outbound { ..., cursor }
    B-->>C: outbound { ..., cursor }
    Note over B,C: steady state
    C->>B: inbound { ... }
    B-->>C: outbound { ..., cursor }
    Note over C: update saved cursor
```

## Pairing

Pairing is a one-time human-approval step that binds a connector to a channel id. Once approved,
the Bridge issues a DPAPI-encrypted token that the connector presents on every subsequent connect.

**What the user sees:** when a new connector connects the Web UI shows a pairing request with the
connector's display name, channel id, and an 8-character code in the shape `XXXX-XXX` (e.g. `A3F7-2QT`). The connector displays the
same code. The user verifies they match and clicks **Approve** in
**Global Settings → Connectors**. This out-of-band code comparison prevents a rogue process from
silently claiming a channel.

**Pairing denial reasons** (sent in `pairing_denied.reason`):

| Reason | Meaning |
|---|---|
| `connector_disabled` | The connector exists but has been disabled via the Web UI toggle. |
| `pairing_rate_limited` | More than **5 pairing attempts** in a **10-minute** rolling window for this channel id. Wait before retrying. Do **not** reconnect immediately. |
| `pairing_expired` | The 5-minute code window elapsed before the user approved. Reconnect to start a new pairing attempt. |
| `denied` | The user clicked **Deny** in the Web UI. |
| `shutting_down` | The Bridge is stopping. |

**When `RequireApproval` is `false`** (see [Configuration](#configuration)) the Bridge
auto-approves and immediately issues a token without any human interaction or code display.

## Replay

When a connector reconnects after being offline it can receive messages it missed by sending a
`sinceCursor` in its `hello` frame.

- **Cursor format:** ISO-8601 round-trip timestamp (UTC). The Bridge formats it as the `"o"`
  format specifier, e.g. `2026-08-07T14:00:01.2345678Z`.
- **Where to get a cursor:** the `cursor` field of every `outbound` frame. Save the latest one
  you receive.
- **What is replayed:** only **assistant (outbound) messages** whose timestamp is strictly greater
  than `sinceCursor` and within the configured `MaxAge` window (default **24 hours**).
- **What is NOT replayed:** inbound messages, `typing` frames, `stream` deltas, or any message
  older than `MaxAge`.
- **Cap:** at most `MaxMessages` (default **100**) messages are replayed. When the store contains
  more, the **newest** `MaxMessages` within the window are returned.
- **No cursor → no replay:** an absent `sinceCursor` produces zero replay frames. An unparseable
  cursor also produces zero replay frames — the Bridge fails closed rather than flooding the
  connector with an entire history.
- **Future-dated cursors** are clamped to `now`, preventing a far-future cursor from permanently
  suppressing replay.

The `ready` frame always arrives **before** any replay frames and includes `replayCount` so the
connector knows how many `outbound` frames to expect before entering steady state.

## Capabilities negotiation

Capabilities are declared in the `hello` frame and affect what the Bridge sends in steady state:

| Capability | Effect |
|---|---|
| `streaming: true` | Bridge sends `typing` and `stream` frames during generation. Without this, the connector only ever receives `outbound`. |
| `richText: true` | Declares that your surface can render Markdown. See the note on `outbound.content.isMarkdown` — the agent does not currently set that flag. |
| `media: true` | Send and receive `content.attachments`. See [Media attachments](#media-attachments). Without it, inbound attachments are refused with `media_not_supported` and outbound frames omit the field entirely. |
| `maxMessageLength` | Bridge enforces this limit on `inbound` frames; messages exceeding it receive `message_too_long`. Clamped to [1, 100 000]. |

## Media attachments

Requires `capabilities.media: true` in your `hello`. A connector that does not declare it behaves
exactly as it did before media existed: outbound frames carry no `attachments` field at all (not an
empty array), and sending attachments is refused with a non-fatal `media_not_supported`.

The **same shape** is used in both directions, so you parse attachments identically on `inbound`
and `outbound`.

```json
{
  "type": "inbound",
  "payload": {
    "conversationId": "terminal-default",
    "content": {
      "text": "what's in this screenshot?",
      "attachments": [
        {
          "mimeType": "image/png",
          "fileName": "screenshot.png",
          "caption": "the failing dialog",
          "sizeBytes": 20481,
          "data": "iVBORw0KGgoAAAANSUhEUg…"
        }
      ]
    }
  }
}
```

| Field | Type | Required? | Meaning |
|---|---|---|---|
| `mimeType` | string | **Yes** | Must be in the allow-list. **Verified against the content's magic bytes** — a mismatch is rejected. |
| `fileName` | string | No | Display name. Sanitised (path separators, control and Unicode-format characters stripped) and truncated to 256 chars. |
| `caption` | string | No | Alt text. Truncated to 1024 chars. |
| `data` | string | One of | Base64 bytes, for small attachments. Mutually exclusive with `handle`. |
| `handle` | string | One of | Opaque Bridge-issued id, for large attachments. Mutually exclusive with `data`. |
| `sizeBytes` | int | No | Declared size. A **hint only** — the Bridge verifies the actual decoded length and never trusts this value. |

> **There is no `url` field, and there never will be.** A frame containing `url` on an attachment
> is rejected with `invalid_payload`. The Bridge will not dereference a location supplied by a
> connector — doing so would hand an untrusted local process a fetch primitive inside the
> credential boundary. See [Security model](#security-model).

### Two carrying modes

A WebSocket frame is capped at **1 MiB** and exceeding it is **fatal**. Base64 inflates payloads by
about a third, so large images cannot travel inside a frame at all.

| Mode | Use when | How |
|---|---|---|
| **Inline** (`data`) | The image is small — a screenshot, a chart. | Base64 it straight into the frame. |
| **Handle** (`handle`) | Anything larger. | Transfer the bytes over the REST endpoints below, then reference the returned handle in a frame. |

The Bridge decides the outbound mode for you and will spill to a handle whenever inlining would not
fit. **Both modes must be implemented** to receive media reliably — you cannot assume `data` will
always be present.

Inbound, prefer `data` for anything comfortably under ~256 KB and a handle above that. If you inline
something too large you get a non-fatal `attachment_too_large` telling you to upload it instead.

### Uploading (connector → agent)

```http
POST /api/connectors/attachments HTTP/1.1
Host: 127.0.0.1:5080
Authorization: Bearer <your pairing token>
Content-Type: multipart/form-data; boundary=…

(a "file" part containing the image; optional "caption" field)
```

A raw body works too — send the bytes with the image `Content-Type` and optional
`X-Attachment-Filename` / `X-Attachment-Caption` headers.

```json
{ "handle": "att_9f2c14e0d3b74a15", "expiresAt": "2026-08-09T12:10:00Z" }
```

Reference `handle` in an `inbound` frame before it expires.

### Fetching (agent → connector)

When an `outbound` frame carries `handle` instead of `data`:

```http
GET /api/connectors/attachments/att_9f2c14e0d3b74a15 HTTP/1.1
Host: 127.0.0.1:5080
Authorization: Bearer <your pairing token>
```

The response body is the raw image with its `Content-Type`.

**Authenticate with the same pairing token from your `paired` frame**, as a bearer token. These two
endpoints are the only `/api/connectors/*` routes that accept it; every other route on that prefix
requires the Web UI session and is not for connectors. Both are **loopback-only**, like the
WebSocket endpoint.

### Rules that will bite you

- **A handle is single-use.** Fetching consumes it. Fetch once and keep the bytes.
- **A handle expires** (10 minutes by default). After that it is simply gone.
- **A handle is scoped to your channel.** Another connector's handle returns `404`, indistinguishable
  from one that never existed.
- **Handles do not survive a Bridge restart.** Storage is in memory by design; media is a staging
  area, not a store. A restart surfaces as `attachment_not_found`.
- **Replay is text-only.** Messages replayed after reconnect never carry attachments: history keeps
  text, and any handle would have expired long before the 24-hour replay window. Do not expect media
  to reappear on reconnect.
- **Uploads have their own rate limit**, separate from the inbound message limit.
- **Attachment errors are never fatal.** A refused attachment leaves the session open; back off and
  carry on.

### Defaults

| Limit | Default | Config key under `connectors.media` |
|---|---|---|
| Attachments per message | 4 | `maxAttachmentsPerMessage` |
| Max size per attachment | 8 MB | `maxAttachmentBytes` |
| Max inline size | 256 KB | `maxInlineBytes` |
| Handle lifetime | 10 minutes | `handleTtl` |
| Storage held per connector | 64 MB | `maxStoredBytesPerConnector` |
| Uploads per minute | 30 (0 = unlimited) | `maxUploadsPerMinute` |
| Allowed types | `image/png`, `image/jpeg`, `image/gif`, `image/webp` | `allowedMimeTypes` |

`connectors.media.enabled: false` disables media entirely, regardless of what a connector declares.

## Limits and error handling

### Error codes

| Code | Fatal? | When it fires |
|---|---|---|
| `malformed_frame` | **Yes** | Frame is not valid JSON, not an object, or `type` is missing/not a string. |
| `unknown_frame_type` | **Yes** | `type` string is not one of the four connector-sent frame types. |
| `invalid_payload` | **Depends** | See the note below — this code is fatal during the handshake and at the parser level, non-fatal afterwards. |
| `protocol_violation` | **Yes** | State machine violation (e.g. non-`hello` first frame, handshake timeout, heartbeat timeout). Every `protocol_violation` the Bridge sends is fatal. |
| `frame_too_large` | **Yes** | WebSocket frame exceeds `MaxFrameBytes` (default **1 048 576** bytes). |
| `message_too_long` | No | `inbound.content.text` length exceeds `capabilities.maxMessageLength`. |
| `rate_limited` | No | Connector sent more than `MaxMessagesPerMinute` (default **120**) inbound messages in a rolling minute. Back off; session stays open. |
| `media_not_supported` | No | Attachments were sent without declaring `capabilities.media`, or media is disabled by policy. |
| `too_many_attachments` | No | More than `maxAttachmentsPerMessage` (default **4**) attachments on one message. |
| `attachment_too_large` | No | An attachment exceeds `maxAttachmentBytes`, or exceeds `maxInlineBytes` when sent inline. Upload it and send a handle instead. |
| `attachment_type_not_allowed` | No | MIME type is not in the allow-list, **or the content does not match the declared type**. |
| `attachment_not_found` | No | Handle is unknown, expired, already consumed, or was issued to another channel. These four are deliberately indistinguishable. |
| `not_paired` | — | Defined but **never sent**. The state machine makes the situation unreachable: the read loop only starts after pairing completes. Safe to ignore. |
| `connector_limit_reached` | **Yes** | `MaxConnectors` (default **16**) are already attached. |
| `duplicate_connector` | **Yes** | A connector with the same `key`+`instanceId` is already attached. |
| `connectors_disabled` | **Yes** | The connector subsystem is disabled (`connectors.enabled: false`). Note you will usually not see this frame at all — the endpoint refuses the upgrade with HTTP **503** before a socket exists. |

> **`invalid_payload` is not uniformly recoverable.** It is **fatal** when it arrives
> (a) before the session is established — a `hello` that fails to deserialise, or whose `key` or
> `instanceId` is missing or malformed; or (b) from the frame parser — a frame with a missing,
> empty, or non-string `type`, or a `payload` that is not an object. It is **non-fatal** for
> post-handshake validation: an `inbound` that fails to deserialise, has neither text nor
> attachments, carries an over-long id, has a bad attachment, or an `abort` for a conversation you
> do not own. Practically: if you receive `invalid_payload` before your `ready` frame, expect the
> socket to close.

**Fatal** means the Bridge closes the WebSocket immediately after sending the `error` frame.
**Not fatal (No)** means the error frame is sent but the session remains open; the connector
should back off and retry the failing operation.

### Heartbeat

The Bridge sends `ping` every **30 seconds**. If no frame of any kind has been received from the
connector for **90 seconds**, the Bridge sends `protocol_violation: heartbeat_timeout` and closes.

### Connector count cap

Once `MaxConnectors` (default 16) are attached, any new connector immediately receives
`connector_limit_reached` and is disconnected.

## Configuration

Under `connectors:` in `cortex.yml` (or environment overrides):

```yaml
connectors:
  enabled: true              # master kill-switch; drops all live channels when set to false
  requireApproval: true      # false = auto-approve without a code-check
  maxConnectors: 16          # max concurrently attached connectors
  replay:
    maxMessages: 100         # maximum outbound messages replayed on reconnect
    maxAge: "24:00:00"       # TimeSpan; messages older than this are not replayed
  limits:
    maxFrameBytes: 1048576   # maximum WebSocket frame size in bytes (1 MiB)
    maxMessagesPerMinute: 120  # inbound rate limit per connector
  media:
    enabled: true                     # master switch for connector attachments
    maxAttachmentsPerMessage: 4       # attachments carried on one message
    maxAttachmentBytes: 8388608       # per-attachment cap (8 MB)
    maxInlineBytes: 262144            # largest attachment carried inline as base64 (256 KB)
    handleTtl: "00:10:00"             # how long an issued handle resolves
    maxStoredBytesPerConnector: 67108864  # live handle bytes held per connector (64 MB)
    maxUploadsPerMinute: 30           # upload rate limit per connector; 0 = unlimited
    allowedMimeTypes:                 # omit entirely to use the four defaults
      - image/png
      - image/jpeg
      - image/gif
      - image/webp
```

All values shown are the **defaults from the source** — omitting a key uses that default.

> **`allowedMimeTypes` replaces the defaults, it does not add to them.** Listing only
> `image/png` means PNG only. Omit the key entirely to get all four. Do not use it to disable
> media — set `media.enabled: false` instead.

> **`maxInlineBytes` is capped by `maxFrameBytes`.** Configuring it larger than a frame can carry
> has no effect: the effective value is clamped so base64-encoded attachments always fit, with
> room reserved for the message text and JSON envelope. A whole-message inline budget applies too,
> so four individually-legal attachments cannot overflow the frame in aggregate.

**Web UI** — **Global Settings → Connectors** exposes:

- Master enable/disable toggle
- List of paired connectors with live/offline status and enable/disable/revoke controls
- Pending pairing requests with approve/deny buttons

Tokens are never surfaced in the UI or API responses.

## Security model

- **Loopback only:** the `/connector` WebSocket endpoint is guarded by a double loopback check
  (pre-accept and post-accept). Any connection arriving from a non-loopback address receives
  `403 Forbidden` before the socket is upgraded. The Kestrel bind address (`127.0.0.1:5080` by
  default) is the primary defence; the loopback check is defence in depth.
- **Code comparison:** first-run pairing requires a human to visually compare the code the
  connector prints with the code shown in the Web UI. This prevents a rogue local process from
  silently claiming a channel.
- **DPAPI tokens:** pairing tokens are DPAPI-encrypted and stored on disk. The connector receives
  the token as an opaque string; the Bridge validates it with a constant-time comparison.
- **Revocation:** an administrator can revoke a connector from the Web UI or `DELETE /api/connectors/{channelId}`,
  which drops the live channel immediately.
- **Abort ownership:** a connector may only abort conversations it originated — specifically,
  conversations for which it previously sent an `inbound` frame in the current session (or its own
  channel id). The Bridge tracks up to **256** conversation ids per session; the 257th and beyond
  cannot be aborted (fail-closed).
- **No connector-supplied URLs, ever.** Attachments carry bytes or a Bridge-issued handle, never a
  location. If the Bridge fetched a connector-supplied URL it would gain a fetch primitive inside
  the credential boundary: `file:///C:/Users/<user>/AppData/Local/Cortex/secrets/secrets.json`
  would exfiltrate DPAPI-protected secrets, and `http://169.254.169.254/…` is textbook SSRF. A
  frame containing `url` on an attachment is rejected outright rather than having the field
  ignored.
- **Attachment content is verified, not trusted.** The declared `mimeType` is checked against the
  content's magic bytes, and `sizeBytes` is ignored in favour of the actual decoded length.
- **Attachment handles are capabilities:** ≥128 bits of entropy, bound to the issuing channel,
  single-use, and expiring. Presenting another channel's handle returns `404` — not `403`, which
  would confirm it exists. Handle values are never written to logs.
- **What a connector can do:** send user messages, receive agent responses, abort its own
  in-flight turns, reconnect after disconnect, and (with `media`) exchange images.
- **What a connector cannot do:** reach the container directly, see other connectors' messages or
  attachments, abort another connector's or channel's turn, read or write DPAPI secrets, or access
  any Cortex REST endpoint other than the two attachment routes without the Web UI auth cookie.

## Writing a connector

### Checklist

- [ ] Connect to `ws://127.0.0.1:5080/connector`
- [ ] Load a saved token from disk (if present)
- [ ] Send `hello` immediately (within 10 seconds)
- [ ] Handle `pairing_required` — display the code; wait (no timeout on your end)
- [ ] On `paired` — persist token and `channelId`; a `ready` will follow
- [ ] On `ready` — log channel id and replay count; process replay `outbound` frames
- [ ] On every `outbound` — save the `cursor`; use it as `sinceCursor` next connect
- [ ] Reply to `ping` with `pong`
- [ ] On `rate_limited` / `message_too_long` / attachment errors — back off, do not close
- [ ] On fatal `error` or `pairing_denied` — close cleanly, do not reconnect immediately
- [ ] Treat `invalid_payload` received **before** `ready` as fatal
- [ ] Reconnect with exponential backoff on socket close (except after `pairing_denied`)
- [ ] If you declared `media`: handle **both** `data` and `handle` attachments on `outbound`
- [ ] If you declared `media`: upload anything over ~256 KB rather than inlining it
- [ ] Ignore fields you do not recognise, so future protocol additions do not break you

### Conformance checklist

Worth exercising before you ship. Every row is behaviour the Bridge actually enforces:

| Scenario | Expected |
|---|---|
| Connect and send `hello` within 10 s | `pairing_required` or `ready` |
| Do not send `hello` for 15 s | `protocol_violation`, socket closes |
| Send `inbound` as the first frame | `protocol_violation`, socket closes |
| Send `{"type":"nonsense"}` | `unknown_frame_type`, socket closes |
| Send `not json` | `malformed_frame`, socket closes |
| Send a message longer than `maxMessageLength` | `message_too_long`, **session stays open** |
| Send 200 messages in a minute | `rate_limited`, session stays open |
| Ignore `ping` for 90 s | `protocol_violation: heartbeat_timeout`, socket closes |
| Abort a `conversationId` you never sent | `invalid_payload`, session stays open |
| Reconnect with a saved token | straight to `ready`, no pairing |
| Reconnect with `sinceCursor` from your last `outbound` | replayed `outbound` frames, `ready.replayCount` matches |
| Send an attachment without declaring `media` | `media_not_supported`, session stays open |
| Send 5 attachments | `too_many_attachments` |
| Send a `.exe` labelled `image/png` | `attachment_type_not_allowed` |
| Send `url` on an attachment | `invalid_payload` |
| Reference a handle twice | second attempt gets `attachment_not_found` |
| Fetch an attachment with no `Authorization` header | HTTP 401 |
| Fetch a handle you did not receive | HTTP 404 |

### Walkthrough

See `examples/connectors/echo/connector.mjs` for a complete, single-file, zero-dependency Node.js
implementation. It demonstrates all of the above steps with inline comments that map directly to
the protocol sections above. Run it with:

```sh
node examples/connectors/echo/connector.mjs
```

See `examples/connectors/echo/README.md` for first-run and subsequent-run behaviour.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Pairing code never appears in the Web UI | The connector did not send `hello` within 10 seconds, or the `key`/`instanceId` is invalid (non-lowercase, too long, forbidden characters). Check the Bridge log for `protocol_violation`. |
| `pairing_denied` with `connector_disabled` | The connector's channel has been disabled in the Web UI. Re-enable it under Global Settings → Connectors. |
| `pairing_denied` with `pairing_rate_limited` | More than 5 pairing attempts in 10 minutes. Wait 10 minutes before reconnecting. |
| `pairing_denied` with `pairing_expired` | The code expired (5-minute window). Reconnect to start a new attempt. |
| `pairing_denied` with `denied` | A human clicked Deny. Reconnect to try again (a new code will be issued). |
| Connection refused / 403 Forbidden | Connecting from a non-loopback address. The endpoint only accepts `127.0.0.1`, `::1`, or IPv4-mapped loopback. |
| Immediate close after `hello` | `malformed_frame`, `unknown_frame_type`, or `protocol_violation` — check the `error` frame `message` field. Common causes: non-JSON payload, missing `type`, or sending a non-`hello` first frame. |
| `duplicate_connector` | A connector with the same `key:instanceId` is already attached. Disconnect the existing instance or use a different `instanceId`. |
| `connector_limit_reached` | 16 connectors are already attached. Increase `connectors.maxConnectors` in `cortex.yml`. |
| `rate_limited` | More than 120 inbound messages per minute. Implement a send queue with backoff; session stays open. |
| `message_too_long` | Message text exceeds `maxMessageLength`. Truncate or split before sending. |
| `frame_too_large` | The raw WebSocket frame exceeds 1 MiB. Chunk large content. |
| `media_not_supported` | You sent attachments without `capabilities.media: true` in `hello`, or the operator set `connectors.media.enabled: false`. |
| `attachment_too_large` | The image exceeds `maxAttachmentBytes`, or exceeds `maxInlineBytes` when inlined. Upload it and send a handle instead. |
| `attachment_type_not_allowed` | The type is outside `allowedMimeTypes`, or the bytes are not actually the type you declared. The Bridge sniffs magic bytes; renaming a file does not work. |
| `attachment_not_found` | The handle is unknown, expired (10 min), already consumed (handles are single-use), or the Bridge restarted. Re-upload. |
| HTTP 401 on the attachment endpoints | Missing or wrong `Authorization: Bearer <token>`. Use the token from your `paired` frame, not the pairing code. |
| HTTP 404 on `GET /api/connectors/attachments/{handle}` | Unknown, expired, consumed, or another connector's handle — deliberately indistinguishable. |
| HTTP 429 on upload | Upload rate limit (`maxUploadsPerMinute`, default 30). |
| HTTP 507 on upload | Your channel's storage quota is full. Wait for handles to expire or be consumed. |
| No `stream` frames arriving | `capabilities.streaming` was `false` or absent in `hello`. Reconnect with `streaming: true`. |
| No attachments arriving on `outbound` | `capabilities.media` was `false` or absent in `hello`. Note the agent only attaches media on proactive sends, not on every reply. |
| Replayed messages have no attachments | Expected. Replay is text-only — see [Media attachments](#media-attachments). |
| No replay despite sending `sinceCursor` | Cursor is unparseable (must be ISO-8601), older than `MaxAge` (24 h), or the hub client is not connected. Check logs for `Connector replay:` warnings. |
