/**
 * Echo Connector — minimal Cortex connector example.
 *
 * Connects to the Cortex Bridge WebSocket endpoint, completes the pairing flow
 * on first run, then echoes every line typed on stdin back to Cortex as an
 * inbound message.  Outbound (agent) replies are printed to stdout.
 *
 * Zero dependencies — uses the global WebSocket available in Node 22+.
 *
 * Protocol reference: docs/connector-plugin-system.md
 */

import { createInterface } from "node:readline";
import { readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

// ── Configuration ────────────────────────────────────────────────────────────

const BRIDGE_URL = "ws://127.0.0.1:5080/connector";

// Attachment transfer endpoints. Same host and port as the WebSocket; these are the only
// /api/connectors/* routes that accept a connector token rather than the Web UI session.
const ATTACHMENTS_URL = "http://127.0.0.1:5080/api/connectors/attachments";


// The connector's unique type key.  Must be [a-z0-9_-], 1–64 chars.
const KEY = "echo";

// The instance identifier — allows one key to run multiple channels.
// Defaults to "default" when omitted in hello.
const INSTANCE_ID = "default";

// Capabilities advertised to the Bridge in the hello frame.
const CAPABILITIES = {
  streaming: true,        // receive stream + typing frames
  richText: true,         // we'll render Markdown as plain text
  media: true,            // send and receive image attachments
  maxMessageLength: 100000,
};

// Reconnect backoff: starts at 2 s, doubles each attempt, caps at 60 s.
const BACKOFF_INITIAL_MS = 2_000;
const BACKOFF_MAX_MS = 60_000;

// ── Persistent state (token.json next to this file) ─────────────────────────
//
// SECURITY — READ THIS BEFORE COPYING THIS PATTERN.
//
// The durable token is a bearer credential: anything holding it is accepted as
// this connector without pairing again. This example stores it as plain JSON
// because it must run unmodified on any OS with no dependencies, but that means
// ANY process running as the same user can read it and impersonate you.
//
// A production connector should hand the token to the platform secret store
// instead — Windows DPAPI or Credential Manager, macOS Keychain, or the Linux
// Secret Service / libsecret. Cortex itself stores its copy DPAPI-encrypted.
//
// The file is created owner-only (0o600) below, which is meaningful on Unix and
// a no-op on Windows, so do not rely on it as the control.

const __dir = dirname(fileURLToPath(import.meta.url));
const STATE_FILE = join(__dir, "token.json");

// Received images are written here.
const DOWNLOAD_DIR = join(__dir, "received");

function loadState() {
  try {
    if (existsSync(STATE_FILE)) {
      return JSON.parse(readFileSync(STATE_FILE, "utf8"));
    }
  } catch {
    // Ignore — treat as first run.
  }
  return {};
}

function saveState(state) {
  writeFileSync(STATE_FILE, JSON.stringify(state, null, 2), { encoding: "utf8", mode: 0o600 });
}

// ── Frame helpers ─────────────────────────────────────────────────────────────

/**
 * Serialise a frame to JSON.  The wire format is always:
 *   { "type": "...", "payload": { ... } }
 */
function makeFrame(type, payload = {}) {
  return JSON.stringify({ type, payload });
}

// ── Main loop ────────────────────────────────────────────────────────────────

let backoffMs = BACKOFF_INITIAL_MS;

// Shared readline interface so we only create it once across reconnects.
const rl = createInterface({
  input: process.stdin,
  terminal: false,
});

// Accumulate stdin lines while not yet in steady state (before "ready").
let pendingLines = [];
let sendLine = null; // set once steady state is reached

rl.on("line", (line) => {
  if (sendLine) {
    sendLine(line);
  } else {
    pendingLines.push(line);
  }
});

async function connect() {
  const state = loadState();

  console.log(`\n[echo] Connecting to ${BRIDGE_URL} …`);

  let ws;
  try {
    ws = new WebSocket(BRIDGE_URL);
  } catch (err) {
    console.error(`[echo] Failed to create WebSocket: ${err.message}`);
    scheduleReconnect();
    return;
  }

  // Whether pairing was explicitly denied — do not reconnect in that case.
  let pairingDenied = false;

  // Track the streaming text for the current turn so we can print deltas inline.
  let streamingConversation = null;

  // Tracks whether the handshake completed. The ready frame is what makes
  // invalid_payload recoverable rather than fatal - see isFatalError.
  let established = false;

  ws.addEventListener("open", () => {
    backoffMs = BACKOFF_INITIAL_MS; // reset backoff on successful connect

    // ── Protocol step 1: send hello ───────────────────────────────────────
    // The hello frame must be sent within 10 seconds of connecting.
    const hello = {
      key: KEY,
      instanceId: INSTANCE_ID,
      displayName: "Echo Connector",
      version: "1.0.0",
    };

    // If we have a saved token, include it — this skips the pairing flow.
    if (state.token) {
      hello.token = state.token;
    }

    // If we have a saved cursor, include it — this triggers replay of missed messages.
    if (state.cursor) {
      hello.sinceCursor = state.cursor;
    }

    hello.capabilities = CAPABILITIES;

    ws.send(makeFrame("hello", hello));
    console.log("[echo] hello sent");
  });

  ws.addEventListener("message", ({ data }) => {
    let frame;
    try {
      frame = JSON.parse(data);
    } catch {
      console.error("[echo] Received non-JSON frame — ignoring.");
      return;
    }

    const { type, payload = {} } = frame;

    switch (type) {
      // ── Protocol step 2a: pairing required (first run) ──────────────────
      case "pairing_required": {
        const { code, expiresAt } = payload;
        const expiry = new Date(expiresAt).toLocaleTimeString();
        console.log("\n" + "=".repeat(60));
        console.log("  PAIRING REQUIRED");
        console.log(`  Code: ${code}`);
        console.log(`  Expires at: ${expiry}`);
        console.log("  Go to Cortex → Global Settings → Connectors");
        console.log("  and approve the request with the matching code.");
        console.log("=".repeat(60) + "\n");
        // The Bridge will send "paired" (or "pairing_denied") once the human acts.
        break;
      }

      // ── Protocol step 2b: pairing approved ──────────────────────────────
      case "paired": {
        const { token, channelId } = payload;
        state.token = token;
        state.channelId = channelId;
        saveState(state);
        console.log(`[echo] Paired!  Channel id: ${channelId}`);
        console.log("[echo] Token saved to token.json");
        // "ready" will arrive immediately after "paired".
        break;
      }

      // ── Protocol step 2c: pairing denied ────────────────────────────────
      case "pairing_denied": {
        const { reason } = payload;
        console.error(`[echo] Pairing denied: ${reason}`);
        if (reason === "pairing_rate_limited") {
          console.error("[echo] Too many pairing attempts.  Wait 10 minutes.");
        }
        pairingDenied = true;
        ws.close();
        break;
      }

      // ── Protocol step 3: ready — entering steady state ──────────────────
      case "ready": {
        const { channelId, replayCount } = payload;
        console.log(`[echo] Ready on channel ${channelId}`);
        if (replayCount > 0) {
          console.log(`[echo] ${replayCount} missed message(s) will be replayed.`);
        }
        // Flush lines typed before we were ready.
        sendLine = (line) => sendInbound(ws, line, state);
        for (const line of pendingLines) {
          sendLine(line);
        }
        pendingLines = [];
        console.log('[echo] Type a message and press Enter.  Type "/abort" to cancel.');
        break;
      }

      // ── Steady state: typing indicator ───────────────────────────────────
      case "typing": {
        process.stdout.write("\r[echo] Agent is typing…        \r");
        break;
      }

      // ── Steady state: streaming delta ─────────────────────────────────────
      case "stream": {
        const { conversationId, delta } = payload;
        if (streamingConversation !== conversationId) {
          if (streamingConversation !== null) {
            process.stdout.write("\n"); // end previous response line
          }
          process.stdout.write("[agent] ");
          streamingConversation = conversationId;
        }
        process.stdout.write(delta);
        break;
      }

      // ── Steady state: final agent response (also replay frames) ──────────
      case "outbound": {
        const { conversationId, messageId, content, isThinking, cursor } = payload;

        if (streamingConversation === conversationId) {
          // The streaming deltas already printed the content; just end the line.
          process.stdout.write("\n");
          streamingConversation = null;
        } else {
          // Non-streaming or replay frame — print full content.
          const label = isThinking ? "[thinking]" : "[agent]";
          console.log(`${label} ${content?.text ?? ""}`);
        }

        // ── Media: an attachment arrives either inline or as a handle ─────
        // BOTH modes must be handled. The Bridge inlines what fits the frame
        // and spills anything larger to a handle, so you cannot assume `data`.
        for (const attachment of content?.attachments ?? []) {
          if (attachment.data) {
            const bytes = Buffer.from(attachment.data, "base64");
            saveAttachment(attachment, bytes);
          } else if (attachment.handle) {
            // Handles are single-use and expire — fetch promptly, once.
            fetchAttachment(attachment, state).catch((err) =>
              console.error(`[echo] attachment fetch failed: ${err.message}`),
            );
          }
        }

        // ── Critical: save the cursor so we can replay on next connect ────
        if (cursor) {
          state.cursor = cursor;
          saveState(state);
        }
        break;
      }

      // ── Liveness: reply to ping with pong ────────────────────────────────
      case "ping": {
        ws.send(makeFrame("pong"));
        break;
      }

      // ── Error frames ──────────────────────────────────────────────────────
      case "error": {
        const { code, message } = payload;
        const fatal = isFatalError(code, established);
        console.error(`[echo] ${fatal ? "FATAL " : ""}error: ${code} — ${message}`);
        if (fatal) {
          // Fatal errors: Bridge will close the socket.  We just log and let
          // the "close" event handle reconnection (or not).
        }
        // Recoverable errors (rate_limited, message_too_long, invalid_payload):
        // session remains open — do nothing special.
        break;
      }

      default:
        // Unknown frame types from the Bridge are unexpected but harmless — log and ignore.
        console.warn(`[echo] Unknown frame type from Bridge: ${type}`);
        break;
    }
  });

  // Track whether reconnect has already been scheduled so we never double-schedule.
  // Note: Node.js 22's built-in WebSocket fires "error" but NOT "close" on a
  // connection failure (this differs from browser behaviour where close always follows
  // error).  We therefore schedule reconnects from both handlers, guarded by this flag.
  let reconnectScheduled = false;

  ws.addEventListener("error", (evt) => {
    console.error(`[echo] WebSocket error: ${evt.message ?? "(no message)"}`);
    if (!pairingDenied && !reconnectScheduled) {
      reconnectScheduled = true;
      scheduleReconnect();
    }
  });

  ws.addEventListener("close", ({ code, reason }) => {
    sendLine = null; // block stdin until reconnected
    streamingConversation = null;
    established = false;

    if (pairingDenied) {
      console.error("[echo] Not reconnecting after pairing denial.");
      process.exit(1);
    }

    console.log(`[echo] Connection closed (code ${code}): ${reason || "(no reason)"}`);
    if (!reconnectScheduled) {
      reconnectScheduled = true;
      scheduleReconnect();
    }
  });
}

/** Returns true when the error code causes the Bridge to close the socket. */
function isFatalError(code, isEstablished = true) {
  const fatal = new Set([
    "malformed_frame",
    "unknown_frame_type",
    "protocol_violation",
    "frame_too_large",
    "connector_limit_reached",
    "duplicate_connector",
    "connectors_disabled",
  ]);
  if (fatal.has(code)) return true;

  // invalid_payload is the one code whose fatality depends on WHERE you are: the Bridge closes
  // the socket for a bad hello or an unparseable frame, but keeps the session open for a bad
  // inbound. Having received `ready` is the signal.
  //
  // Note there is deliberately no "not_paired" above: it is defined in the protocol but the
  // state machine makes it unreachable, so the Bridge never sends it.
  return code === "invalid_payload" && !isEstablished;
}

/**
 * Send an inbound message or handle the /abort command.
 * Called for each line from stdin once steady state is reached.
 */
function sendInbound(ws, line, state) {
  if (ws.readyState !== WebSocket.OPEN) {
    return;
  }

  // "/abort" line sends an abort frame for the default conversation.
  if (line.trim() === "/abort") {
    const conversationId = state.channelId ?? undefined;
    ws.send(makeFrame("abort", conversationId ? { conversationId } : {}));
    console.log("[echo] abort sent");
    return;
  }

  if (!line.trim()) {
    return; // ignore blank lines
  }

  // "/send <path> [caption]" uploads an image and references it by handle.
  if (line.startsWith("/send ")) {
    const [, filePath, ...captionWords] = line.trim().split(/\s+/);
    const caption = captionWords.join(" ") || undefined;

    uploadAttachment(filePath, state)
      .then((handle) => {
        ws.send(makeFrame("inbound", {
          content: {
            text: caption ?? "what is in this image?",
            attachments: [{ mimeType: guessMimeType(filePath), handle, caption }],
          },
        }));
        console.log(`[echo] sent ${filePath} as ${handle}`);
      })
      .catch((err) => console.error(`[echo] upload failed: ${err.message}`));
    return;
  }

  ws.send(makeFrame("inbound", {
    content: {
      text: line,
      isMarkdown: false,
    },
  }));
}

/** Map a file extension to one of the four allowed image types. */
function guessMimeType(filePath) {
  const ext = filePath.slice(filePath.lastIndexOf(".")).toLowerCase();
  return {
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".gif": "image/gif",
    ".webp": "image/webp",
  }[ext] ?? "image/png";
}

/**
 * Write a received attachment to disk.  fileName is UNTRUSTED metadata even though
 * the Bridge sanitises it, so we take only the base name and never join a caller
 * path directly.
 */
function saveAttachment(attachment, bytes) {
  mkdirSync(DOWNLOAD_DIR, { recursive: true });

  const safeName = (attachment.fileName ?? "image")
    .replace(/[\/\\:]/g, "")
    .replace(/^\.+/, "") || "image";
  const target = join(DOWNLOAD_DIR, `${Date.now()}-${safeName}`);

  writeFileSync(target, bytes);
  const caption = attachment.caption ? ` — ${attachment.caption}` : "";
  console.log(`[echo] saved ${bytes.length} byte ${attachment.mimeType}${caption} -> ${target}`);
}

/**
 * Fetch a handle-carried attachment over the REST endpoint.
 *
 * Handles are SINGLE-USE and expire (10 minutes by default), so fetch promptly and
 * exactly once.  A 404 means unknown, expired, already consumed, or issued to another
 * connector — the four are deliberately indistinguishable.
 */
async function fetchAttachment(attachment, state) {
  const response = await fetch(`${ATTACHMENTS_URL}/${attachment.handle}`, {
    headers: { Authorization: `Bearer ${state.token}` },
  });

  if (!response.ok) {
    throw new Error(`HTTP ${response.status} fetching attachment`);
  }

  const bytes = Buffer.from(await response.arrayBuffer());
  saveAttachment(attachment, bytes);
}

/**
 * Upload an image and return a handle to reference in an inbound frame.
 *
 * Anything over ~256 KB must travel this way: a WebSocket frame is capped at 1 MiB and
 * exceeding it is FATAL, and base64 inflates payloads by about a third.
 */
async function uploadAttachment(filePath, state) {
  const bytes = readFileSync(filePath);
  const form = new FormData();
  form.append("file", new Blob([bytes]), filePath.split(/[\\/]/).pop());

  const response = await fetch(ATTACHMENTS_URL, {
    method: "POST",
    headers: { Authorization: `Bearer ${state.token}` },
    body: form,
  });

  if (!response.ok) {
    // 415 = not an allowed image, 429 = rate limited, 507 = your storage quota is full.
    throw new Error(`HTTP ${response.status} uploading attachment`);
  }

  const { handle } = await response.json();
  return handle;
}

/** Schedule a reconnect attempt with exponential backoff. */
function scheduleReconnect() {
  const delay = backoffMs;
  backoffMs = Math.min(backoffMs * 2, BACKOFF_MAX_MS);
  console.log(`[echo] Reconnecting in ${delay / 1000} s …`);
  setTimeout(connect, delay);
}

// ── Entry point ──────────────────────────────────────────────────────────────

connect();
