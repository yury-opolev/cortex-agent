# Deploying a cortex change (self-update)

How to get the **running** cortex install onto a code change you've built. This is safe to run from
a coda coding session — coda runs on the host, so it can trigger the deploy directly (see "Why
detached" below).

Design details: [docs/superpowers/specs/2026-07-06-self-update-design.md](superpowers/specs/2026-07-06-self-update-design.md).

## The recipe

```powershell
# 1. Build the exact artifacts (Docker images + signed MSIX) and emit artifacts/update-manifest.json.
#    Version auto-bumps; note the new version it prints (e.g. 0.2.310).
./scripts/Build-All.ps1 -CertThumbprint F578A5879BE57511D40288B6DA3A0F383BD74EEE

# 2. Schedule the detached deploy of that EXACT version, then report the pending restart and exit.
./scripts/Self-Update.ps1 -Schedule -SkipPull -TargetVersion 0.2.310
```

`-Schedule` verifies the manifest for the pinned `-TargetVersion` (sha256 + Authenticode signature +
cert thumbprint), runs the **test gate**, then registers a one-shot `CortexSelfUpdate` Scheduled Task
that fires in ~45s and runs the deploy detached. It returns immediately — after that, tell the user
"restarting into v<X> in ~45s" and **expect this session to be killed** when the Bridge restarts
(that is normal — the coding session ends; the deploy continues on its own).

Only ever pass a version you actually built in step 1. If the manifest doesn't match the pinned
version, the script refuses (it will not deploy the wrong artifacts).

## What the deploy does (detached, automatic)

1. re-verify the pinned manifest (sha256 + signature) — refuse on any mismatch;
2. `docker compose up -d --force-recreate cortex-agent voice-id` (new images);
3. `Add-AppxPackage -ForceUpdateFromAnyVersion -ForceApplicationShutdown` (installs the MSIX; stops
   the old Bridge — this is what kills the coding session);
4. relaunch the Bridge and poll `/health` until it reports the target version, healthy;
5. **auto-rollback** to the last-known-good MSIX + previous image if it doesn't come back healthy;
6. write `artifacts/update-status.json` with the result.

Check the outcome afterward: `Invoke-RestMethod http://localhost:5080/health` (new `version`) and
`artifacts/update-status.json`.

## Why detached (do NOT deploy inline from coda)

coda `serve` is a child of the Bridge, in a Windows Job Object with `KILL_ON_JOB_CLOSE`. Stopping the
Bridge (which the MSIX install does) instantly kills coda **and any command coda is running** — so
coda must **schedule** the deploy to a Scheduled Task (owned by Task Scheduler, outside the job) and
let itself be replaced. Do not run `Self-Update.ps1 -Apply` directly from a coda `run_command`; use
`-Schedule`.

## Modes

| Command | Effect |
|---|---|
| `Self-Update.ps1` (no flags) | **dry-run** — pull/build/test/verify, deploy nothing |
| `Self-Update.ps1 -Schedule -TargetVersion X` | verify + test-gate, then schedule the detached deploy (**use this from coda**) |
| `Self-Update.ps1 -Apply -TargetVersion X` | deploy **inline** (for a human/scratch env; will restart the Bridge in-process) |
| `-SkipPull` | build/deploy the current working tree instead of pulling `origin/main` |
| `-MsixPath <file>` | pin an explicit MSIX file (default: the manifest's) |

## Notes / prerequisites

- Runs per-user and needs an interactive logged-on session (per-user `Add-AppxPackage`, the AUMID
  relaunch, and Docker Desktop all require it). The Scheduled Task is registered "run when logged on".
- Concurrency-guarded: it refuses to schedule/run if an update is already in progress.
- Only the **MSIX** (Bridge/Launcher/bundled coda) and **Docker images** (agent/voice) are updated;
  there is no separate step needed — `Build-All` + the deploy cover both.
