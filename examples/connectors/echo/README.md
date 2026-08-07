# Echo Connector

A minimal, zero-dependency Cortex connector written in a single Node.js file.
It demonstrates the full connector lifecycle — pairing, replay, streaming, and
reconnection — and serves as the reference example for `docs/connector-plugin-system.md`.

## Requirements

- **Node.js 22+** (uses the built-in global `WebSocket` added in Node 21, stable in Node 22)
- A running Cortex Bridge on `ws://127.0.0.1:5080/connector`

## Running

```sh
node connector.mjs
```

## First run (no token.json)

1. The connector connects and sends a `hello` frame.
2. The Bridge responds with `pairing_required` containing a short code (e.g. `A3F7`).
3. The connector prints the code prominently:

   ```
   ============================================================
     PAIRING REQUIRED
     Code: A3F7
     Expires at: 2:05:00 PM
     Go to Cortex → Global Settings → Connectors
     and approve the request with the matching code.
   ============================================================
   ```

4. In the Cortex Web UI go to **Global Settings → Connectors**.  A pending request
   appears with the same code.  Verify the codes match and click **Approve**.
5. The Bridge sends `paired` (token) then `ready`.  The connector saves the token
   and cursor to `token.json` next to the script.

## Subsequent runs (token.json present)

The connector presents the saved token in `hello`, skipping the pairing flow entirely.
If a cursor was saved from a previous session, missed messages are replayed immediately
after `ready` (you will see the replay count logged).

## Sending messages

Once `Ready on channel plugin:echo:default` is printed, type any line and press Enter.
The message is sent to Cortex as an `inbound` frame.  The agent's reply arrives as
streaming `stream` deltas (printed inline) followed by a final `outbound` frame.

Type `/abort` to cancel an in-flight generation.

## Reconnection

The connector reconnects automatically with exponential backoff (2 s → 4 s → … → 60 s)
when the socket closes for any reason **except** `pairing_denied`.  After a `pairing_denied`
the process exits with code 1.

## Files

| File | Purpose |
|---|---|
| `connector.mjs` | The connector itself |
| `token.json` | Created on first successful pairing; contains `token`, `channelId`, and `cursor` |
