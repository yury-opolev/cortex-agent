# What is Cortex?

Cortex is a personal AI assistant you run yourself. It talks to you across several
channels — a built-in web chat, Discord, and voice — and it remembers what matters
across conversations.

## Two processes

Cortex runs as **two cooperating processes**:

- **Bridge** — runs on your Windows host. It serves this web UI (on
  `http://127.0.0.1:5080` by default), manages your channels (Web Chat, Discord,
  Voice), stores message history, and keeps your secrets encrypted on the host
  using Windows DPAPI. The Bridge is the part you configure.
- **Agent** — runs inside a Docker container. This is the AI runtime: it builds the
  prompt, calls the language model, runs tools, and owns the long-term memory
  system. The Agent holds **no stored secrets** — credentials are pushed to it from
  the Bridge at startup and kept only in memory.

The two talk to each other over a local SignalR connection. A small **Launcher**
supervises the Bridge process and restarts it when needed (for example, after you
change a setting that requires a restart).

## Why split it this way?

Keeping the AI runtime in a container sandboxes tool execution (file access, shell
commands) away from your host, while the Bridge stays on Windows where it can reach
your microphone, speakers, and the encrypted secret store. You get host integration
where it's useful and isolation where it's safer.

## What you can do from this UI

- **Chat** with the assistant directly.
- Manage **tenants** (separate assistant identities / users).
- Configure **LLM providers**, **channels**, **speech**, and **memory** under
  Global Settings.
- Review **message history** and **stored memories** per tenant.

## Next steps

- New here? See [First-Run Setup](help:first-run).
- Want to know how to actually interact with it? See [Talking to Cortex](help:talking).
- Curious how memory and channels work under the hood? See the **How It Works** section.
