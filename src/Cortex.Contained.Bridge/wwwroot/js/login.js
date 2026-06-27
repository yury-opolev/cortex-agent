// Cortex — Login / Set-Password page logic
"use strict";

const panelLoading = document.getElementById("panel-loading");
const panelSetup = document.getElementById("panel-setup");
const panelLogin = document.getElementById("panel-login");
const subtitle = document.getElementById("login-subtitle");

// ── Helpers ──────────────────────────────────────────────────
function showPanel(panel) {
    panelLoading.classList.remove("active");
    panelSetup.classList.remove("active");
    panelLogin.classList.remove("active");
    panel.classList.add("active");
}
function showStatus(id, message, type) {
    const el = document.getElementById(id);
    el.textContent = message;
    el.className = "status-msg " + type;
}

function clearStatus(id) {
    const el = document.getElementById(id);
    el.textContent = "";
    el.className = "status-msg";
}

// ── Check auth status and show correct panel ────────────────
async function init() {
    try {
        const resp = await fetch("/api/auth/status");
        if (!resp.ok) throw new Error("Failed to check auth status");
        const data = await resp.json();

        panelLoading.classList.remove("active");

        if (!data.isPasswordSet) {
            subtitle.textContent = "Create a password to get started";
            showPanel(panelSetup);
            document.getElementById("new-password").focus();
        } else {
            subtitle.textContent = "Sign in to continue";
            showPanel(panelLogin);
            document.getElementById("login-password").focus();
        }
    } catch {
        showPanel(panelLogin);
        subtitle.textContent = "Sign in to continue";
    }
}

// ── Set Password (first-time setup) ─────────────────────────
document.getElementById("setup-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    clearStatus("setup-status");

    const password = document.getElementById("new-password").value;
    const confirm = document.getElementById("confirm-password").value;

    if (password !== confirm) {
        showStatus("setup-status", "Passwords do not match.", "error");
        return;
    }

    if (password.length < 8) {
        showStatus("setup-status", "Password must be at least 8 characters.", "error");
        return;
    }

    const btn = e.target.querySelector("button[type=submit]");
    btn.disabled = true;

    try {
        const resp = await fetch("/api/auth/setup", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ password }),
        });

        if (resp.status === 409) {
            showStatus("setup-status", "Password has already been set. Redirecting to login...", "info");
            setTimeout(() => location.reload(), 1500);
            return;
        }

        if (!resp.ok) {
            const data = await resp.json().catch(() => ({}));
            throw new Error(data.error || "Failed to set password");
        }

        // Password set and session cookie received — redirect to app
        window.location.href = "/";
    } catch (err) {
        showStatus("setup-status", err.message, "error");
    } finally {
        btn.disabled = false;
    }
});

// ── Login ────────────────────────────────────────────────────
document.getElementById("login-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    clearStatus("login-status");

    const password = document.getElementById("login-password").value;
    const btn = e.target.querySelector("button[type=submit]");
    btn.disabled = true;

    try {
        const resp = await fetch("/api/auth/login", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ password }),
        });

        if (resp.status === 429) {
            showStatus("login-status", "Too many login attempts. Please wait a minute and try again.", "error");
            return;
        }

        if (resp.status === 401) {
            showStatus("login-status", "Incorrect password.", "error");
            document.getElementById("login-password").value = "";
            document.getElementById("login-password").focus();
            return;
        }

        if (!resp.ok) {
            const data = await resp.json().catch(() => ({}));
            throw new Error(data.error || "Login failed");
        }

        // Session cookie received — redirect to app (or back to where they came from)
        const returnTo = new URLSearchParams(window.location.search).get("returnTo") || "/";
        window.location.href = returnTo;
    } catch (err) {
        showStatus("login-status", err.message, "error");
    } finally {
        btn.disabled = false;
    }
});

// ── Theme persistence (match other pages) ────────────────────
const theme = localStorage.getItem("cortex-theme") || "dark";
document.documentElement.setAttribute("data-bs-theme", theme);

// ── Start ────────────────────────────────────────────────────
init();
