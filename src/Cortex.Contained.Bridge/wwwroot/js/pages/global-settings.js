"use strict";

/**
 * Global settings page — Alpine.js component.
 * Tabs: General (LLM providers), Channels, Speech, Memory.
 */

// ── Telemetry ───────────────────────────────────────────────
// Fire-and-forget POST to /api/telemetry/ui. Never throws, never awaited —
// we don't want telemetry to change page behavior even when the endpoint
// is down or returns an error.
function uiTelemetry(source, event, properties) {
    try {
        fetch("/api/telemetry/ui", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ source, event, properties: properties || {} }),
            credentials: "same-origin",
        }).catch(() => {});
    } catch {
        // ignore
    }
}

// ── Shared constants ────────────────────────────────────────

const channelIcons = {
    webchat: "bi-chat-dots",
    voice:   "bi-mic",
    discord: "bi-discord",
};

const channelSettingsSchema = {
    voice: [
        { key: "_ActivationMode", label: "Activation Mode", hint: "How voice input is triggered", type: "select", options: ["push-to-talk", "open-mic"], default: "push-to-talk", virtual: true },
        { key: "PushToTalkHotkey", label: "Push-to-Talk Hotkey", hint: "Press once to start listening, press again to stop. e.g. Ctrl+Space, Alt+Shift+V, F5", default: "Ctrl+Space", showWhen: { key: "_ActivationMode", value: "push-to-talk" } },
    ],
    discord: [
        { key: "BotToken", label: "Bot Token", hint: "Discord bot token from the Developer Portal. See the setup guide below.", type: "password" },
        { key: "DmVoiceTranscription", label: "DM Voice Transcription", hint: "Transcribe voice message attachments in DMs via STT. Requires a Whisper model and Bridge restart.", type: "select", options: ["false", "true"], default: "false" },
        { key: "DmVoiceReplyMode", label: "DM Voice Reply Mode", hint: "How to reply to DM voice messages: text (default) or voice (audio attachment).", type: "select", options: ["text", "voice"], default: "text" },
        { key: "SilenceTimeoutMs", label: "Silence Timeout (ms)", hint: "Soft silence commit point. With Turn Detector enabled, completed sentences commit much faster (~400ms minimum); this is the soft point where the bot commits unless the detector says the user isn't done. A hard 4000ms ceiling above this prevents stranded waits.", default: "1500" },
        { key: "UseTurnDetector", label: "Smart Turn Detection", hint: "When enabled, a small ONNX model predicts when the user has finished a thought and commits early — typically cuts voice latency by ~1 second on completed sentences. Silence Timeout above remains the safety ceiling.", type: "select", options: ["true", "false"], default: "true" },
        { key: "EnableBargeIn", label: "Barge-In Detection", hint: "When enabled, user speech during bot playback pauses audio and classifies the interruption.", type: "select", options: ["true", "false"], default: "true" },
        { key: "BargeInOnsetGuardMs", label: "Barge-In Onset Guard (ms)", hint: "Sustained user speech required before a barge-in stops the agent. Filters single-frame coughs/claps. Lower = snappier interrupts, higher = fewer false triggers.", default: "150", showWhen: { key: "EnableBargeIn", value: "true" } },
        { key: "BargeInClassifierMode", label: "Barge-In Classifier", hint: "How an interruption is classified as a real take-the-floor vs. a backchannel (\"mhm\"). Heuristic Only uses lexicon + word-count rules. Heuristic + LLM additionally consults the model on ambiguous cases (only effective on voice paths co-located with the LLM; the Discord path is heuristic-only by architecture).", type: "select", options: ["HeuristicPlusLlm", "HeuristicOnly"], default: "HeuristicPlusLlm", showWhen: { key: "EnableBargeIn", value: "true" } },
    ],
};

const ttsEngineLabels = {
    kokoro: "Kokoro",
    "windows-sapi": "Windows SAPI",
    silero: "Silero",
    "silero-v5-russian": "Silero v5 Russian (CC BY-NC)",
    "silero-v5-cis-base": "Silero v5 CIS Base (MIT)",
    "roest-da": "Røst (Danish)",
    auto: "Auto (Multi-language)",
};

const languageNames = {
    en: "English", ru: "Russian", de: "German", fr: "French",
    es: "Spanish", it: "Italian", pt: "Portuguese", uk: "Ukrainian",
    pl: "Polish", zh: "Chinese", ja: "Japanese", ko: "Korean",
    ar: "Arabic", hi: "Hindi", tr: "Turkish", nl: "Dutch",
    sv: "Swedish", da: "Danish", no: "Norwegian", fi: "Finnish",
    cs: "Czech", ro: "Romanian", hu: "Hungarian", el: "Greek",
    kk: "Kazakh",
};


function globalSettingsPage() {
    return {
        // ── State ──────────────────────────────────────────
        loading: true,
        activeTab: "general",

        // General tab
        providers: [],
        fallbackOrder: [],
        defaultModels: {},
        memoryModels: {},
        maxSubagentRounds: 0,
        maxConcurrentSubagents: 5,
        webUiInfo: "",
        agentConnected: false,
        dirty: false,
        saving: false,
        // Set of provider names currently being refreshed — used to show a
        // spinner on the specific provider's Refresh button.
        refreshingProviders: {},

        // Original snapshot for dirty check
        _original: null,

        // Channels tab
        channels: [],
        showRestartBanner: false,
        savingChannel: null,      // channelType being saved
        discordGuideOpen: false,

        // Speech tab
        speechLoading: true,
        speechProviders: [],     // from /api/speech/status -> providers
        sttReady: false,
        speechError: null,

        // Speech toggles (master + STT + TTS + voice-id)
        speechEnabled: true,
        sttEnabled: true,
        ttsEnabled: true,
        voiceIdEnabled: true,
        voiceRestartRequired: false,
        savingSpeechToggles: false,

        // Built-in memory master toggle
        memoryEnabled: true,
        savingMemoryToggle: false,

        // Language voice config
        langConfigLoading: true,
        langRows: [],            // [{lang, maleVoice, femaleVoice}]
        fallbackLanguage: "en",
        allLanguages: [],
        allTtsProviders: [],     // from /api/speech/tts-engine -> providers (all, including unready)
        langAddMenuOpen: false,
        savingLangConfig: false,

        // Memory tab
        memoryLoading: true,
        memoryError: null,
        memoryConfig: null,
        savingMemory: false,
        memoryStatus: "",

        // Embedding provider card (Remote Services)
        embeddingLoading: true,
        embeddingProvider: null,   // { endpoint, keySet, model, dimensions, isDefault }
        embeddingEndpointInput: "",
        embeddingKeyInput: "",     // blank = untouched; non-blank = user typed a new value
        embeddingTestResult: null, // { ok, dim?, error? } | null
        embeddingSaving: false,
        embeddingTesting: false,
        embeddingResetting: false,
        showEmbeddingResetModal: false,

        // Coding tab
        codaAuthLoading: false,
        codaAuthStatus: null,
        codaFoldersLoading: false,
        codaFolders: [],
        codaAddForm: { path: "", label: "", policy: "YoloSafe" },
        codaAddError: "",
        codaSource: { source: "auto", resolvedPath: "", version: "", bundlePresent: false },
        codaSourceLoading: false,
        codaSourceSaving: false,
        codaMcp: { policy: "host", curatedMcpDir: "" },
        codaMcpLoading: false,

        // ── Lifecycle ──────────────────────────────────────

        async init() {
            await this.loadSettings();
        },

        // ── General Tab ────────────────────────────────────

        async loadSettings() {
            this.loading = true;
            uiTelemetry("global-settings.js", "loadSettings.start", {});
            try {
                const data = await api.get("/api/settings");
                this.providers = data.providers || [];
                this.fallbackOrder = [...(data.fallbackOrder || [])];
                this.maxSubagentRounds = data.maxSubagentRounds ?? 0;
                this.maxConcurrentSubagents = data.maxConcurrentSubagents ?? 5;

                this.defaultModels = {};
                this.memoryModels = {};
                this.providers.forEach(p => {
                    this.defaultModels[p.name] = p.defaultModel || (p.models.length > 0 ? p.models[0] : "");
                    this.memoryModels[p.name] = p.memoryModel || "";
                });

                const webUi = data.webUi || {};
                this.webUiInfo = `${webUi.bindAddress || "?"}:${webUi.port || "?"}`;
                this.agentConnected = !!data.agentConnected;

                this._original = {
                    fallbackOrder: [...this.fallbackOrder],
                    defaultModels: { ...this.defaultModels },
                    memoryModels: { ...this.memoryModels },
                    maxSubagentRounds: this.maxSubagentRounds,
                    maxConcurrentSubagents: this.maxConcurrentSubagents,
                };
                this.dirty = false;

                this.loadSpeechTogglesFromSettings(data);
                this.loadMemoryToggleFromSettings(data);

                uiTelemetry("global-settings.js", "loadSettings.success", {
                    providerCount: this.providers.length,
                    providers: this.providers.map(p => ({
                        name: p.name,
                        modelCount: (p.models || []).length,
                        defaultModel: p.defaultModel || null,
                        memoryModel: p.memoryModel || null,
                        apiKeyConfigured: !!p.apiKeyConfigured,
                    })),
                });

                // Load secondary data in parallel
                await Promise.all([
                    this.loadChannels(),
                    this.loadSpeechStatus(),
                    this.loadLangConfig(),
                    this.loadMemorySettings(),
                    this.loadEmbeddingProvider(),
                ]);
            } catch (e) {
                uiTelemetry("global-settings.js", "loadSettings.error", { message: String(e && e.message || e) });
                Alpine.store("toast").error("Failed to load settings: " + e.message);
            }
            this.loading = false;
        },

        checkDirty() {
            if (!this._original) { this.dirty = false; return; }
            const o = this._original;
            this.dirty = (
                JSON.stringify(this.fallbackOrder) !== JSON.stringify(o.fallbackOrder) ||
                JSON.stringify(this.defaultModels) !== JSON.stringify(o.defaultModels) ||
                JSON.stringify(this.memoryModels) !== JSON.stringify(o.memoryModels) ||
                this.maxSubagentRounds !== o.maxSubagentRounds ||
                this.maxConcurrentSubagents !== o.maxConcurrentSubagents
            );
        },

        onDefaultModelChange(providerName, value) {
            this.defaultModels[providerName] = value;
            this.checkDirty();
        },

        onMemoryModelChange(providerName, value) {
            this.memoryModels[providerName] = value;
            this.checkDirty();
        },

        onMaxRoundsInput(value) {
            this.maxSubagentRounds = parseInt(value, 10) || 0;
            this.checkDirty();
        },

        onMaxConcurrentSubagentsInput(value) {
            this.maxConcurrentSubagents = Math.min(20, Math.max(1, parseInt(value, 10) || 5));
            this.checkDirty();
        },

        // ── Fallback reorder (drag-and-drop) ───────────────

        _draggedProvider: null,

        dragStart(e, name) {
            this._draggedProvider = name;
            e.dataTransfer.effectAllowed = "move";
            e.target.classList.add("dragging");
        },

        dragEnd(e) {
            e.target.classList.remove("dragging");
            this._draggedProvider = null;
        },

        dragOver(e) {
            e.preventDefault();
            e.dataTransfer.dropEffect = "move";
        },

        drop(e, targetName) {
            e.preventDefault();
            const draggedName = this._draggedProvider;
            if (!draggedName || draggedName === targetName) return;

            const order = [...this.fallbackOrder];
            const fromIdx = order.indexOf(draggedName);
            const toIdx = order.indexOf(targetName);
            order.splice(fromIdx, 1);
            order.splice(toIdx, 0, draggedName);
            this.fallbackOrder = order;
            this.checkDirty();
        },

        // ── Refresh models ──────────────────────────────────

        isRefreshing(providerName) {
            return !!this.refreshingProviders[providerName];
        },

        async refreshModels(providerName) {
            const existing = this.providers.find(p => p.name === providerName);
            const beforeIds = [...(existing?.models || [])];

            this.refreshingProviders = { ...this.refreshingProviders, [providerName]: true };
            uiTelemetry("global-settings.js", "refreshModels.start", {
                providerName,
                beforeCount: beforeIds.length,
            });

            try {
                const result = await api.post("/api/settings/refresh-models", { providerName });
                const newModelIds = (result.models || []).map(m => m.id).filter(Boolean);

                if (newModelIds.length === 0) {
                    uiTelemetry("global-settings.js", "refreshModels.empty", { providerName, beforeCount: beforeIds.length });
                    Alpine.store("toast").error("No models returned from provider API");
                    return;
                }

                // Compute the diff locally so the toast can be informative and
                // the telemetry carries the same signal the server just logged.
                const beforeSet = new Set(beforeIds);
                const afterSet = new Set(newModelIds);
                const added = newModelIds.filter(id => !beforeSet.has(id));
                const removed = beforeIds.filter(id => !afterSet.has(id));

                await api.post("/api/settings", { providerModels: { [providerName]: newModelIds } });

                uiTelemetry("global-settings.js", "refreshModels.success", {
                    providerName,
                    beforeCount: beforeIds.length,
                    afterCount: newModelIds.length,
                    added,
                    removed,
                });

                if (added.length === 0 && removed.length === 0) {
                    Alpine.store("toast").success(`${providerName}: no changes (${newModelIds.length} models)`);
                } else {
                    const parts = [];
                    if (added.length > 0) parts.push(`+${added.length} (${added.slice(0, 3).join(", ")}${added.length > 3 ? "…" : ""})`);
                    if (removed.length > 0) parts.push(`-${removed.length} (${removed.slice(0, 3).join(", ")}${removed.length > 3 ? "…" : ""})`);
                    Alpine.store("toast").success(`${providerName}: ${beforeIds.length} → ${newModelIds.length} · ${parts.join(", ")}`);
                }

                await this.loadSettings();
            } catch (e) {
                uiTelemetry("global-settings.js", "refreshModels.error", {
                    providerName,
                    message: String(e && e.message || e),
                });
                Alpine.store("toast").error("Refresh failed: " + e.message);
            } finally {
                const next = { ...this.refreshingProviders };
                delete next[providerName];
                this.refreshingProviders = next;
            }
        },

        // ── Save general settings ───────────────────────────

        async saveGeneralSettings() {
            this.saving = true;
            const payload = {};
            const o = this._original;

            if (JSON.stringify(this.fallbackOrder) !== JSON.stringify(o.fallbackOrder)) {
                payload.fallbackOrder = this.fallbackOrder;
            }
            if (JSON.stringify(this.defaultModels) !== JSON.stringify(o.defaultModels)) {
                payload.providerDefaultModels = this.defaultModels;
            }
            if (JSON.stringify(this.memoryModels) !== JSON.stringify(o.memoryModels)) {
                payload.providerMemoryModels = this.memoryModels;
            }
            if (this.maxSubagentRounds !== o.maxSubagentRounds) {
                payload.maxSubagentRounds = this.maxSubagentRounds;
            }
            if (this.maxConcurrentSubagents !== o.maxConcurrentSubagents) {
                payload.maxConcurrentSubagents = this.maxConcurrentSubagents;
            }

            try {
                await api.post("/api/settings", payload);
                this._original = {
                    fallbackOrder: [...this.fallbackOrder],
                    defaultModels: { ...this.defaultModels },
                    memoryModels: { ...this.memoryModels },
                    maxSubagentRounds: this.maxSubagentRounds,
                    maxConcurrentSubagents: this.maxConcurrentSubagents,
                };
                this.dirty = false;
                Alpine.store("toast").success("Settings saved");
                await this.loadSettings();

                // LLM provider config (fallback order, default/memory models, maxSubagentRounds) is
                // wired at startup — DirectLlmClient and AgentRuntime capture it at boot, so those
                // need a Bridge restart. maxConcurrentSubagents is pushed live via UpdateConfigAsync
                // and does NOT require a restart.
                const restartRequiringKeys = Object.keys(payload).filter(k => k !== 'maxConcurrentSubagents');
                if (restartRequiringKeys.length > 0 && window.cortexRestart) {
                    window.cortexRestart.promptAndRestart({
                        reason: "LLM provider changes need a Bridge restart to take effect.",
                    });
                }
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.saving = false;
        },

        // ── Channels Tab ───────────────────────────────────

        async loadChannels() {
            try {
                this.channels = await api.get("/api/channels") || [];
                // Compute virtual field values for voice channel
                this.channels.forEach(ch => {
                    if (!ch._formSettings) {
                        ch._formSettings = { ...(ch.settings || {}) };
                    }
                    if (ch.type === "voice") {
                        const ptt = (ch._formSettings["PushToTalk"] || "true").toLowerCase() === "true";
                        ch._formSettings["_ActivationMode"] = ptt ? "push-to-talk" : "open-mic";
                    }
                });
            } catch (e) {
                console.error("Failed to load channels:", e);
            }
        },

        channelIcon(type) {
            return channelIcons[type] || "bi-plug";
        },

        channelSchema(type) {
            return channelSettingsSchema[type] || [];
        },

        getFieldValue(ch, field) {
            if (field.virtual) {
                return ch._formSettings?.[field.key] || field.default || "";
            }
            return ch._formSettings?.[field.key] || ch.settings?.[field.key] || field.default || "";
        },

        setFieldValue(ch, field, value) {
            if (!ch._formSettings) ch._formSettings = {};
            ch._formSettings[field.key] = value;
        },

        isFieldVisible(ch, field) {
            if (!field.showWhen) return true;
            // Resolve the dependency the same way getFieldValue does
            // (formSettings → saved settings → schema default) so a field
            // gated on a defaulted-but-unsaved setting (e.g. EnableBargeIn,
            // default "true") is visible on a fresh install.
            const depKey = field.showWhen.key;
            const depDefault = (channelSettingsSchema[ch.type] || [])
                .find(f => f.key === depKey)?.default;
            const depVal = ch._formSettings?.[depKey]
                || ch.settings?.[depKey]
                || depDefault
                || "";
            return depVal === field.showWhen.value;
        },

        async toggleChannel(ch, enabled) {
            const settings = this._collectChannelSettings(ch);
            try {
                const data = await api.post(`/api/channels/${ch.type}`, { enabled, settings });
                ch.enabled = enabled;
                if (data.restartRequired) {
                    this.showRestartBanner = true;
                    if (window.cortexRestart) {
                        window.cortexRestart.promptAndRestart({
                            reason: `Enabling/disabling ${ch.displayName} needs a Bridge restart to take effect.`,
                        });
                    }
                }
                Alpine.store("toast").success(`${ch.displayName} ${enabled ? "enabled" : "disabled"}`);
            } catch (e) {
                Alpine.store("toast").error(e.message);
                // Revert
                ch.enabled = !enabled;
            }
        },

        async saveChannelSettings(ch) {
            this.savingChannel = ch.type;
            const settings = this._collectChannelSettings(ch);
            try {
                const data = await api.post(`/api/channels/${ch.type}`, { enabled: ch.enabled, settings });
                if (data.restartRequired) {
                    this.showRestartBanner = true;
                    if (window.cortexRestart) {
                        window.cortexRestart.promptAndRestart({
                            reason: `${ch.displayName} settings need a Bridge restart to take effect.`,
                        });
                    }
                }
                Alpine.store("toast").success("Channel settings saved");
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.savingChannel = null;
        },

        _collectChannelSettings(ch) {
            const settings = {};
            const formSettings = ch._formSettings || {};
            const schema = channelSettingsSchema[ch.type] || [];

            schema.forEach(field => {
                if (field.key.startsWith("_")) return; // skip virtual
                const val = formSettings[field.key];
                if (val !== undefined) {
                    settings[field.key] = val;
                }
            });

            // Also include real settings not in schema
            Object.entries(formSettings).forEach(([key, val]) => {
                if (!key.startsWith("_") && !(key in settings)) {
                    settings[key] = val;
                }
            });

            // Map virtual _ActivationMode back to PushToTalk boolean
            if (ch.type === "voice") {
                const mode = formSettings["_ActivationMode"];
                if (mode) {
                    settings["PushToTalk"] = mode === "push-to-talk" ? "true" : "false";
                }
            }

            return settings;
        },

        _prereqResults: {},

        async checkPrerequisites(channelType) {
            this._prereqResults[channelType] = { loading: true, checks: [], ready: true };
            try {
                const data = await api.get(`/api/channels/${channelType}/prerequisites`);
                this._prereqResults[channelType] = { loading: false, checks: data.checks || [], ready: data.ready };
            } catch (e) {
                this._prereqResults[channelType] = { loading: false, checks: [], ready: false, error: e.message };
            }
        },

        getPrereqs(channelType) {
            return this._prereqResults[channelType] || null;
        },

        // ── Speech Tab ─────────────────────────────────────

        async loadSpeechStatus() {
            this.speechLoading = true;
            this.speechError = null;
            try {
                const data = await api.get("/api/speech/status");
                this.sttReady = data.stt?.ready || false;
                this.speechProviders = data.providers || [];
            } catch (e) {
                this.speechError = e.message;
            }
            this.speechLoading = false;
        },

        _applySpeechToggles(speech) {
            this.speechEnabled = !!speech.enabled;
            this.sttEnabled = !!speech.stt?.enabled;
            this.ttsEnabled = !!speech.tts?.enabled;
            this.voiceIdEnabled = !!speech.voiceId?.enabled;
        },

        loadSpeechTogglesFromSettings(data) {
            if (data?.speech) {
                this._applySpeechToggles(data.speech);
            }
        },

        async saveSpeechToggles() {
            this.savingSpeechToggles = true;
            try {
                const payload = {
                    speechEnabled: this.speechEnabled,
                    sttEnabled: this.sttEnabled,
                    ttsEnabled: this.ttsEnabled,
                    voiceIdEnabled: this.voiceIdEnabled,
                };
                // The @change handlers optimistically wrote the new values to Alpine
                // state before this POST. api.post throws on any non-2xx (see
                // api-helper.js), so a thrown error is the usual failure path — but a
                // 200 with success:false is still handled below. In both failure
                // cases we resync the toggles to what the backend actually has so the
                // checkboxes can't drift out of sync with the optimistic write.
                const data = await api.post("/api/speech/toggles", payload);
                if (data?.success) {
                    this._applySpeechToggles({
                        enabled: data.speechEnabled,
                        stt: { enabled: data.sttEnabled },
                        tts: { enabled: data.ttsEnabled },
                        voiceId: { enabled: data.voiceIdEnabled },
                    });
                    this.voiceRestartRequired = !!data.restartRequired;
                    Alpine.store("toast").success(
                        data.restartRequired
                            ? "Voice settings saved — restart required to fully apply voice-id"
                            : "Voice settings saved");
                } else {
                    Alpine.store("toast").error("Failed to save voice settings");
                    await this.loadSettings();
                }
            } catch (e) {
                Alpine.store("toast").error("Failed to save voice settings: " + e.message);
                // Re-apply known-good state from the backend so the optimistic
                // checkbox value doesn't linger after a failed save.
                await this.loadSettings();
            } finally {
                this.savingSpeechToggles = false;
            }
        },

        loadMemoryToggleFromSettings(data) {
            if (data?.memory) {
                this.memoryEnabled = !!data.memory.enabled;
            }
        },

        async saveMemoryToggle() {
            this.savingMemoryToggle = true;
            try {
                const data = await api.post("/api/memory/toggle", { enabled: this.memoryEnabled });
                if (data?.success) {
                    this.memoryEnabled = !!data.enabled;
                    Alpine.store("toast").success("Memory " + (data.enabled ? "enabled" : "disabled"));
                } else {
                    Alpine.store("toast").error("Failed to save memory toggle");
                    await this.loadSettings();
                }
            } catch (e) {
                Alpine.store("toast").error("Failed to save memory toggle: " + e.message);
                await this.loadSettings();
            } finally {
                this.savingMemoryToggle = false;
            }
        },

        async downloadSpeechModel(providerOrModel, label) {
            try {
                Alpine.store("toast").info(`Downloading ${label}...`);
                await api.post(`/api/speech/download-model/${providerOrModel}`);
                Alpine.store("toast").success(`${label} downloaded. Restart required.`);
                this.showRestartBanner = true;
                await this.loadSpeechStatus();
            } catch (e) {
                Alpine.store("toast").error("Download failed: " + e.message);
            }
        },

        async downloadProvider(providerName) {
            try {
                Alpine.store("toast").info(`Downloading ${ttsEngineLabels[providerName] || providerName}...`);
                await api.post(`/api/speech/download-provider/${providerName}`);
                Alpine.store("toast").success(`${ttsEngineLabels[providerName] || providerName} downloaded. Restart required.`);
                this.showRestartBanner = true;
                await this.loadSpeechStatus();
            } catch (e) {
                Alpine.store("toast").error("Download failed: " + e.message);
            }
        },

        providerLabel(name) {
            return ttsEngineLabels[name] || name;
        },

        providerLanguages(provider) {
            const langs = [...new Set((provider.voices || []).map(v => v.language))];
            return langs.map(l => languageNames[l] || l.toUpperCase()).join(", ");
        },

        // ── Language Voice Config ──────────────────────────

        async loadLangConfig() {
            this.langConfigLoading = true;
            try {
                const data = await api.get("/api/speech/tts-engine");
                if (!data) { this.langConfigLoading = false; return; }

                this.allTtsProviders = data.providers || [];
                this.fallbackLanguage = data.defaultLanguage || "en";

                // Build full language list
                const providerLanguages = [...new Set(this.allTtsProviders.flatMap(p => (p.voices || []).map(v => v.language)))];
                const commonLanguages = ["en", "ru", "de", "fr", "es", "it", "ja", "ko", "zh", "uk", "pl", "pt"];
                this.allLanguages = [...new Set([...providerLanguages, ...commonLanguages])].sort();

                // Build rows from current config
                const currentConfig = data.languages || {};
                const configuredLanguages = Object.keys(currentConfig);
                this.langRows = [];

                if (configuredLanguages.length > 0) {
                    for (const lang of configuredLanguages) {
                        const cfg = currentConfig[lang] || {};
                        this.langRows.push({
                            lang,
                            maleVoice: cfg.maleVoice || cfg.MaleVoice || "",
                            femaleVoice: cfg.femaleVoice || cfg.FemaleVoice || "",
                        });
                    }
                } else {
                    this.langRows.push({ lang: "en", maleVoice: "", femaleVoice: "" });
                }
            } catch (e) {
                // Silently ignore — endpoint may not exist
            }
            this.langConfigLoading = false;
        },

        /** Get voices for a given gender + language across all providers. Returns [{group, voices: [{value, label}]}]. */
        getVoiceOptions(gender, language) {
            const groups = [];
            for (const p of this.allTtsProviders) {
                const voices = (p.voices || []).filter(v => v.gender === gender && v.language === language);
                if (voices.length === 0) continue;
                groups.push({
                    label: p.ready ? (ttsEngineLabels[p.name] || p.name) : `${ttsEngineLabels[p.name] || p.name} (not downloaded)`,
                    voices: voices.map(v => ({
                        value: `${p.name}:${v.name}`,
                        label: v.description || v.name,
                    })),
                });
            }
            return groups;
        },

        get unusedLanguages() {
            const used = new Set(this.langRows.map(r => r.lang));
            return this.allLanguages.filter(l => !used.has(l));
        },

        addLanguageRow(lang) {
            this.langRows.push({ lang, maleVoice: "", femaleVoice: "" });
            this.langAddMenuOpen = false;
        },

        removeLanguageRow(lang) {
            this.langRows = this.langRows.filter(r => r.lang !== lang);
        },

        langName(code) {
            return languageNames[code] || code.toUpperCase();
        },

        async saveLangConfig() {
            this.savingLangConfig = true;
            const languages = {};
            for (const r of this.langRows) {
                languages[r.lang] = {
                    maleVoice: r.maleVoice,
                    femaleVoice: r.femaleVoice,
                };
            }
            const defaultLang = this.fallbackLanguage || this.langRows[0]?.lang || "en";
            try {
                await api.post("/api/speech/language-config", { defaultLanguage: defaultLang, languages });
                Alpine.store("toast").success("Language configuration saved.");
            } catch (e) {
                Alpine.store("toast").error("Save failed: " + e.message);
            }
            this.savingLangConfig = false;
        },

        // ── Memory Tab ─────────────────────────────────────

        async loadMemorySettings() {
            this.memoryLoading = true;
            this.memoryError = null;
            try {
                const config = await api.get("/api/memory/settings");
                this.memoryConfig = {
                    duplicateThreshold: config.duplicateThreshold ?? 0.90,
                    compactionSimilarityThreshold: config.compactionSimilarityThreshold ?? 0.70,
                    compactionEnabled: config.compactionEnabled ?? true,
                    idleCompactionEnabled: config.idleCompactionEnabled ?? true,
                    idleResetMinutes: config.idleResetMinutes ?? 360,
                    compactionPreserveRecentTurns: config.compactionPreserveRecentTurns ?? 4,
                };
            } catch (e) {
                if (e.message && e.message.includes("503")) {
                    this.memoryError = "Agent not connected";
                } else {
                    this.memoryError = "Failed to load memory settings";
                }
            }
            this.memoryLoading = false;
        },

        async saveMemorySettings() {
            if (!this.memoryConfig) return;
            this.savingMemory = true;
            this.memoryStatus = "";
            try {
                await api.put("/api/memory/settings", this.memoryConfig);
                Alpine.store("toast").success("Memory settings saved");
                this.memoryStatus = "Saved -- takes effect immediately";
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.savingMemory = false;
        },

        // ── Embedding Provider (Remote Services) ──────────

        async loadEmbeddingProvider() {
            this.embeddingLoading = true;
            try {
                const data = await api.get("/api/memory/embedding-provider");
                this._applyEmbeddingProvider(data);
            } catch (e) {
                // Silently swallow — the card shows loading spinner until data arrives,
                // and a missing endpoint shouldn't block the rest of the settings page.
                console.warn("Failed to load embedding provider:", e);
            }
            this.embeddingLoading = false;
        },

        _applyEmbeddingProvider(data) {
            this.embeddingProvider = data;
            this.embeddingEndpointInput = data.endpoint || "";
            // Always clear the key input when (re-)loading — it was never filled
            // from the server (the API never returns the actual key value).
            this.embeddingKeyInput = "";
            this.embeddingTestResult = null;
        },

        async saveEmbeddingProvider() {
            this.embeddingSaving = true;
            try {
                const payload = { endpoint: this.embeddingEndpointInput };
                // Key handling: if the user typed something, send it (set or clear).
                // If the key input is blank AND a key is already stored, OMIT apiKey
                // so the existing key is preserved.
                const keyTrimmed = this.embeddingKeyInput.trim();
                if (keyTrimmed !== "" || !this.embeddingProvider?.keySet) {
                    payload.apiKey = keyTrimmed;
                }
                const data = await api.post("/api/memory/embedding-provider/save", payload);
                this._applyEmbeddingProvider(data);
                Alpine.store("toast").success("Embedding provider saved");
            } catch (e) {
                Alpine.store("toast").error("Save failed: " + e.message);
            }
            this.embeddingSaving = false;
        },

        async clearEmbeddingKey() {
            try {
                const data = await api.post("/api/memory/embedding-provider/clear-key");
                this._applyEmbeddingProvider(data);
                Alpine.store("toast").success("API key cleared");
            } catch (e) {
                Alpine.store("toast").error("Clear failed: " + e.message);
            }
        },

        // Confirmation runs through the app's own Alpine modal (showEmbeddingResetModal),
        // not a native confirm() dialog. The modal's Reset button invokes this directly.
        async resetEmbeddingProvider() {
            this.embeddingResetting = true;
            try {
                const data = await api.post("/api/memory/embedding-provider/reset");
                this._applyEmbeddingProvider(data);
                this.showEmbeddingResetModal = false;
                Alpine.store("toast").success("Reset to local default");
            } catch (e) {
                Alpine.store("toast").error("Reset failed: " + e.message);
            }
            this.embeddingResetting = false;
        },

        async testEmbeddingProvider() {
            this.embeddingTesting = true;
            this.embeddingTestResult = null;
            try {
                const payload = {};
                // Send the current (possibly unsaved) endpoint if it differs
                if (this.embeddingEndpointInput) {
                    payload.endpoint = this.embeddingEndpointInput;
                }
                // Send the key input only if the user typed something
                const keyTrimmed = this.embeddingKeyInput.trim();
                if (keyTrimmed !== "") {
                    payload.apiKey = keyTrimmed;
                }
                const result = await api.post("/api/memory/embedding-provider/test", payload);
                this.embeddingTestResult = result;
            } catch (e) {
                this.embeddingTestResult = { ok: false, error: e.message };
            }
            this.embeddingTesting = false;
        },

        // ── Coding Tab ─────────────────────────────────────

        async loadCodingFolders() {
            this.codaFoldersLoading = true;
            this.codaAuthLoading = true;
            try {
                const [folders, authStatus] = await Promise.all([
                    api.get("/api/coding-folders").catch(() => []),
                    api.get("/api/coding/auth-status").catch(() => null),
                ]);
                this.codaFolders = folders || [];
                this.codaAuthStatus = authStatus;
            } catch (e) {
                Alpine.store("toast").error("Failed to load coding folders: " + e.message);
            }
            this.codaFoldersLoading = false;
            this.codaAuthLoading = false;
            await this.loadCodaSource();
            await this.loadCodaMcpSettings();
        },

        async loadCodaMcpSettings() {
            this.codaMcpLoading = true;
            try {
                const data = await api.get("/api/coding/mcp-settings");
                this.codaMcp = { policy: data.mcp || "host", curatedMcpDir: data.curatedMcpDir || "" };
            } catch (e) {
                // Silently ignore — endpoint may not be available yet
            }
            this.codaMcpLoading = false;
        },

        async saveCodaMcpSettings() {
            try {
                const policy = this.codaMcp.policy || "host";
                const payload = {
                    mcp: policy,
                    // Only send the curated dir when it's relevant, so a stale value isn't persisted.
                    curatedMcpDir: policy === "curated" ? (this.codaMcp.curatedMcpDir || "").trim() || undefined : undefined,
                };
                await api.put("/api/coding/mcp-settings", payload);
                Alpine.store("toast").success("MCP policy saved");
                await this.loadCodaMcpSettings();
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        // Which coda binary the Bridge launches: Auto (bundled-else-host) / Host (PATH) /
        // Built-in (bundled). Route is not tenant-scoped — a single setting per Bridge,
        // mirroring the MCP-policy card. GET/PUT return the freshly-resolved state
        // (source + resolvedPath + version + bundlePresent).
        async loadCodaSource() {
            this.codaSourceLoading = true;
            try {
                this.codaSource = await api.get("/api/coding/coda-source");
            } catch (e) {
                Alpine.store("toast").error("Failed to load coda source");
            }
            this.codaSourceLoading = false;
        },

        async saveCodaSource() {
            this.codaSourceSaving = true;
            try {
                this.codaSource = await api.put("/api/coding/coda-source", { source: this.codaSource.source });
                Alpine.store("toast").success("Coda source saved");
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.codaSourceSaving = false;
        },

        async addCodingFolder() {
            this.codaAddError = "";
            const path = this.codaAddForm.path.trim();
            if (!path) return;
            try {
                await api.post("/api/coding-folders", {
                    path,
                    label: this.codaAddForm.label.trim() || undefined,
                    policy: this.codaAddForm.policy,
                });
                this.codaAddForm = { path: "", label: "", policy: "YoloSafe" };
                await this.loadCodingFolders();
                Alpine.store("toast").success("Folder added");
            } catch (e) {
                this.codaAddError = e.message;
            }
        },

        async removeCodingFolder(path) {
            try {
                await api.del("/api/coding-folders", { path });
                await this.loadCodingFolders();
                Alpine.store("toast").success("Folder removed");
            } catch (e) {
                Alpine.store("toast").error("Remove failed: " + e.message);
            }
        },
    };
}
