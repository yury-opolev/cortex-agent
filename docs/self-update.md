# Deploying a cortex change (self-update)

How to get the **running** cortex install onto a code change you've built. This is safe to run from
a coda coding session — coda runs on the host, so it can trigger the deploy directly (see "Why
detached" below).

Design details: [docs/superpowers/specs/2026-07-06-self-update-design.md](superpowers/specs/2026-07-06-self-update-design.md).

## The recipe

```powershell
# Deploy the current working tree: builds, test-gates, verifies, then schedules the detached deploy.
./scripts/Self-Update.ps1 -Schedule -SkipPull
```

That is the whole recipe. The script owns build → test-gate → verify → schedule and deploys exactly
the version it just built (`Build-All` runs inside it and prints the new version). To update this
machine to the latest `origin/main` instead of the working tree, drop `-SkipPull`.

**Do not run `Build-All.ps1` first and then pass `-TargetVersion`.** `Build-All` bumps
`version.json` on every run, so that sequence builds twice and throws the first version away.
`-TargetVersion` is a pin over *already-built* artifacts and therefore requires `-SkipBuild`; using
it with a build fails fast with an explanatory error, because a build always mints a new version and
so can never match a version pinned beforehand.

`-Schedule` verifies the manifest (sha256 + Authenticode signature + cert thumbprint), runs the
**test gate**, then registers a one-shot `CortexSelfUpdate` Scheduled Task that fires in ~45s and
runs the deploy detached. It returns immediately — after that, tell the user "restarting into v\<X>
in ~45s" and **expect this session to be killed** when the Bridge restarts (that is normal — the
coding session ends; the deploy continues on its own). The scheduled task re-verifies the exact
version it was given, so a rebuild during the delay window makes it refuse rather than ship the
wrong artifacts.

## The test gate

The gate runs every `tests/*.Tests` project — currently 10, ~4200 tests.

The live-LLM **evaluation** suites (`Cortex.Contained.Evals`, `Cortex.Contained.ScenarioEvals`) are
**excluded by default**: they call real providers and require `EVAL_LLM_API_KEY` /
`SCENARIO_EVAL_BRIDGE_PASSWORD`, so on a machine without those they fail for environmental reasons
and would block every deploy. Pass `-IncludeEvals` to run them where the credentials exist. The
memory evals also need an embeddings endpoint: set `EVAL_OLLAMA_ENDPOINT` (the embeddings container
publishes no port; see `tests/Cortex.Contained.Evals/README.md` and `docker-compose.eval.yml`).

Selecting `*.Tests` also keeps `Cortex.Contained.Evals.Setup` — a UI app that lives under `tests/`
but is not a test project — out of the gate, and means a newly added unit-test project is picked up
automatically.

## What the deploy does (detached, automatic)

1. re-verify the pinned manifest (sha256 + signature) — refuse on any mismatch; when the manifest pins
   the reranker image, its tag must still point at the recorded image id;
2. `docker compose up -d --force-recreate cortex-agent voice-id` (new images);
3. `Add-AppxPackage -ForceUpdateFromAnyVersion -ForceApplicationShutdown` (installs the MSIX; stops
   the old Bridge — this is what kills the coding session);
4. relaunch the Bridge and poll `/health` until it reports the target version, healthy; if the
   reranker sidecar was running, also wait until the new Bridge has recreated it on the pinned
   image and its healthcheck passes (see below);
5. **auto-rollback** to the last-known-good MSIX + previous images (agent, and reranker when it was
   running) if it doesn't come back healthy;
6. write `artifacts/update-status.json` with the result.

Check the outcome afterward: `Invoke-RestMethod http://localhost:5080/health` (new `version`) and
`artifacts/update-status.json`.

## The reranker sidecar (opt-in)

`cortex-reranker` (episodic history increment 5) is covered only once this machine has built it; a
machine that never ran `Build-RerankerImage.ps1` sees no new build step, manifest entry or gate.

- **Build and pin.** `scripts/Build-RerankerImage.ps1` builds `docker/reranker` (llama.cpp server
  pinned by digest, Qwen3-Reranker GGUF pinned by sha256) as `cortex-reranker:<version>` + `:latest`.
  After that first build, `Build-All` (`-WithReranker` forces it) rebuilds it on every run — a cache hit
  unless a pin changed — and `Build-Launcher` records `images.cortex-reranker` and its image id in the
  manifest's `imageIds`, which the deploy verifies.
- **Upgrade.** The deploy does not touch the container itself: the new Bridge runs
  `docker compose --profile reranker up -d reranker` with the DPAPI-held key on startup, and compose
  recreates the container because `cortex-reranker:latest` changed. The health gate then waits until the
  container runs the pinned image id and its healthcheck (`/health`) passes; a reranker that never gets
  there fails the gate and triggers the rollback. If the Bridge has `Reranker:Enabled=false` it stops the
  sidecar instead, and there is nothing to gate. The Bridge runs compose against
  `%LOCALAPPDATA%\Cortex\docker-compose.yml`, which the Launcher refreshes from the package's bundled
  `docker-compose.yml` on **every** start (not only when it starts containers), so the relaunch after the
  MSIX install already carries the `reranker` service.
- **Rollback.** Before deploying, the image of the *running* sidecar is tagged `cortex-reranker:rollback`;
  a rollback restores it as `:latest`, and the last-known-good Bridge recreates the sidecar from it.
- **Downgrade.** Check out the older commit in the working tree and run `Self-Update.ps1 -Schedule
  -SkipPull`: its Dockerfile pins the older server and model, the build produces that image (a cache hit
  if it still exists), and the manifest pins it. A quick manual downgrade between deploys:
  `docker tag cortex-reranker:<older version> cortex-reranker:latest`, then restart the Bridge (the build
  keeps the two newest versioned tags).
- **Drill.** `Self-Update.ps1 -RollbackDrill -SkipBuild -TargetVersion <installed>` exercises the
  reranker snapshot/restore together with the rest of the rollback.
- **Opting out.** `docker image rm cortex-reranker:latest cortex-reranker:<version>`: the next build
  no longer includes it.

## Why detached (do NOT deploy inline from coda)

coda `serve` is a child of the Bridge, in a Windows Job Object with `KILL_ON_JOB_CLOSE`. Stopping the
Bridge (which the MSIX install does) instantly kills coda **and any command coda is running** — so
coda must **schedule** the deploy to a Scheduled Task (owned by Task Scheduler, outside the job) and
let itself be replaced. Do not run `Self-Update.ps1 -Apply` directly from a coda `run_command`; use
`-Schedule`.

## Modes

| Command | Effect |
|---|---|
| `Self-Update.ps1 -Schedule -SkipPull` | **the usual one** — build the working tree, gate, verify, schedule the detached deploy (**use this from coda**) |
| `Self-Update.ps1 -Schedule` | same, but pulls `origin/main` first |
| `Self-Update.ps1` (no flags) | **dry-run** — pull/build/test/verify, deploy nothing |
| `Self-Update.ps1 -Apply` | deploy **inline** (for a human/scratch env; will restart the Bridge in-process) |
| `-SkipPull` | build/deploy the current working tree instead of pulling `origin/main` |
| `-SkipBuild` | reuse the existing artifacts/manifest (no rebuild, no version bump) |
| `-TargetVersion X` | pin an already-built version; **requires `-SkipBuild`** |
| `-IncludeEvals` | also run the credential-bound live-LLM eval suites in the gate |
| `-MsixPath <file>` | pin an explicit MSIX file (default: the manifest's) |

## Notes / prerequisites

- Runs per-user and needs an interactive logged-on session (per-user `Add-AppxPackage`, the AUMID
  relaunch, and Docker Desktop all require it). The Scheduled Task is registered "run when logged on".
- Concurrency-guarded: it refuses to schedule/run if an update is already in progress.
- Only the **MSIX** (Bridge/Launcher/bundled coda) and **Docker images** (agent/voice, and the
  reranker sidecar once this machine built it) are updated; there is no separate step needed —
  `Build-All` + the deploy cover them.
