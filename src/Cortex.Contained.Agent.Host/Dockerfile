FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5100

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy entrypoint script (used by dev mode via docker-compose.override.yml)
COPY ["src/Cortex.Contained.Agent.Host/docker-entrypoint.sh", "/usr/local/bin/docker-entrypoint.sh"]
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# Copy project files for restore
COPY ["src/Cortex.Contained.Agent.Host/Cortex.Contained.Agent.Host.csproj", "src/Cortex.Contained.Agent.Host/"]
COPY ["src/Cortex.Contained.Contracts/Cortex.Contained.Contracts.csproj", "src/Cortex.Contained.Contracts/"]
COPY ["lib/memory-mcp/src/MemoryMcp.Core/MemoryMcp.Core.csproj", "lib/memory-mcp/src/MemoryMcp.Core/"]
COPY ["lib/memory-mcp/Directory.Build.props", "lib/memory-mcp/"]
COPY ["lib/Directory.Packages.props", "lib/"]
COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
RUN dotnet restore "src/Cortex.Contained.Agent.Host/Cortex.Contained.Agent.Host.csproj"

# Copy source and build (restore is cached from layer above; NuGet audit
# runs here and requires network access)
COPY src/ src/
COPY lib/memory-mcp/src/MemoryMcp.Core/ lib/memory-mcp/src/MemoryMcp.Core/
WORKDIR /src/src/Cortex.Contained.Agent.Host
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ============================================================
# TIER 1 — Base image (minimal: curl, python3, jq)
# Build:  docker compose build                (with target: final)
# ============================================================
FROM base AS final
WORKDIR /app

# Install curl for healthcheck, Python3 for agent scripts, and basic utilities
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    python3 \
    python3-pip \
    jq \
    && rm -rf /var/lib/apt/lists/*

# Run as non-root user (app user is pre-created in the aspnet base image)
# Pre-create all data subdirectories so they're owned by app from the start.
# Without this, subdirectories created at runtime via volume mounts can end up
# owned by root, causing "readonly database" errors for SQLite stores.
RUN mkdir -p /app/data /app/data/logs \
             /app/state /app/state/scheduler /app/state/maintenance /app/state/memory \
             /app/config && \
    chown -R app:app /app
USER app

COPY --from=publish --chown=app:app /app/publish .
COPY --from=build --chmod=755 /usr/local/bin/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

HEALTHCHECK --interval=30s --timeout=5s --retries=3 --start-period=10s \
  CMD curl -f http://localhost:5100/health || exit 1

ENTRYPOINT ["docker-entrypoint.sh"]
CMD ["dotnet", "Cortex.Contained.Agent.Host.dll"]

# ============================================================
# TIER 2 — Common image (full dev toolchain + Homebrew)
# Build:  docker compose build                (with target: common)
#
# Pre-installs Node.js, npm, git, build tools, ripgrep, and
# more.  Homebrew (linuxbrew) is available so the agent can
# install additional packages at runtime without root:
#   brew install <package>
#   pip install --user <package>
#   npm install -g <package>
# ============================================================
FROM final AS common
USER root

ENV DEBIAN_FRONTEND=noninteractive

# ---- System packages ----
RUN apt-get update && apt-get install -y --no-install-recommends \
    # Network & download
    wget \
    ca-certificates \
    openssh-client \
    dnsutils \
    # Version control
    git \
    # Build toolchain
    build-essential \
    pkg-config \
    # JavaScript runtime (Debian bookworm ships Node 18 LTS)
    nodejs \
    npm \
    # Python extras
    python3-venv \
    # Search & text processing
    ripgrep \
    gawk \
    # Archive & compression
    unzip \
    zip \
    # System utilities
    procps \
    file \
    less \
    # Needed by Homebrew installer
    sudo \
    && rm -rf /var/lib/apt/lists/*

# ---- Playwright (Chromium) ----
# Install system dependencies + browser at build time so the agent
# can use Playwright at runtime without needing root/apt-get.
# Browser stored in shared /opt/playwright so the non-root app user can access it.
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/playwright
RUN pip install --break-system-packages playwright && \
    playwright install --with-deps chromium && \
    chmod -R o+rx /opt/playwright && \
    rm -rf /var/lib/apt/lists/*

# ---- Set up app user with a proper home directory and login shell
#      (the aspnet base image creates the user without these) ----
RUN mkdir -p /home/app && \
    usermod -d /home/app -s /bin/bash app && \
    chown -R app:app /home/app

# ---- npm global prefix (works without root) ----
RUN mkdir -p /home/app/.npm-global && \
    chown -R app:app /home/app/.npm-global
ENV NPM_CONFIG_PREFIX=/home/app/.npm-global

# ---- Homebrew (linuxbrew) — enables non-root runtime package installs ----
ENV HOMEBREW_PREFIX=/home/linuxbrew/.linuxbrew \
    HOMEBREW_CELLAR=/home/linuxbrew/.linuxbrew/Cellar \
    HOMEBREW_REPOSITORY=/home/linuxbrew/.linuxbrew/Homebrew

# Install Homebrew as the linuxbrew user (mirrors the OpenClaw pattern).
# Files stay owned by linuxbrew — they are world-readable and world-
# executable by default so the 'app' user can run brew directly.
# For runtime 'brew install', we add app to the linuxbrew group and
# grant group-write on the prefix.
RUN useradd -m -s /bin/bash linuxbrew && \
    mkdir -p "${HOMEBREW_PREFIX}" && \
    chown -R linuxbrew:linuxbrew "$(dirname "${HOMEBREW_PREFIX}")" && \
    su - linuxbrew -c \
      "NONINTERACTIVE=1 CI=1 /bin/bash -c \"\$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"" && \
    test -x "${HOMEBREW_PREFIX}/bin/brew" || { echo "Homebrew install failed"; exit 1; } && \
    # Grant group-write so app (added to linuxbrew group) can 'brew install'
    chmod -R g+w "${HOMEBREW_PREFIX}" && \
    find "${HOMEBREW_PREFIX}" -type d -exec chmod g+s {} + && \
    usermod -aG linuxbrew app && \
    ln -sf "${HOMEBREW_PREFIX}/bin/brew" /usr/local/bin/brew

# ---- Allow app user to fix /app/data ownership without password ----
# The entrypoint uses this to fix root-owned files in Docker volumes
# that persist across container rebuilds.
RUN printf 'app ALL=(root) NOPASSWD: /bin/chown\n' \
    > /etc/sudoers.d/app-data-fix && \
    chmod 440 /etc/sudoers.d/app-data-fix

# ---- Switch back to non-root app user ----
USER app

# Hardcode paths — Docker ENV interpolation of earlier ENV vars is unreliable
ENV HOME=/home/app \
    PATH=/home/linuxbrew/.linuxbrew/bin:/home/linuxbrew/.linuxbrew/sbin:/home/app/.npm-global/bin:/home/app/.local/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

# Allow pip install without --break-system-packages flag (Debian 12+)
RUN mkdir -p /home/app/.config/pip && \
    printf '[global]\nbreak-system-packages = true\n' > /home/app/.config/pip/pip.conf

# Configure npm global prefix for non-root installs
RUN npm config set prefix /home/app/.npm-global

# ---- Playwright for app user ----
# System deps + browser already installed by root above.
# Install the Python package for the app user so both system python and
# Homebrew python can import playwright.
RUN pip install playwright

# ---- Smoke-test key tools ----
RUN brew --version && \
    node --version && \
    npm --version && \
    python3 --version && \
    git --version && \
    curl --version > /dev/null && \
    python3 -c "from playwright.sync_api import sync_playwright; print('Playwright OK')" && \
    echo "All tools verified OK"
