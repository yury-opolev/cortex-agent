# Scheduler & Reminders

Cortex can do things later, not just now. Ask in plain language — "remind me at 9am
tomorrow to…", "every Monday, summarize…" — and it schedules the work.

## One-shot and recurring

The scheduler supports two kinds of tasks:

- **One-shot** — runs once at a specific time.
- **Recurring** — runs on a **cron** schedule (e.g. every morning, every Monday).

Scheduled tasks are persisted (SQLite-backed) inside the Agent, so they survive
restarts. A background ticker checks for due tasks on a short interval.

## How reminders reach you

When a task fires, Cortex delivers the result through the appropriate channel. For
voice, it can deliver a spoken cue; for text channels, it sends a message. Because
delivery is proactive (you didn't just send a message), voice delivery uses the same
ring/invite path described in [Cross-Channel Transfer](help:cross-channel-transfer)
when you're not already present.

## Managing tasks

Just ask the assistant what's scheduled, or to change or cancel a task. Scheduling is
driven conversationally rather than through a settings form.
