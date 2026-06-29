"use strict";

// ── State ──────────────────────────────────────────────────────
const wizardState = {
    currentPanel: 1,
    providerTemplates: [],   // ProviderTemplate[] from GET /api/setup/providers

    // All providers that will be saved (existing + newly added, in priority order)
    stagedProviders: [],     // { templateId, displayName, apiKey, refreshToken, tokenExpiresAt, clientId, models, defaultModel, isExisting }

    // Sub-flow state — current provider being added (cleared by resetSubFlow)
    selectedTemplate: null,
    apiKey: "",
    models: [],              // AvailableModel[] fetched from API
    selectedModels: [],
    defaultModel: null,

    // Anthropic PKCE OAuth sub-flow
    anthropicCodeVerifier: null,
    anthropicRefreshToken: null,
    anthropicTokenExpiresAt: 0,
    anthropicAuthMode: "apikey",    // "apikey" | "oauth"

    // GitHub Copilot device flow
    deviceCode: null,
    pollTimer: null,
    pollIntervalMs: 5000,

    // GitHub Copilot advanced options (optional)
    copilotClientId: null,       // overrides the built-in default OAuth App client ID
    copilotGithubBaseUrl: null,  // overrides public github.com (for GitHub Enterprise)
};

// ── Utilities ──────────────────────────────────────────────────
function $(sel)  { return document.querySelector(sel); }
function $$(sel) { return document.querySelectorAll(sel); }

function esc(str) {
    const d = document.createElement("div");
    d.textContent = str;
    return d.innerHTML;
}

function showStatus(elId, msg, type) {
    const el = $(elId);
    el.textContent = msg;
    el.className = `status-msg ${type}`;
}

function hideStatus(elId) {
    $(elId).className = "status-msg";
}

// ── Navigation ─────────────────────────────────────────────────
// Panels 1-4 = main step 1 (Providers)
// Panel  5   = main step 2 (Review)
// Panel  6   = main step 3 (Done)
function goToPanel(n) {
    wizardState.currentPanel = n;
    $$(".step-panel").forEach(p => p.classList.remove("active"));
    $(`#step-${n}`).classList.add("active");

    const mainStep = n <= 4 ? 1 : n === 5 ? 2 : 3;
    $$(".step-dot").forEach(d => {
        const s = parseInt(d.dataset.step);
        d.classList.remove("active", "completed");
        if (s === mainStep) d.classList.add("active");
        else if (s < mainStep) d.classList.add("completed");
    });
}

// ── Theme ──────────────────────────────────────────────────────
(function applyTheme() {
    const theme = localStorage.getItem("cortex-theme") || "dark";
    document.documentElement.setAttribute("data-bs-theme", theme);
})();

// ── Startup ────────────────────────────────────────────────────
async function init() {
    initEvents();
    await loadProviders();
    await loadExistingProviders();
    renderHub();
}

async function loadProviders() {
    try {
        const resp = await fetch("/api/setup/providers");
        wizardState.providerTemplates = await resp.json();
    } catch (err) {
        console.error("Failed to load providers:", err);
    }
}

// Pre-populate the hub with providers already in the current config.
async function loadExistingProviders() {
    try {
        const resp = await fetch("/api/settings");
        if (!resp.ok) return;
        const data = await resp.json();
        for (const p of (data.providers || [])) {
            if (!p.apiKeyConfigured) continue;
            const tmpl = wizardState.providerTemplates.find(t => t.id === p.name);
            wizardState.stagedProviders.push({
                templateId: p.name,
                displayName: tmpl ? tmpl.name : p.name,
                apiKey: "",
                refreshToken: null,
                tokenExpiresAt: 0,
                clientId: null,
                githubBaseUrl: null,
                models: p.models || [],
                defaultModel: p.defaultModel || (p.models && p.models[0]) || null,
                isExisting: true,
            });
        }
    } catch {
        // No existing providers — that's fine
    }
}

// ── Panel 1: Hub ───────────────────────────────────────────────
function renderHub() {
    const list  = $("#hub-provider-list");
    const empty = $("#hub-empty");
    list.innerHTML = "";

    if (wizardState.stagedProviders.length === 0) {
        empty.style.display = "block";
    } else {
        empty.style.display = "none";
        wizardState.stagedProviders.forEach((p, i) => {
            const initial = (p.displayName || "?")[0].toUpperCase();
            const modelSummary = p.models.length === 0
                ? "No models selected"
                : p.models.length === 1
                ? p.models[0]
                : `${p.models[0]} +${p.models.length - 1} more`;

            const card = document.createElement("div");
            card.className = "hub-provider-card";
            card.innerHTML = `
                <div class="hub-card-icon">${esc(initial)}</div>
                <div class="hub-card-info">
                    <div class="hub-card-name">${esc(p.displayName)}</div>
                    <div class="hub-card-models">${esc(modelSummary)}</div>
                </div>
                ${p.isExisting ? '<span class="hub-card-badge">Saved</span>' : ""}
                <button class="btn-remove" data-idx="${i}"
                        title="Remove ${esc(p.displayName)}"
                         aria-label="Remove ${esc(p.displayName)}"><i class="bi bi-x-lg"></i></button>
            `;
            card.querySelector(".btn-remove").addEventListener("click", e => {
                e.stopPropagation();
                removeProvider(parseInt(e.currentTarget.dataset.idx));
            });
            list.appendChild(card);
        });
    }

    hideStatus("#hub-status");
    $("#btn-hub-finish").disabled = wizardState.stagedProviders.length === 0;
}

function removeProvider(idx) {
    wizardState.stagedProviders.splice(idx, 1);
    renderHub();
}

// ── Panel 2: Choose Provider ───────────────────────────────────
function startAddProvider() {
    renderProviderCards();
    goToPanel(2);
}

function renderProviderCards() {
    const container = $("#provider-cards");
    container.innerHTML = "";
    wizardState.selectedTemplate = null;
    $("#btn-choose-next").disabled = true;

    wizardState.providerTemplates.forEach(t => {
        const alreadyAdded = wizardState.stagedProviders.some(s => s.templateId === t.id);
        const card = document.createElement("div");
        card.className = "provider-card" + (alreadyAdded ? " disabled" : "");
        card.dataset.id = t.id;
        card.innerHTML = `
            <div class="radio-dot"></div>
            <div class="card-info">
                <h3>${esc(t.name)}</h3>
                <p>${esc(t.description)}</p>
                ${alreadyAdded ? '<span class="already-badge">Already added</span>' : ""}
            </div>
        `;
        if (!alreadyAdded) {
            card.addEventListener("click", () => selectTemplate(t.id));
        }
        container.appendChild(card);
    });
}

function selectTemplate(id) {
    wizardState.selectedTemplate = wizardState.providerTemplates.find(t => t.id === id) || null;
    $$(".provider-card").forEach(c => c.classList.toggle("selected", c.dataset.id === id));
    $("#btn-choose-next").disabled = false;
}

// ── Panel 3: Authenticate ──────────────────────────────────────
function setupAuthPanel() {
    const tmpl = wizardState.selectedTemplate;
    if (!tmpl) return;

    hideStatus("#auth-status");

    // Hide all auth sub-sections
    $("#auth-apikey-section").style.display           = "none";
    $("#auth-oauth-section").style.display            = "none";
    $("#auth-anthropic-oauth-section").style.display  = "none";
    $("#auth-toggle").style.display                   = "none";
    $("#btn-auth-next").style.display                 = "";

    if (tmpl.authMethod === "oauth") {
        // GitHub Copilot device flow
        $("#auth-title").textContent = "Connect GitHub Account";
        $("#auth-desc").textContent  = "Cortex needs access to GitHub Copilot. Authorize below — the code will appear in seconds.";
        $("#auth-oauth-section").style.display = "block";
        $("#btn-auth-next").style.display = "none";

        // Reset advanced Copilot options so a second run starts fresh
        wizardState.copilotClientId = null;
        wizardState.copilotGithubBaseUrl = null;
        const clientIdInput     = $("#copilot-client-id-input");
        const githubBaseUrlInput = $("#copilot-github-base-url-input");
        if (clientIdInput)     clientIdInput.value     = "";
        if (githubBaseUrlInput) githubBaseUrlInput.value = "";

        startDeviceFlow();

    } else if (tmpl.supportsOAuth) {
        // Anthropic — toggle between API key and OAuth
        $("#auth-toggle").style.display = "flex";
        applyAnthropicAuthMode(wizardState.anthropicAuthMode);

    } else {
        // Plain API key
        showApiKeySection(tmpl);
    }
}

function showApiKeySection(tmpl) {
    $("#auth-title").textContent     = "Enter your API key";
    $("#auth-desc").textContent      = `Paste your ${tmpl.apiKeyLabel || "API Key"} below. It will be encrypted and stored securely using Windows DPAPI.`;
    $("#api-key-label").textContent  = tmpl.apiKeyLabel || "API Key";
    $("#api-key-input").placeholder  = tmpl.apiKeyPlaceholder || "";
    $("#api-key-input").value        = wizardState.apiKey;
    $("#auth-apikey-section").style.display           = "block";
    $("#auth-anthropic-oauth-section").style.display  = "none";
    $("#btn-auth-next").style.display = "";
    updateAuthNextButton();
}

function applyAnthropicAuthMode(mode) {
    wizardState.anthropicAuthMode = mode;
    const tmpl = wizardState.selectedTemplate;
    $("#btn-auth-apikey").classList.toggle("active", mode === "apikey");
    $("#btn-auth-oauth").classList.toggle("active", mode === "oauth");

    if (mode === "apikey") {
        showApiKeySection(tmpl);
    } else {
        $("#auth-title").textContent = "Sign in with Claude Pro / Max";
        $("#auth-desc").textContent  = "Click the button below to open Anthropic login, then paste the authorization code shown on screen.";
        $("#auth-apikey-section").style.display          = "none";
        $("#auth-anthropic-oauth-section").style.display = "block";
        $("#btn-auth-next").style.display = "none";
        $("#anthropic-code-input").value  = "";
        $("#anthropic-oauth-status-inline").textContent = "";
        updateAnthropicConnectButton();
    }
}

function updateAuthNextButton() {
    const key = $("#api-key-input").value.trim();
    $("#btn-auth-next").disabled = key.length < 4;
}

// ── Panel 3: Anthropic Auth Tabs ──────────────────────────────
function setupAnthropicAuthTabs() {
    document.querySelectorAll("[data-auth-tab]").forEach(btn => {
        btn.addEventListener("click", () => {
            const tab = btn.dataset.authTab;
            // Toggle active tab button
            document.querySelectorAll("[data-auth-tab]").forEach(b => {
                b.className = b.dataset.authTab === tab
                    ? "btn btn-sm btn-outline-primary active"
                    : "btn btn-sm btn-outline-secondary";
            });
            // Toggle panels
            document.querySelectorAll(".anthropic-auth-panel").forEach(p => p.style.display = "none");
            const panel = document.getElementById("anthropic-tab-" + tab);
            if (panel) panel.style.display = "";
        });
    });
}

// ── Panel 3: Anthropic Device Code Flow ───────────────────────
let anthropicDevicePollTimer = null;

async function startAnthropicDeviceFlow() {
    const btn = $("#btn-anthropic-device-start");
    btn.disabled = true;
    btn.textContent = "Starting...";
    hideStatus("#auth-status");

    try {
        const resp = await fetch("/api/setup/anthropic-device-code", { method: "POST" });
        if (!resp.ok) {
            const err = await resp.json().catch(() => ({}));
            throw new Error(err.error || `HTTP ${resp.status}`);
        }
        const data = await resp.json();
        // data: { deviceCode, userCode, verificationUrl, expiresIn, interval }

        // Open the verification URL
        window.open(data.verificationUrl, "_blank", "noopener,noreferrer");

        // Show polling status
        const statusEl = $("#anthropic-device-status");
        statusEl.style.display = "";
        $("#anthropic-device-msg").textContent = `Waiting for approval... (code: ${data.userCode})`;
        btn.textContent = "Waiting for approval...";

        // Start polling
        const pollInterval = Math.max(data.interval || 5, 3) * 1000;
        anthropicDevicePollTimer = setInterval(async () => {
            try {
                const pollResp = await fetch("/api/setup/anthropic-device-poll", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ deviceCode: data.deviceCode }),
                });
                const pollData = await pollResp.json();

                if (pollData.status === "approved") {
                    clearInterval(anthropicDevicePollTimer);
                    anthropicDevicePollTimer = null;
                    statusEl.style.display = "none";
                    btn.textContent = "Connected!";

                    wizardState.apiKey = pollData.accessToken;
                    wizardState.anthropicRefreshToken = pollData.refreshToken || null;
                    wizardState.anthropicTokenExpiresAt = pollData.expiresAt || 0;
                    await validateAndFetchModels();
                } else if (pollData.status === "error") {
                    clearInterval(anthropicDevicePollTimer);
                    anthropicDevicePollTimer = null;
                    throw new Error(pollData.error || "Authorization failed");
                }
                // status === "pending" — keep polling
            } catch (pollErr) {
                clearInterval(anthropicDevicePollTimer);
                anthropicDevicePollTimer = null;
                statusEl.style.display = "none";
                btn.disabled = false;
                btn.textContent = "Sign In with Anthropic";
                showStatus("#auth-status", `Authorization failed: ${pollErr.message}`, "error");
            }
        }, pollInterval);

    } catch (err) {
        showStatus("#auth-status", `Failed to start login: ${err.message}`, "error");
        btn.disabled = false;
        btn.textContent = "Sign In with Anthropic";
    }
}

// ── Panel 3: Anthropic Setup Token ────────────────────────────
function updateSetupTokenButton() {
    const token = ($("#anthropic-setup-token-input")?.value || "").trim();
    const btn = $("#btn-anthropic-setup-token-save");
    if (btn) btn.disabled = !token.startsWith("sk-ant-oat01-") || token.length < 80;
}

async function saveAnthropicSetupToken() {
    const token = ($("#anthropic-setup-token-input")?.value || "").trim();
    if (!token) return;

    const btn = $("#btn-anthropic-setup-token-save");
    btn.disabled = true;
    btn.textContent = "Saving...";
    hideStatus("#auth-status");

    try {
        const resp = await fetch("/api/setup/anthropic-setup-token", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ token }),
        });
        if (!resp.ok) {
            const err = await resp.json().catch(() => ({}));
            throw new Error(err.error || `HTTP ${resp.status}`);
        }

        wizardState.apiKey = token;
        wizardState.anthropicRefreshToken = null;
        wizardState.anthropicTokenExpiresAt = 0;
        await validateAndFetchModels();
    } catch (err) {
        showStatus("#auth-status", `Failed to save token: ${err.message}`, "error");
        btn.disabled = false;
        btn.textContent = "Save Token";
    }
}

// ── Panel 3: Anthropic PKCE OAuth (legacy) ────────────────────
function updateAnthropicConnectButton() {
    const code = $("#anthropic-code-input").value.trim();
    $("#btn-anthropic-connect").disabled = code.length < 4 || !wizardState.anthropicCodeVerifier;
}

async function startAnthropicAuth() {
    const btn = $("#btn-anthropic-open");
    btn.disabled = true;
    btn.textContent = "Opening…";
    $("#anthropic-oauth-status-inline").textContent = "";
    hideStatus("#auth-status");

    try {
        const resp = await fetch("/api/setup/anthropic-auth", { method: "POST" });
        if (!resp.ok) {
            const err = await resp.json().catch(() => ({}));
            throw new Error(err.error || `HTTP ${resp.status}`);
        }
        const data = await resp.json();  // { authUrl, codeVerifier }
        wizardState.anthropicCodeVerifier = data.codeVerifier;
        window.open(data.authUrl, "_blank", "noopener,noreferrer");
        $("#anthropic-oauth-status-inline").textContent = "Browser opened — paste the code below.";
        updateAnthropicConnectButton();
    } catch (err) {
        showStatus("#auth-status", `Failed to start login: ${err.message}`, "error");
    } finally {
        btn.disabled = false;
        btn.textContent = "Open Anthropic Login";
    }
}

async function exchangeAnthropicCode() {
    const code = $("#anthropic-code-input").value.trim();
    if (!code || !wizardState.anthropicCodeVerifier) return;

    const btn = $("#btn-anthropic-connect");
    btn.disabled = true;
    btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1" role="status"></span>Connecting…';
    hideStatus("#auth-status");

    try {
        const resp = await fetch("/api/setup/anthropic-exchange", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ code, codeVerifier: wizardState.anthropicCodeVerifier }),
        });
        if (!resp.ok) {
            const err = await resp.json().catch(() => ({}));
            throw new Error(err.error || `HTTP ${resp.status}`);
        }
        const data = await resp.json();
        wizardState.apiKey                  = data.accessToken;
        wizardState.anthropicRefreshToken   = data.refreshToken || null;
        wizardState.anthropicTokenExpiresAt = data.expiresAt    || 0;
        await validateAndFetchModels();
    } catch (err) {
        showStatus("#auth-status", `Authorization failed: ${err.message}`, "error");
        btn.disabled  = false;
        btn.textContent = "Connect";
    }
}

// ── Panel 3: GitHub Copilot device flow ───────────────────────
function resetOAuthUI() {
    stopDeviceFlow();
    $("#oauth-loading").style.display = "none";
    $("#oauth-active").style.display  = "none";
    $("#oauth-idle").style.display    = "block";
    $("#oauth-waiting-msg").textContent = "Waiting for authorization…";
}

function stopDeviceFlow() {
    if (wizardState.pollTimer !== null) {
        clearTimeout(wizardState.pollTimer);
        wizardState.pollTimer = null;
    }
    wizardState.deviceCode = null;
}

async function startDeviceFlow() {
    stopDeviceFlow();
    hideStatus("#auth-status");
    $("#oauth-loading").style.display = "block";
    $("#oauth-active").style.display  = "none";
    $("#oauth-idle").style.display    = "none";

    try {
        const resp = await fetch("/api/setup/copilot-auth", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                clientId:       wizardState.copilotClientId       || null,
                githubBaseUrl:  wizardState.copilotGithubBaseUrl  || null,
            }),
        });
        if (!resp.ok) {
            const err = await resp.json().catch(() => ({}));
            throw new Error(err.error || `HTTP ${resp.status}`);
        }
        const flow = await resp.json();
        wizardState.deviceCode     = flow.deviceCode;
        wizardState.pollIntervalMs = (flow.pollingIntervalSeconds + 3) * 1000;

        $("#oauth-user-code").textContent = flow.userCode;
        $("#oauth-verify-link").href      = flow.verificationUri;
        $("#oauth-loading").style.display = "none";
        $("#oauth-active").style.display  = "block";
        wizardState.pollTimer = setTimeout(pollForToken, wizardState.pollIntervalMs);
    } catch (err) {
        showStatus("#auth-status", err.message, "error");
        resetOAuthUI();
    }
}

async function pollForToken() {
    wizardState.pollTimer = null;
    try {
        const resp = await fetch("/api/setup/copilot-poll", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                deviceCode:    wizardState.deviceCode,
                clientId:      wizardState.copilotClientId       || null,
                githubBaseUrl: wizardState.copilotGithubBaseUrl  || null,
            }),
        });
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        const result = await resp.json();

        if (result.status === "success") {
            wizardState.apiKey = result.accessToken;
            $("#oauth-waiting-msg").textContent = "Authorized! Fetching available models…";
            await validateAndFetchModels();

        } else if (result.status === "pending") {
            if (result.retryAfterSeconds) {
                wizardState.pollIntervalMs = result.retryAfterSeconds * 1000;
            }
            wizardState.pollTimer = setTimeout(pollForToken, wizardState.pollIntervalMs);

        } else {
            const msgs = { expired: "The code expired. Please try again.", denied: "Authorization was denied.", failed: "Authorization failed. Please try again." };
            // Prefer GitHub's real error (e.g. device_flow_disabled) when the backend surfaces it.
            showStatus("#auth-status", result.error || msgs[result.status] || "Authorization failed.", "error");
            resetOAuthUI();
        }
    } catch (err) {
        showStatus("#auth-status", `Polling error: ${err.message}`, "error");
        resetOAuthUI();
    }
}

// ── Panel 3 → 4: fetch models ─────────────────────────────────
async function validateAndFetchModels() {
    const tmpl = wizardState.selectedTemplate;
    const isGithubOAuth    = tmpl.authMethod === "oauth";
    const isAnthropicOAuth = tmpl.supportsOAuth && wizardState.anthropicAuthMode === "oauth";

    let apiKey, tokenType;
    if (isGithubOAuth || isAnthropicOAuth) {
        apiKey    = wizardState.apiKey;
        tokenType = "oauth";
    } else {
        apiKey    = $("#api-key-input").value.trim();
        tokenType = null;
    }

    wizardState.apiKey = apiKey;

    const isOAuthFlow = isGithubOAuth || isAnthropicOAuth;
    const btn = $("#btn-auth-next");

    if (!isOAuthFlow) {
        btn.disabled  = true;
        btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1" role="status"></span>Fetching models...';
    } else if (isGithubOAuth) {
        $("#oauth-waiting-msg").textContent = "Fetching available models…";
    }

    hideStatus("#auth-status");

    try {
        const body = { provider: tmpl.id, apiKey };
        if (tokenType) body.tokenType = tokenType;

        const resp = await fetch("/api/setup/fetch-models", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
        });
        if (!resp.ok) {
            const err = await resp.json().catch(() => ({}));
            throw new Error(err.error || `HTTP ${resp.status}`);
        }

        const models = await resp.json();
        if (!models || models.length === 0) {
            throw new Error("No chat models found for this provider.");
        }

        wizardState.models        = models;
        wizardState.selectedModels = [];
        wizardState.defaultModel   = null;
        renderModelList();
        goToPanel(4);

    } catch (err) {
        showStatus("#auth-status", err.message, "error");
        if (isGithubOAuth) resetOAuthUI();
    } finally {
        if (!isOAuthFlow) {
            btn.disabled    = false;
            btn.textContent = "Validate & Fetch Models";
            updateAuthNextButton();
        }
    }
}

// ── Panel 4: Model selection ───────────────────────────────────
function renderModelList() {
    const container = $("#model-list");
    container.innerHTML = "";

    const groups = {};
    wizardState.models.forEach(m => {
        const pub = m.publisher || "Other";
        if (!groups[pub]) groups[pub] = [];
        groups[pub].push(m);
    });

    for (const [publisher, models] of Object.entries(groups)) {
        const header = document.createElement("div");
        header.className = "model-group-header";
        header.textContent = publisher;
        container.appendChild(header);

        models.forEach(m => {
            const item = document.createElement("div");
            item.className = "model-item";
            item.dataset.id = m.id;

            const cb = document.createElement("input");
            cb.type    = "checkbox";
            cb.checked = wizardState.selectedModels.includes(m.id);
            cb.addEventListener("change", () => toggleModel(m.id, cb.checked));

            const info = document.createElement("div");
            info.className = "model-info";

            const name = document.createElement("div");
            name.className   = "model-name";
            name.textContent = m.id;
            info.appendChild(name);

            if (m.description) {
                const desc = document.createElement("div");
                desc.className   = "model-desc";
                desc.textContent = m.description;
                info.appendChild(desc);
            }

            const defaultBadge = document.createElement("span");
            defaultBadge.className   = "default-badge";
            defaultBadge.textContent = "Default";
            defaultBadge.style.display = wizardState.defaultModel === m.id ? "inline" : "none";

            const setDefaultBtn = document.createElement("button");
            setDefaultBtn.className   = "btn btn-set-default";
            setDefaultBtn.textContent = "Set default";
            setDefaultBtn.style.display = wizardState.defaultModel === m.id ? "none" : "";
            setDefaultBtn.addEventListener("click", e => { e.stopPropagation(); setDefaultModel(m.id); });

            item.appendChild(cb);
            item.appendChild(info);
            item.appendChild(defaultBadge);
            item.appendChild(setDefaultBtn);

            item.addEventListener("click", e => {
                if (e.target === cb || e.target === setDefaultBtn) return;
                cb.checked = !cb.checked;
                toggleModel(m.id, cb.checked);
            });

            container.appendChild(item);
        });
    }

    updateModelCount();
}

function toggleModel(id, checked) {
    if (checked) {
        if (!wizardState.selectedModels.includes(id)) wizardState.selectedModels.push(id);
        if (!wizardState.defaultModel) { setDefaultModel(id); return; }
    } else {
        wizardState.selectedModels = wizardState.selectedModels.filter(m => m !== id);
        if (wizardState.defaultModel === id)
            wizardState.defaultModel = wizardState.selectedModels[0] || null;
    }
    refreshModelListUI();
    updateModelCount();
}

function setDefaultModel(id) {
    if (!wizardState.selectedModels.includes(id)) {
        wizardState.selectedModels.push(id);
        const cb = document.querySelector(`.model-item[data-id="${CSS.escape(id)}"] input[type="checkbox"]`);
        if (cb) cb.checked = true;
    }
    wizardState.defaultModel = id;
    refreshModelListUI();
    updateModelCount();
}

function refreshModelListUI() {
    $$(".model-item").forEach(item => {
        const id        = item.dataset.id;
        const badge     = item.querySelector(".default-badge");
        const setBtn    = item.querySelector(".btn-set-default");
        const isDefault  = wizardState.defaultModel === id;
        const isSelected = wizardState.selectedModels.includes(id);
        if (badge)  badge.style.display  = isDefault ? "inline" : "none";
        if (setBtn) setBtn.style.display = (isSelected && !isDefault) ? "" : "none";
    });
}

function updateModelCount() {
    const count = wizardState.selectedModels.length;
    const def   = wizardState.defaultModel || "none";
    $("#model-count").textContent = `${count} model${count !== 1 ? "s" : ""} selected · Default: ${def}`;
    $("#btn-models-add").disabled = count === 0 || !wizardState.defaultModel;
}

function addProviderToList() {
    const tmpl = wizardState.selectedTemplate;
    if (!tmpl) return;

    if (wizardState.stagedProviders.some(p => p.templateId === tmpl.id)) {
        showStatus("#models-status", `${tmpl.name} is already in your list.`, "error");
        return;
    }

    const orderedModels = [
        wizardState.defaultModel,
        ...wizardState.selectedModels.filter(m => m !== wizardState.defaultModel),
    ];

    wizardState.stagedProviders.push({
        templateId:   tmpl.id,
        displayName:  tmpl.name,
        apiKey:       wizardState.apiKey,
        refreshToken: wizardState.anthropicRefreshToken,
        tokenExpiresAt: wizardState.anthropicTokenExpiresAt,
        clientId:     wizardState.copilotClientId      || null,
        githubBaseUrl: wizardState.copilotGithubBaseUrl || null,
        models:       orderedModels,
        defaultModel: wizardState.defaultModel,
        isExisting:   false,
    });

    resetSubFlow();
    renderHub();
    goToPanel(1);
}

function resetSubFlow() {
    wizardState.selectedTemplate        = null;
    wizardState.apiKey                  = "";
    wizardState.models                  = [];
    wizardState.selectedModels          = [];
    wizardState.defaultModel            = null;
    wizardState.anthropicCodeVerifier   = null;
    wizardState.anthropicRefreshToken   = null;
    wizardState.anthropicTokenExpiresAt = 0;
    wizardState.anthropicAuthMode       = "apikey";
    wizardState.copilotClientId         = null;
    wizardState.copilotGithubBaseUrl    = null;
    stopDeviceFlow();
}

// ── Panel 5: Review & Reorder ─────────────────────────────────
function renderReviewList() {
    const container = $("#review-list");
    container.innerHTML = "";

    wizardState.stagedProviders.forEach((p, i) => {
        const item = document.createElement("div");
        item.className = "review-item";
        item.draggable = true;
        item.dataset.idx = i;

        const modelText = p.models.length === 0
            ? "No models"
            : `${p.models.length} model${p.models.length !== 1 ? "s" : ""}`;

        item.innerHTML = `
            <div class="drag-handle" aria-hidden="true"><i class="bi bi-grip-vertical"></i></div>
            <div class="review-info">
                <div class="review-name">${esc(p.displayName)}</div>
                <div class="review-models">${esc(modelText)}</div>
            </div>
            <span class="priority-badge ${i === 0 ? "primary" : "fallback"}">
                ${i === 0 ? "Primary" : `Fallback&nbsp;${i}`}
            </span>
        `;
        container.appendChild(item);
    });

    initDragToReorder(container);
}

function initDragToReorder(container) {
    let dragged = null;

    container.addEventListener("dragstart", e => {
        dragged = e.target.closest(".review-item");
        if (dragged) { dragged.classList.add("dragging"); e.dataTransfer.effectAllowed = "move"; }
    });

    container.addEventListener("dragend", () => {
        if (dragged) dragged.classList.remove("dragging");
        container.querySelectorAll(".drag-over").forEach(el => el.classList.remove("drag-over"));
        dragged = null;
    });

    container.addEventListener("dragover", e => {
        e.preventDefault();
        const target = e.target.closest(".review-item");
        if (!target || target === dragged) return;
        container.querySelectorAll(".drag-over").forEach(el => el.classList.remove("drag-over"));
        target.classList.add("drag-over");
    });

    container.addEventListener("drop", e => {
        e.preventDefault();
        const target = e.target.closest(".review-item");
        if (!target || target === dragged) return;
        const fromIdx = parseInt(dragged.dataset.idx);
        const toIdx   = parseInt(target.dataset.idx);
        const [removed] = wizardState.stagedProviders.splice(fromIdx, 1);
        wizardState.stagedProviders.splice(toIdx, 0, removed);
        renderReviewList();   // re-render with updated indices + priority badges
    });
}

// ── Panel 5 → 6: Save ─────────────────────────────────────────
async function saveSetup() {
    const btn = $("#btn-review-save");
    btn.disabled  = true;
    btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1" role="status"></span>Saving...';
    hideStatus("#review-status");

    const providers = wizardState.stagedProviders.map(p => ({
        provider:       p.templateId,
        apiKey:         p.apiKey         || "",
        refreshToken:   p.refreshToken   || null,
        tokenExpiresAt: p.tokenExpiresAt || 0,
        clientId:       p.clientId       || null,
        githubBaseUrl:  p.githubBaseUrl  || null,
        models:         p.models,
        isExisting:     p.isExisting,
    }));

    const fallbackOrder = wizardState.stagedProviders.map(p => p.templateId);

    try {
        const resp = await fetch("/api/setup/save", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ providers, fallbackOrder }),
        });
        if (!resp.ok) {
            const err = await resp.json().catch(() => ({}));
            throw new Error(err.error || `HTTP ${resp.status}`);
        }
        goToPanel(6);
    } catch (err) {
        showStatus("#review-status", err.message, "error");
    } finally {
        btn.disabled    = false;
        btn.textContent = "Save & Finish";
    }
}

// ── Event wiring ───────────────────────────────────────────────
function initEvents() {
    // Hub (panel 1)
    $("#btn-add-provider").addEventListener("click", startAddProvider);
    $("#btn-hub-finish").addEventListener("click", () => { renderReviewList(); goToPanel(5); });

    // Choose provider (panel 2)
    $("#btn-choose-back").addEventListener("click", () => { resetSubFlow(); goToPanel(1); });
    $("#btn-choose-next").addEventListener("click", () => { setupAuthPanel(); goToPanel(3); });

    // Authenticate (panel 3)
    $("#btn-auth-back").addEventListener("click", () => {
        stopDeviceFlow();
        wizardState.apiKey = "";
        wizardState.anthropicCodeVerifier = null;
        goToPanel(2);
    });
    $("#btn-auth-next").addEventListener("click", validateAndFetchModels);

    $("#api-key-input").addEventListener("input", updateAuthNextButton);
    $("#api-key-input").addEventListener("keydown", e => {
        if (e.key === "Enter") { e.preventDefault(); if (!$("#btn-auth-next").disabled) validateAndFetchModels(); }
    });

    // Auth-method toggle (Anthropic)
    $("#btn-auth-apikey").addEventListener("click", () => applyAnthropicAuthMode("apikey"));
    $("#btn-auth-oauth").addEventListener("click",  () => applyAnthropicAuthMode("oauth"));

    // Anthropic auth tabs
    setupAnthropicAuthTabs();

    // Anthropic Device Code Flow
    $("#btn-anthropic-device-start").addEventListener("click", startAnthropicDeviceFlow);

    // Anthropic Setup Token
    $("#anthropic-setup-token-input").addEventListener("input", updateSetupTokenButton);
    $("#btn-anthropic-setup-token-save").addEventListener("click", saveAnthropicSetupToken);

    // Anthropic PKCE (legacy)
    $("#btn-anthropic-open").addEventListener("click", startAnthropicAuth);
    $("#anthropic-code-input").addEventListener("input", updateAnthropicConnectButton);
    $("#anthropic-code-input").addEventListener("keydown", e => {
        if (e.key === "Enter") { e.preventDefault(); if (!$("#btn-anthropic-connect").disabled) exchangeAnthropicCode(); }
    });
    $("#btn-anthropic-connect").addEventListener("click", exchangeAnthropicCode);

    // Copilot advanced options — keep wizardState in sync as the user types
    $("#copilot-client-id-input").addEventListener("input", e => {
        wizardState.copilotClientId = e.target.value.trim() || null;
    });
    $("#copilot-github-base-url-input").addEventListener("input", e => {
        wizardState.copilotGithubBaseUrl = e.target.value.trim() || null;
    });

    // Copilot device flow
    $("#btn-start-flow").addEventListener("click", startDeviceFlow);
    $("#btn-copy-code").addEventListener("click", () => {
        const code = $("#oauth-user-code").textContent;
        navigator.clipboard.writeText(code).catch(() => {});
        const btn = $("#btn-copy-code");
        btn.classList.add("copied");
        $("#icon-copy").style.display   = "none";
        $("#icon-copied").style.display = "";
        setTimeout(() => {
            btn.classList.remove("copied");
            $("#icon-copy").style.display   = "";
            $("#icon-copied").style.display = "none";
        }, 1500);
    });

    // Models (panel 4)
    $("#btn-models-back").addEventListener("click", () => { resetSubFlow(); goToPanel(1); });
    $("#btn-models-add").addEventListener("click", addProviderToList);

    // Review (panel 5)
    $("#btn-review-back").addEventListener("click", () => goToPanel(1));
    $("#btn-review-save").addEventListener("click", saveSetup);
}

// ── Boot ──────────────────────────────────────────────────────
if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
} else {
    init();
}
