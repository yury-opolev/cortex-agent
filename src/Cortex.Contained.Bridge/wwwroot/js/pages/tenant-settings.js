"use strict";

/**
 * Per-tenant settings page.
 * Discord pairing, API channel management, personality editing.
 */
function tenantSettingsPage() {
    return {
        tenant: null,
        loading: true,
        saving: false,

        // Editable fields
        persona: "",
        apiEnabled: false,
        enabled: true,
        voiceGender: "female",

        // Voice channel config
        discordGuildId: "",
        discordVoiceChannelId: "",
        voiceGreeting: "",
        savingVoice: false,

        // Personality
        personalityLoading: false,
        savingPersonality: false,
        personalityStatus: "",

        // System Prompt
        systemPrompt: { mainTemplate: "", subagentTemplate: "", voiceMode: "", codingRelay: "", subagentInstructions: "" },
        systemPromptLoading: false,
        savingSystemPrompt: false,
        systemPromptErrors: [],
        systemPromptWarnings: [],
        systemPromptPreview: "",

        // Self-Notes
        selfNotes: "",
        selfNotesLoading: false,
        savingSelfNotes: false,
        selfNotesStatus: "",

        // Voice identification
        voiceIdLoading: false,
        savingVoiceId: false,
        voiceIdState: "Unknown",
        voiceIdFeatureEnabled: true,
        voiceIdEmbeddingDim: 0,
        voiceIdModelId: null,
        voiceIdThresholdOverride: null,
        voiceIdThresholdOverrideInput: "",
        voiceIdShowAdvanced: false,
        voiceIdStatus: "",

        // Setup code
        setupCode: null,
        setupCodeExpiry: null,
        codeCountdown: "",
        _countdownTimer: null,

        // Discord bot info (for setup instructions)
        botUsername: null,
        botUserId: null,
        botApplicationId: null,
        expiryHours: 24,

        // API key
        apiKey: null,
        apiKeyRevealed: false,

        get tenantId() { return Alpine.store("router").tenantId; },

        async init() {
            await Promise.all([this.load(), this.loadBotInfo()]);
            await Promise.all([this.loadPersonality(), this.loadSystemPrompt(), this.loadSelfNotes(), this.loadVoiceId()]);
        },

        async load() {
            this.loading = true;
            try {
                const tenants = await api.get("/api/tenants") || [];
                this.tenant = tenants.find(t => t.tenantId === this.tenantId);
                if (this.tenant) {
                    this.apiEnabled = this.tenant.apiEnabled;
                    this.enabled = this.tenant.enabled;
                    this.voiceGender = this.tenant.voiceGender || "female";
                    this.discordGuildId = this.tenant.discordGuildId || "";
                    this.discordVoiceChannelId = this.tenant.discordVoiceChannelId || "";
                    this.voiceGreeting = this.tenant.voiceGreeting || "";
                }
            } catch (e) {
                Alpine.store("toast").error("Failed to load tenant: " + e.message);
            }
            this.loading = false;
        },

        async loadBotInfo() {
            try {
                const info = await api.get("/api/discord/bot-info");
                this.botUsername = info?.username;
                this.botUserId = info?.userId;
                this.botApplicationId = info?.applicationId;
            } catch {
                // Discord may not be configured
            }
        },

        /** Build user-facing setup instructions for copy-paste. */
        get setupInstructions() {
            if (!this.setupCode) return "";
            const botLink = this.botUserId
                ? `1. Click this link to open the bot's profile in Discord:\n   https://discord.com/users/${this.botUserId}\n2. Click "Message" to start a DM, then send this code:\n`
                : `1. Open Discord and find the bot (ask your admin for the link)\n2. Send a direct message (DM) to the bot with this code:\n`;
            const expiryText = this.expiryHours >= 24
                ? `${Math.round(this.expiryHours / 24)} day(s)`
                : `${Math.round(this.expiryHours)} hour(s)`;
            return botLink + `\n   ${this.setupCode}\n\n3. The bot will confirm the connection.\n   Code expires in ${expiryText}.`;
        },

        async save() {
            this.saving = true;
            try {
                await api.put(`/api/tenants/${encodeURIComponent(this.tenantId)}`, {
                    apiEnabled: this.apiEnabled,
                    enabled: this.enabled,
                    voiceGender: this.voiceGender,
                });
                Alpine.store("toast").success("Settings saved");
                await this.load();
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.saving = false;
        },

        async saveVoiceConfig() {
            this.savingVoice = true;
            try {
                await api.put(`/api/tenants/${encodeURIComponent(this.tenantId)}`, {
                    discordGuildId: this.discordGuildId,
                    discordVoiceChannelId: this.discordVoiceChannelId,
                    voiceGreeting: this.voiceGreeting,
                });
                Alpine.store("toast").success("Voice settings saved");
                await this.load();
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.savingVoice = false;
        },

        // ── Personality ──────────────────────────────────────

        async loadPersonality() {
            this.personalityLoading = true;
            this.personalityStatus = "";
            try {
                const data = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/personality`);
                this.persona = data.personality || "";
            } catch (e) {
                this.personalityStatus = "Failed to load personality";
            }
            this.personalityLoading = false;
        },

        async savePersonality() {
            const text = this.persona.trim();
            if (!text) {
                Alpine.store("toast").error("Personality text cannot be empty");
                return;
            }

            this.savingPersonality = true;
            this.personalityStatus = "";
            try {
                await api.put(`/api/tenants/${encodeURIComponent(this.tenantId)}/personality`, {
                    personality: text,
                });
                Alpine.store("toast").success("Personality saved");
                this.personalityStatus = "Saved \u2014 takes effect on next message";
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.savingPersonality = false;
        },

        async resetPersonality() {
            this.savingPersonality = true;
            this.personalityStatus = "";
            try {
                const data = await api.del(`/api/tenants/${encodeURIComponent(this.tenantId)}/personality`);
                this.persona = data.personality || "";
                Alpine.store("toast").success("Personality reset to default");
                this.personalityStatus = "Reset \u2014 takes effect on next message";
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.savingPersonality = false;
        },

        // ── System Prompt ────────────────────────────────────

        async loadSystemPrompt() {
            this.systemPromptLoading = true;
            try {
                const data = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/system-prompt`);
                this.systemPrompt = data;
                await this.refreshPreview();
            } catch (e) {
                Alpine.store("toast").error("Failed to load system prompt");
            }
            this.systemPromptLoading = false;
        },

        async saveSystemPrompt() {
            this.savingSystemPrompt = true;
            this.systemPromptErrors = [];
            try {
                const res = await api.put(`/api/tenants/${encodeURIComponent(this.tenantId)}/system-prompt`, this.systemPrompt);
                this.systemPromptWarnings = res.warnings || [];
                Alpine.store("toast").success("System prompt saved");
                await this.refreshPreview();
            } catch (e) {
                this.systemPromptErrors = (e && e.body && e.body.errors) || [e.message || "Save failed"];
            }
            this.savingSystemPrompt = false;
        },

        async resetSystemPrompt() {
            this.savingSystemPrompt = true;
            try {
                this.systemPrompt = await api.del(`/api/tenants/${encodeURIComponent(this.tenantId)}/system-prompt`);
                this.systemPromptErrors = [];
                this.systemPromptWarnings = [];
                Alpine.store("toast").success("System prompt reset to default");
                await this.refreshPreview();
            } catch (e) {
                Alpine.store("toast").error("Reset failed");
            }
            this.savingSystemPrompt = false;
        },

        async refreshPreview() {
            try {
                const data = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/system-prompt/preview?channel=web&voice=false`);
                this.systemPromptPreview = data.preview || "";
            } catch (e) {
                // preview is best-effort
            }
        },

        insertPlaceholder(field, name) {
            this.systemPrompt[field] = (this.systemPrompt[field] || "") + "{{" + name + "}}";
        },

        // ── Self-Notes ──────────────────────────────────────

        async loadSelfNotes() {
            this.selfNotesLoading = true;
            this.selfNotesStatus = "";
            try {
                const data = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/self-notes`);
                this.selfNotes = data.selfNotes || "";
            } catch (e) {
                this.selfNotesStatus = "Failed to load self-notes";
            }
            this.selfNotesLoading = false;
        },

        async saveSelfNotes() {
            const text = this.selfNotes.trim();
            if (!text) {
                Alpine.store("toast").error("Self-notes text cannot be empty");
                return;
            }

            this.savingSelfNotes = true;
            this.selfNotesStatus = "";
            try {
                await api.put(`/api/tenants/${encodeURIComponent(this.tenantId)}/self-notes`, {
                    selfNotes: text,
                });
                Alpine.store("toast").success("Self-notes saved");
                this.selfNotesStatus = "Saved \u2014 takes effect on next message";
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.savingSelfNotes = false;
        },

        async resetSelfNotes() {
            this.savingSelfNotes = true;
            this.selfNotesStatus = "";
            try {
                const data = await api.del(`/api/tenants/${encodeURIComponent(this.tenantId)}/self-notes`);
                this.selfNotes = data.selfNotes || "";
                Alpine.store("toast").success("Self-notes reset to default");
                this.selfNotesStatus = "Reset \u2014 takes effect on next message";
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.savingSelfNotes = false;
        },

        // \u2500\u2500 Voice Identification \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        async loadVoiceId() {
            this.voiceIdLoading = true;
            this.voiceIdStatus = "";
            try {
                const data = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/voice-id`);
                this.voiceIdState = data.state || "Unknown";
                this.voiceIdFeatureEnabled = data.featureEnabled !== false;
                this.voiceIdEmbeddingDim = data.embeddingDim || 0;
                this.voiceIdModelId = data.modelId || null;
                this.voiceIdThresholdOverride = data.thresholdOverride;
                this.voiceIdThresholdOverrideInput = data.thresholdOverride != null
                    ? String(data.thresholdOverride) : "";
            } catch (e) {
                this.voiceIdStatus = "Failed to load voice-id settings";
            }
            this.voiceIdLoading = false;
        },

        async saveVoiceIdFeatureFlag() {
            // Capture intent at call entry so a revert after a failed PUT
            // restores the *intended-before* state even if a second click
            // landed before the disable kicked in.
            const intended = this.voiceIdFeatureEnabled;
            this.savingVoiceId = true;
            this.voiceIdStatus = "";
            try {
                await api.put(`/api/tenants/${encodeURIComponent(this.tenantId)}/voice-id`, {
                    featureEnabled: intended,
                });
                Alpine.store("toast").success(intended
                    ? "Voice identification enabled"
                    : "Voice identification disabled");
            } catch (e) {
                Alpine.store("toast").error(e.message);
                this.voiceIdFeatureEnabled = !intended;
            }
            this.savingVoiceId = false;
        },

        async saveVoiceIdThreshold() {
            this.savingVoiceId = true;
            this.voiceIdStatus = "";
            let threshold = null;
            if (this.voiceIdThresholdOverrideInput.trim() !== "") {
                const parsed = Number(this.voiceIdThresholdOverrideInput);
                if (!Number.isFinite(parsed) || parsed < 0 || parsed > 1) {
                    Alpine.store("toast").error("Threshold must be a number between 0 and 1");
                    this.savingVoiceId = false;
                    return;
                }
                threshold = parsed;
            }
            try {
                await api.put(`/api/tenants/${encodeURIComponent(this.tenantId)}/voice-id`, {
                    thresholdOverride: threshold,
                    thresholdOverridePresent: true,
                });
                this.voiceIdThresholdOverride = threshold;
                Alpine.store("toast").success(threshold == null
                    ? "Threshold override cleared"
                    : `Threshold override set to ${threshold}`);
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.savingVoiceId = false;
        },

        async resetVoiceIdEnrollment() {
            if (!window.confirm("Wipe the voiceprint and stop verification for this tenant?")) {
                return;
            }
            this.savingVoiceId = true;
            this.voiceIdStatus = "";
            try {
                await api.del(`/api/tenants/${encodeURIComponent(this.tenantId)}/voice-id`);
                Alpine.store("toast").success("Voiceprint wiped");
                await this.loadVoiceId();
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.savingVoiceId = false;
        },

        async startVoiceIdEnrollment() {
            this.savingVoiceId = true;
            this.voiceIdStatus = "";
            try {
                await api.post(`/api/tenants/${encodeURIComponent(this.tenantId)}/voice-id/start-enrollment`, {});
                Alpine.store("toast").success("Enrollment armed — join the voice channel to capture samples");
                await this.loadVoiceId();
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.savingVoiceId = false;
        },

        get voiceIdCanStartEnrollment() {
            return this.voiceIdState === "Unknown" || this.voiceIdState === "Declined";
        },

        // ── Discord Pairing ──────────────────────────────────

        async generateCode() {
            try {
                const data = await api.post(`/api/tenants/${encodeURIComponent(this.tenantId)}/setup-code`);
                this.setupCode = data.code;
                this.setupCodeExpiry = data.expiresAt;
                if (data.expiryHours) this.expiryHours = data.expiryHours;
                this.startCountdown();
                Alpine.store("toast").success("Setup code generated");
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        async revokeCode() {
            try {
                await api.del(`/api/tenants/${encodeURIComponent(this.tenantId)}/setup-code`);
                this.setupCode = null;
                this.setupCodeExpiry = null;
                this.stopCountdown();
                Alpine.store("toast").info("Setup code revoked");
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        async unlinkDiscord() {
            try {
                await api.del(`/api/tenants/${encodeURIComponent(this.tenantId)}/discord`);
                this.tenant.discordUserId = null;
                this.tenant.discordUsername = null;
                Alpine.store("toast").success("Discord user unlinked");
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        startCountdown() {
            this.stopCountdown();
            this._countdownTimer = setInterval(() => {
                if (!this.setupCodeExpiry) { this.stopCountdown(); return; }
                const remaining = Math.max(0, Math.floor((this.setupCodeExpiry - Date.now()) / 1000));
                if (remaining <= 0) {
                    this.codeCountdown = "Expired";
                    this.setupCode = null;
                    this.stopCountdown();
                } else {
                    const m = Math.floor(remaining / 60);
                    const s = remaining % 60;
                    this.codeCountdown = `${m}:${String(s).padStart(2, "0")}`;
                }
            }, 1000);
        },

        stopCountdown() {
            clearInterval(this._countdownTimer);
            this._countdownTimer = null;
            this.codeCountdown = "";
        },

        copyCode() {
            if (this.setupCode) {
                navigator.clipboard.writeText(this.setupCode);
                Alpine.store("toast").info("Code copied to clipboard");
            }
        },

        copyInstructions() {
            if (this.setupInstructions) {
                navigator.clipboard.writeText(this.setupInstructions);
                Alpine.store("toast").info("Instructions copied to clipboard");
            }
        },

        // ── API Key Management ───────────────────────────────

        async generateApiKey() {
            try {
                const data = await api.post(`/api/tenants/${encodeURIComponent(this.tenantId)}/api-key`);
                this.apiKey = data.apiKey;
                this.apiKeyRevealed = true;
                Alpine.store("toast").success("API key generated");
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        async revokeApiKey() {
            try {
                await api.del(`/api/tenants/${encodeURIComponent(this.tenantId)}/api-key`);
                this.apiKey = null;
                this.apiKeyRevealed = false;
                Alpine.store("toast").success("API key revoked");
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        copyApiKey() {
            if (this.apiKey) {
                navigator.clipboard.writeText(this.apiKey);
                Alpine.store("toast").info("API key copied to clipboard");
            }
        },
    };
}
