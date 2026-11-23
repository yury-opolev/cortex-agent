"use strict";

/**
 * Per-tenant Storage page: what the agent stores and whether it is encrypted, the recovery kit
 * (export / restore of the tenant storage key, password-protected) and the one-way switch that
 * turns encryption at rest on (preview with pre-flight checks, re-authentication, typed confirmation).
 */
function tenantStoragePage() {
    return {
        get tenantId() { return Alpine.store("router").tenantId; },
        get base() { return `/api/tenants/${encodeURIComponent(this.tenantId)}/storage`; },

        connected: true,
        status: null,
        bridge: null,
        bridgeStores: [],
        statusTimer: null,

        preview: null,
        previewError: "",
        previewing: false,

        kit: { password: "", passphrase: "", passphrase2: "" },
        exporting: false,

        restore: { password: "", passphrase: "", text: "", fileName: "", replaceExisting: false },
        restoring: false,

        showEnableModal: false,
        enable: { password: "", confirm: "", erase: true, acceptNoKit: false, snapshotTaken: false },
        enabling: false,
        restarting: false,

        frozen: null,
        frozenPassword: "",
        downloading: "",

        async init() {
            await this.loadStatus();
            this.statusTimer = setInterval(() => this.loadStatus(), 5000);
        },

        destroy() {
            if (this.statusTimer) clearInterval(this.statusTimer);
        },

        async loadStatus() {
            try {
                const data = await api.get(`${this.base}/status`);
                this.applyStatus(data);
                this.connected = true;
            } catch (e) {
                this.applyStatus(e.body || {});
                this.connected = false;
            }
            if (this.restarting && this.status?.encryptAtRest && this.status.messagesState === "open" && this.status.memoryState === "open") {
                this.restarting = false;
                Alpine.store("toast").success("Encryption at rest is on and every encrypted store is open");
            }
            try {
                this.frozen = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/eval/frozen`);
            } catch {
                this.frozen = null;
            }
        },

        hasPackage(version) {
            return (this.frozen?.packages || []).includes(version.packageFileName);
        },

        /** Downloads a passphrase-encrypted evaluation package (Bridge password again); upload it to SafeExchange as described in the runbook. */
        async downloadPackage(name) {
            this.downloading = name;
            try {
                const resp = await fetch(`/api/tenants/${encodeURIComponent(this.tenantId)}/eval/frozen/packages/${encodeURIComponent(name)}`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ password: this.frozenPassword }),
                });
                if (resp.status === 401) {
                    window.location.href = "/login.html";
                    return;
                }
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({ error: `HTTP ${resp.status}` }));
                    throw new Error(err.error || `HTTP ${resp.status}`);
                }
                const url = URL.createObjectURL(await resp.blob());
                const a = document.createElement("a");
                a.href = url;
                a.download = name;
                a.click();
                URL.revokeObjectURL(url);
                this.frozenPassword = "";
                Alpine.store("toast").success(`${name} downloaded. Attach it to the SafeExchange secret cortex-recovery-kit under this file name.`);
            } catch (e) {
                Alpine.store("toast").error("Download: " + e.message);
            }
            this.downloading = "";
        },

        applyStatus(data) {
            this.status = data?.agent || null;
            this.bridge = data?.bridge || this.bridge;
            this.bridgeStores = data?.bridgeStores || this.bridgeStores;
        },

        get encrypted() { return !!this.status?.encryptAtRest; },

        get keyMismatch() {
            const expected = this.status?.expectedKeyFingerprint;
            return !!expected && !!this.bridge?.keyFingerprint && expected !== this.bridge.keyFingerprint;
        },

        get needsRestore() {
            return !!this.status?.hasEncryptedData && (!this.bridge?.keyPresent || this.bridge?.keyUnavailable || this.keyMismatch);
        },

        async loadPreview() {
            this.previewing = true;
            this.previewError = "";
            try {
                const data = await api.post(`${this.base}/encryption/preview`);
                this.preview = data?.preview || null;
                this.bridge = data?.bridge || this.bridge;
            } catch (e) {
                this.previewError = e.message;
            }
            this.previewing = false;
        },

        get kitError() {
            if (this.kit.passphrase.length < 12) return "Use a passphrase of at least 12 characters (four random words work well).";
            if (this.kit.passphrase !== this.kit.passphrase2) return "The passphrases do not match.";
            if (!this.kit.password) return "Enter your Bridge password.";
            return "";
        },

        async exportKit() {
            if (this.kitError) return;
            this.exporting = true;
            try {
                const resp = await fetch(`${this.base}/recovery-kit`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ password: this.kit.password, passphrase: this.kit.passphrase }),
                });
                if (resp.status === 401) {
                    window.location.href = "/login.html";
                    return;
                }
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({ error: `HTTP ${resp.status}` }));
                    throw new Error(err.error || `HTTP ${resp.status}`);
                }
                const disposition = resp.headers.get("Content-Disposition") || "";
                const match = disposition.match(/filename="?([^";]+)"?/);
                const url = URL.createObjectURL(await resp.blob());
                const a = document.createElement("a");
                a.href = url;
                a.download = match ? match[1] : "cortex-recovery-kit.json";
                a.click();
                URL.revokeObjectURL(url);
                Alpine.store("toast").success("Recovery kit downloaded. Keep the file and the passphrase in separate safe places.");
                this.kit = { password: "", passphrase: "", passphrase2: "" };
                await this.loadStatus();
            } catch (e) {
                Alpine.store("toast").error("Recovery kit: " + e.message);
            }
            this.exporting = false;
        },

        async onKitFile(event) {
            const file = event.target.files?.[0];
            this.restore.text = file ? await file.text() : "";
            this.restore.fileName = file ? file.name : "";
        },

        async restoreKit() {
            this.restoring = true;
            try {
                const r = await api.post(`${this.base}/recovery-kit/restore`, {
                    password: this.restore.password,
                    passphrase: this.restore.passphrase,
                    kit: this.restore.text,
                    replaceExistingKey: this.restore.replaceExisting,
                });
                Alpine.store("toast").success(r?.message || "Key restored");
                this.restore = { password: "", passphrase: "", text: "", fileName: "", replaceExisting: false };
                await this.loadStatus();
            } catch (e) {
                Alpine.store("toast").error("Restore: " + e.message);
            }
            this.restoring = false;
        },

        get canEnable() {
            return this.preview && !this.preview.alreadyEnabled && this.preview.blockers.length === 0
                && this.enable.password && this.enable.confirm === "ENCRYPT" && this.enable.snapshotTaken
                && (this.bridge?.recoveryKitExportedAt || this.enable.acceptNoKit);
        },

        async openEnable() {
            await this.loadPreview();
            this.enable = { password: "", confirm: "", erase: true, acceptNoKit: false, snapshotTaken: false };
            this.showEnableModal = true;
        },

        async confirmEnable() {
            this.enabling = true;
            try {
                const r = await api.post(`${this.base}/encryption/enable`, {
                    password: this.enable.password,
                    confirm: this.enable.confirm,
                    eraseEvaluationArtifacts: this.enable.erase,
                    acceptNoRecoveryKit: this.enable.acceptNoKit,
                });
                this.showEnableModal = false;
                this.restarting = !!r?.restartScheduled;
                Alpine.store("toast").success(r?.restartScheduled
                    ? "Encryption enabled; the agent restarts and encrypts its stores (see the status below)"
                    : "Encryption enabled; restart the agent to apply it");
            } catch (e) {
                const errors = e.body?.errors?.join(" ") || e.message;
                Alpine.store("toast").error("Not enabled: " + errors);
            }
            this.enabling = false;
            await this.loadStatus();
        },

        badge(state) {
            return state === "encrypted" || state === "open" || state === "accepted" ? "bg-success"
                : state === "plaintext" ? "bg-warning text-dark"
                : state === "mixed" || state === "awaitingKey" ? "bg-info text-dark"
                : state === "failed" || state === "rejected" ? "bg-danger"
                : "bg-secondary";
        },

        bytes(n) {
            if (!n) return "0 B";
            const units = ["B", "KB", "MB", "GB"];
            let i = 0;
            while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
            return `${n.toFixed(i ? 1 : 0)} ${units[i]}`;
        },

        fingerprint(fp) {
            return fp ? fp.match(/.{1,4}/g).join("-") : "—";
        },

        formatDateTime(ts) {
            return ts ? new Date(ts).toLocaleString() : "never";
        },
    };
}
