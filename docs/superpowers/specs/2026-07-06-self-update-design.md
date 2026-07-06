# Cortex self-update — design

Status: draft (feature/self-update)
Date: 2026-07-06

## Goal

Let cortex fix and update its own running code with no human in the deploy step:
`propose → build → test-gate → deploy(MSIX + container images) → runtime-verify → auto-rollback-on-fail → report`.

This session already proved the *fix* half is autonomous (coda resumed a crashed session,
completed the cron feature, and shipped it via PR #2). This feature closes the *deploy* half —
safely.

## The hard constraint (why this is non-trivial)

`coda serve` is a child of the Bridge, enrolled in a Windows Job Object with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and **no breakaway** (`WindowsJobProcessGroup`). So when
`Add-AppxPackage -ForceApplicationShutdown` stops the Bridge, the OS force-kills coda **and every
process coda is running** — including the upgrade command itself. **coda cannot be the process that
restarts its own parent.**

Cortex is also two artifacts, and OS-native updaters (Store, App Installer) only ever touch one:

- **MSIX** — Bridge + Launcher + bundled coda (host).
- **Container images** — `cortex-agent`, voice sidecars (Docker).

So the deploy must be a **detached orchestrator** that (a) lives outside the Bridge's Job Object and
(b) coordinates *both* artifacts. That orchestrator is this feature; it is the load-bearing 80% of
any future distribution story (Store/App Installer can only ever do the MSIX half).

## Architecture

```
agent tool  cortex_self_update  (Agent.Host, runs in Docker)
      │  invokes over SignalR (like the coding_* tools)
      ▼
IAgentHub.ScheduleSelfUpdate  →  Bridge handler (host)
      │  registers a ONE-SHOT Scheduled Task (Task Scheduler service owns it →
      │  NOT in coda's Job Object → survives the Bridge/coda death), then returns
      ▼
Task Scheduler (~20s later)  →  Self-Update.ps1 -Apply   (detached)
      1. git pull <ref>                         (resolve sources)
      2. Build-All.ps1                          (agent+voice-id images + signed MSIX + manifest)
      3. dotnet test  → GATE (abort if red, before touching anything)
      4. verify update-manifest.json            (sha256 + Authenticode thumbprint + version)
      5. docker compose up -d --force-recreate cortex-agent voice-id
      6. Add-AppxPackage -ForceApplicationShutdown  (AppXSvc does the real install; survives)
      7. Start-Process  <AUMID>                  (relaunch Bridge via Launcher)
      8. poll /health until version==target && healthy  (RUNTIME verify)
      9. on failure → ROLLBACK: reinstall last-known-good MSIX + previous image tags, verify again
     10. write update-status.json {ok, fromVersion, toVersion, rolledBack, ts, log}
      ▼
new Bridge boots → reads update-status.json → proactive message via IProactiveMessageDispatcher:
      "✅ Upgraded to vX"  /  "⚠️ Update failed, rolled back to vY"
```

## The build→deploy contract: a version-keyed manifest

The deploy never globs `artifacts\*.msix`. `Build-Launcher.ps1` emits, next to the signed package,
`artifacts/update-manifest.json`:

```json
{
  "version": "0.2.310",
  "gitCommit": "…",
  "msix":   { "path": "artifacts/CortexLauncher-0.2.310.msix", "sha256": "…", "certThumbprint": "…" },
  "images": { "cortex-agent": "cortex-agent:0.2.310", "voice-id": "voice-id:0.1.0" },
  "builtAt": "…Z"
}
```

Before `Add-AppxPackage`, the pipeline **verifies**, and refuses on any mismatch:

1. file sha256 == `msix.sha256` (not stale/truncated);
2. Authenticode signature thumbprint == `msix.certThumbprint` (really our cert);
3. version in the MSIX AppxManifest == `manifest.version`;
4. the test-gate passed.

This is what makes "which MSIX to pick" a verifiable contract instead of a filename lottery — the
single failure mode that would otherwise brick the Bridge (installing a stale/unsigned/half-written
package).

## Safety model

- **Dry-run by default.** `Self-Update.ps1` runs steps 1–4 (pull/build/test/verify) and stops before
  any deploy unless `-Apply` is passed. The agent tool schedules it `-Apply`; humans run it bare.
- **Test-gate before any mutation.** A red build aborts *before* containers or the MSIX are touched.
- **Auto-rollback to last-known-good.** Keep the previously-installed signed MSIX + prior image tags.
  If `/health` doesn't return the target version healthy within the budget, reinstall them. A bad
  self-update that couldn't roll back = a bricked agent that can't fix itself because it's down.
- **Recovery floor the agent can't corrupt.** The rollback decision + last-known-good live in the
  detached script / Launcher, *outside* what the agent edits — don't let it modify its own seatbelt.
- **Concurrency guard.** Refuse if an update task is already scheduled/running (lock file).
- **Provenance.** Every self-change already traces git commit → PR → tests → manifest → status file.
- **Prereqs (documented).** Task runs "only when the user is logged on" — per-user `Add-AppxPackage`,
  the AUMID relaunch, and Docker Desktop all need an interactive session.

## Release-aware from day one

The pipeline is factored so the only step that differs between *dev self-update* and *end-user
auto-update* is **resolve version → artifacts**:

- **Now (build-from-source):** `git pull` + `Build-All` produce the artifacts + manifest locally.
- **Later (pull-a-release):** CI on tag publishes images to GHCR and attaches the signed MSIX +
  manifest to a GitHub Release; the resolver *downloads* them instead of building. Same
  verify/deploy/rollback/report half. (App Installer `.appinstaller` can then auto-update the MSIX;
  the Bridge still pulls the matching image tags — the half the OS updater can't do.)

## Increments

1. **Spec** (this doc).
2. **Manifest emission** — `Build-Launcher.ps1` writes `update-manifest.json`. Low risk.
3. **`Self-Update.ps1`** — the full pipeline, **dry-run default**; deploy/rollback behind `-Apply`.
   Safe to build + exercise without ever deploying.
4. **Agent tool + hub + Bridge scheduler** (TDD the C# orchestration) + status→proactive report.
   The *armed* step — last, after review.

## Test plan

- C# (TDD): manifest model round-trip; the scheduling/guard/status orchestration (Task Scheduler +
  Add-AppxPackage behind an interface, so decision logic is unit-tested; concurrency guard; status
  parsing → report).
- PowerShell: `Self-Update.ps1 -WhatIf/-DryRun` exercised end-to-end (pull/build/test/verify) with
  **no** deploy; manifest-verify unit-checked with a tampered file (sha/thumbprint/version mismatch
  each rejected); rollback path exercised with an intentionally-unhealthy target (in a scratch env).
- Runtime: the health-gate must assert real behavior, not just "it built" — the cron permission bug
  (installed-but-not-running) is the standing reminder that a build gate is insufficient.
