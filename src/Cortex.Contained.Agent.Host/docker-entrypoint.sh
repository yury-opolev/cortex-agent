#!/bin/sh
# docker-entrypoint.sh — ensures /app/data directories are writable by the
# 'app' user and drops privileges before starting the .NET process.
#
# Problem: Docker volumes and dev mode can leave /app/data subdirectories
# owned by root.  The 'app' user cannot write to root-owned files, causing
# SQLite Error 8 ("attempt to write a readonly database").
#
# Fix: Always fix ownership of known data subdirectories at startup.
# When running as root (dev mode), use chown directly and drop privileges.
# When running as app (production), use sudo if available.

set -e

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
