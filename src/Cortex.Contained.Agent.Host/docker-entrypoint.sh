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

    # Non-dev: drop to app user and exec the command
    exec runuser -u app -- "$@"
fi

# Already running as non-root (production) — just exec
exec "$@"
