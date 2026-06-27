# Web Chat

Web Chat is the conversation you have right here in the Cortex web UI, reachable from
the **Chat** item in the sidebar.

## Key facts

- It's the **default channel** and is always enabled — there's no toggle to turn it
  off.
- Replies **stream** token-by-token as the model generates them.
- It renders **markdown** (headings, lists, tables) and **syntax-highlighted code
  blocks**, so formatted answers and code look right.
- A connection indicator in the sidebar shows whether the UI is connected to the
  Bridge.

## Access

The web UI listens on `http://127.0.0.1:5080` by default and is bound to loopback
(`127.0.0.1`) so it's only reachable from the host. If you expose it beyond the host,
do so behind a reverse proxy — see your deployment's security notes. The UI is behind
authentication; the login page is served without auth so you can sign in.

## History

Conversations are persisted. You can browse past messages per tenant under
**Tenant → History**, drill into a specific channel's conversation, and clear history
(all, or older-than a chosen date).
