# Talking to Cortex

There's no single "right" way to talk to Cortex — it's a conversational assistant.
A few things are worth knowing.

## It remembers

Cortex extracts durable facts from your conversations and stores them as
**long-term memory**. You don't have to do anything special — over time it learns
your preferences, projects, and context. You can review and edit everything it has
remembered under a tenant's **Memory** page, and you can ask it directly to
"remember" or "forget" something. See [The Memory System](help:memory-system).

## It can act, not just answer

The Agent has tools: it can read and write files (sandboxed inside its container),
search, run commands, set reminders and scheduled tasks, and spin up sub-agents for
bigger jobs. Just ask in plain language ("remind me tomorrow at 9 to…",
"summarize this file…").

## It works across channels

A conversation isn't locked to where it started. You can say something like
"let's continue this in voice" and Cortex will carry the relevant context over to
the voice channel and greet you there. See
[Cross-Channel Transfer](help:cross-channel-transfer).

## Personality is configurable

How Cortex behaves — its name, tone, and operating style — is set per tenant under
**Tenant Settings → Personality**. It can even adjust its own personality when you
ask. See [Personality & Self-Notes](help:personality-self-notes).

## Tenants

Each **tenant** is an independent assistant context with its own personality,
memory, history, and channel links. The **default** tenant is used unless you route
to another. Multi-tenant setups are useful if several people share one Cortex
install or you want separate personas.
