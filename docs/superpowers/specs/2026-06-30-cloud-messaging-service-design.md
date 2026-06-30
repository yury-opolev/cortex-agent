# AI Messenger — Design Spec (moved to its own repo)

The **AI Messenger** cloud messaging service has its own repository, and the canonical design
spec lives there:

- **Repo:** https://github.com/yury-opolev/ai-messenger (private)
- **Spec:** `docs/design.md` in that repo

This file is intentionally a pointer (not a second full copy) so the design has a single source
of truth.

**What touches `cortex-agent`:** only the home-side **`CloudMessagingChannel`** — a new outbound
`IChannel` that dials the cloud service (like the Discord channel dials Discord), config-gated and
off by default. See §5/§10 of the spec in the `ai-messenger` repo. Work happens on the
`feature/webchat-cloud-service` branch.
