#!/bin/sh
# docker-entrypoint.sh — prepares /app/data, starts the in-container job
# scheduler, and launches the .NET agent host.
#
# Responsibilities:
#   1. Ensure /app/data subdirectories are writable by the 'app' user.
#   2. Seed + start supercronic (a rootless cron runner) in the background,
#      running as 'app' — no added runtime privileges.
#   3. exec the .NET process in the FOREGROUND so it receives SIGTERM/SIGINT
#      and shuts down gracefully (supercronic is torn down with the container).
#
# Problem: Docker volumes and dev mode can leave /app/data subdirectories
# owned by root.  The 'app' user cannot write to root-owned files, causing
# SQLite Error 8 ("attempt to write a readonly database").
#
# Fix: Always fix ownership of known data subdirectories at startup.
# When running as root (dev mode), use chown directly and drop privileges.
# When running as app (production), use sudo if available.

set -e

# ---- Job scheduler (supercronic) configuration ----
# The live crontab lives on the persistent /app/data volume so schedule edits
# survive container restarts AND image upgrades. The baked default only seeds
# it the first time. The 'app' user owns the file and can add/edit entries at
# runtime; supercronic hot-reloads it on change (-inotify).
SCHEDULER_BIN=/usr/local/bin/supercronic
SCHEDULER_CRON_DIR=/app/data/cron
SCHEDULER_CRON_FILE="$SCHEDULER_CRON_DIR/crontab"
SCHEDULER_CRON_DEFAULT=/usr/local/share/cortex/crontab.default

fix_data_ownership() {
    # Fix any root-owned files/dirs under /app/data so the app user can write.
    # This handles the case where Docker volumes retain root ownership from
    # a previous container run or image rebuild.
    if [ "$(id -u)" = "0" ]; then
        chown -R app:app /app/data 2>/dev/null || true
    elif command -v sudo >/dev/null 2>&1; then
        # Running as app with sudo available (common image tier) —
        # fix any root-owned files/dirs under /app/data.
        sudo chown -R app:app /app/data 2>/dev/null || true
    fi
}

# Run a command as the 'app' user: via runuser when we are currently root
# (dev / compose `user:` override), or directly when we are already app
# (the production default — the image's final USER is app). This guarantees
# neither the scheduler daemon nor its jobs ever gain privileges (constraint A).
run_as_app() {
    if [ "$(id -u)" = "0" ]; then
        runuser -u app -- "$@"
    else
        "$@"
    fi
}

# Seed the persistent crontab (once) and launch supercronic in the background,
# running as 'app'. Any problem here is logged and ignored: a scheduler hiccup
# must never stop the agent host from starting.
start_scheduler() {
    if [ ! -x "$SCHEDULER_BIN" ]; then
        echo "docker-entrypoint: $SCHEDULER_BIN not found; job scheduler disabled" >&2
        return 0
    fi

    # Seed from the baked default on first run only. The live crontab lives on
    # the persistent /app/data volume and is created as 'app' so it is
    # app-owned and editable at runtime (constraint C). We mkdir at runtime
    # because an existing (upgraded) volume won't contain a newly-added image
    # directory — named volumes only copy image contents when first empty.
    if [ ! -f "$SCHEDULER_CRON_FILE" ] && [ -f "$SCHEDULER_CRON_DEFAULT" ]; then
        if ! run_as_app mkdir -p "$SCHEDULER_CRON_DIR" \
                || ! run_as_app cp "$SCHEDULER_CRON_DEFAULT" "$SCHEDULER_CRON_FILE"; then
            echo "docker-entrypoint: could not seed $SCHEDULER_CRON_FILE; job scheduler disabled" >&2
            return 0
        fi
    fi

    # -inotify: hot-reload when 'app' edits the crontab at runtime (no restart).
    # -no-reap: tini (compose `init: true`) is PID 1 and already reaps zombies.
    run_as_app "$SCHEDULER_BIN" -inotify -no-reap "$SCHEDULER_CRON_FILE" &
    echo "docker-entrypoint: job scheduler (supercronic) started on $SCHEDULER_CRON_FILE" >&2
}

fix_data_ownership

if [ "$(id -u)" = "0" ]; then
    # In dev mode (dotnet watch / build stage) the bind-mounted source dirs
    # and anonymous obj/bin volumes are root-owned.  Dropping to 'app' would
    # cause permission errors during restore/build.  Detect dev mode by
    # checking if the command is "dotnet watch" and stay as root.
    case "$*" in
        *"dotnet watch"*|*"dotnet build"*)
            exec "$@"
            ;;
    esac

    # Non-dev root invocation: start the scheduler as app in the background,
    # then drop to app and exec the command in the foreground.
    start_scheduler
    exec runuser -u app -- "$@"
fi

# Already running as non-root app (production default): start the scheduler in
# the background, then exec the command in the FOREGROUND so it (dotnet) is the
# signal-receiving process and shuts down gracefully on SIGTERM/SIGINT.
start_scheduler
exec "$@"
