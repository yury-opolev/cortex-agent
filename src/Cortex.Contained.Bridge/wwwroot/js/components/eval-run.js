"use strict";

/**
 * "Run evaluation" on the Recall page. One button starts the automated evaluation (labelling, κ, dev ablations,
 * pre-registration, holdout, sanitised copy, comparison, export); the owner only enters the Bridge password, grades
 * the κ sample, approves dictionary terms and gives the package passphrase. Text from the data (queries, candidates,
 * suggested terms) is rendered with x-text only, fetched one item at a time and never cached or logged.
 */
const evalRunPhaseLabels = {
    "prepare": "Prepare workspaces",
    "label-history": "Write and judge history queries",
    "label-memory": "Write and judge memory queries",
    "validate": "Validate datasets",
    "guard": "Paraphrase guard",
    "grading": "Your κ grading",
    "kappa": "LLM-vs-human κ",
    "plan": "Pre-registered analysis plan",
    "ablate-dev": "Dev ablations",
    "select": "Choose configurations (dev only)",
    "rerank-curve": "Rerank curve (dev)",
    "select-rerank": "Choose rerank budget",
    "register": "Register (pre-registration)",
    "holdout-raw": "Holdout on raw data (Gate H)",
    "gate-r": "Gate R",
    "suggest": "Suggest dictionary terms",
    "dictionary": "Your dictionary approval",
    "freeze": "Freeze sanitised copy",
    "holdout-frozen": "Holdout on the sanitised copy",
    "compare": "Raw vs sanitised comparison",
    "passphrase": "Your package passphrase",
    "export": "Export package",
    "summary": "Summary",
};

const evalRunKinds = ["person", "place", "org", "handle", "other", "public-host"];

function evalRunPanel() {
    return {
        get tenantId() { return Alpine.store("router").tenantId; },
        get base() { return `/api/tenants/${encodeURIComponent(this.tenantId)}/eval`; },

        run: null,
        error: "",
        busy: false,
        timer: null,
        kinds: evalRunKinds,
        start: { password: "", fresh: false },
        resume: { password: "" },
        grading: { password: "", grant: "", item: null, busy: false, error: "" },
        dict: { password: "", suggestions: [], extra: [], draft: { term: "", kind: "person", entity: "" }, loaded: false, busy: false },
        exp: { password: "", passphrase: "", confirm: "" },
        summary: null,
        report: { key: "", text: "" },

        async init() {
            await this.load();
            this.timer = setInterval(() => this.load(), 5000);
        },

        destroy() {
            if (this.timer) clearInterval(this.timer);
            this.forget();
        },

        async load() {
            try {
                this.run = await api.get(`${this.base}/run`);
                this.error = "";
                if (this.run?.reports?.includes("summary") && !this.summary) await this.loadSummary();
            } catch (e) {
                this.error = e.message;
            }
        },

        phaseLabel(name) { return evalRunPhaseLabels[name] || name; },

        phaseIcon(state) {
            return state === "done" ? "bi-check-circle-fill text-success"
                : state === "current" ? "bi-arrow-right-circle-fill text-primary"
                : state === "skipped" ? "bi-dash-circle text-secondary"
                : "bi-circle text-secondary";
        },

        get active() { return this.run?.status === "running" || this.run?.status === "waiting"; },

        async control(action, body, done) {
            this.busy = true;
            try {
                const result = await api.post(`${this.base}/run/${action}`, body);
                this.run = result.run;
                if (done) Alpine.store("toast").success(done);
                return true;
            } catch (e) {
                if (e.body?.run) this.run = e.body.run;
                Alpine.store("toast").error(e.body?.error || e.message);
                return false;
            } finally {
                this.busy = false;
            }
        },

        async startRun() {
            if (this.start.fresh && !confirm("Delete the previous evaluation's datasets, registrations and runs (frozen versions and the dictionary stay)?")) return;
            this.summary = null;
            await this.control("start", { password: this.start.password, fresh: this.start.fresh }, "Evaluation started");
            this.start.password = "";
        },

        async cancelRun() {
            if (!confirm("Cancel the evaluation run? Its progress is kept; Resume continues it.")) return;
            await this.control("cancel", {}, "Evaluation cancelled");
        },

        async resumeRun() {
            await this.control("resume", { password: this.resume.password }, "Evaluation resumed");
            this.resume.password = "";
        },

        // ── κ grading ───────────────────────────────────────────
        async gradingCall(path, body) {
            const resp = await fetch(`${this.base}/grading/${path}`, {
                method: "POST",
                headers: { "Content-Type": "application/json", "X-Grading-Grant": this.grading.grant },
                body: JSON.stringify(body ?? {}),
                cache: "no-store",
            });
            const data = await resp.json().catch(() => ({}));
            if (!resp.ok) {
                if (data.grantExpired) this.grading.grant = "";
                throw new Error(data.error || `HTTP ${resp.status}`);
            }
            return data;
        },

        async openGrading() {
            this.grading.busy = true;
            this.grading.error = "";
            try {
                const session = await api.post(`${this.base}/grading/session`, { password: this.grading.password });
                this.grading.grant = session.grant;
                this.grading.password = "";
                await this.showItem(Math.min(this.run?.grading?.graded ?? 0, Math.max(0, (this.run?.grading?.total ?? 1) - 1)));
            } catch (e) {
                this.grading.error = e.body?.error || e.message;
            }
            this.grading.busy = false;
        },

        async showItem(index) {
            if (index < 0) return;
            this.grading.busy = true;
            try {
                const item = await this.gradingCall(`items/${index}`);
                this.grading.item = item.exists ? item : null;
                this.grading.error = "";
            } catch (e) {
                this.grading.item = null;
                this.grading.error = e.message;
            }
            this.grading.busy = false;
        },

        async grade(value) {
            const item = this.grading.item;
            if (!item || this.grading.busy) return;
            this.grading.busy = true;
            try {
                const progress = await this.gradingCall(`items/${item.index}/grade`, { grade: value });
                if (this.run) this.run.grading = progress;
                this.grading.busy = false;
                if (progress.complete) {
                    this.closeGrading();
                    Alpine.store("toast").success("Grading complete: the run continues with κ");
                    await this.load();
                } else {
                    await this.showItem(item.index + 1 < item.total ? item.index + 1 : 0);
                }
            } catch (e) {
                this.grading.error = e.message;
                this.grading.busy = false;
            }
        },

        closeGrading() {
            this.grading.item = null;
            this.grading.grant = "";
        },

        onKey(event) {
            const item = this.grading.item;
            if (!item || ["INPUT", "TEXTAREA", "SELECT"].includes(event.target?.tagName)) return;
            if (["0", "1", "2"].includes(event.key)) { event.preventDefault(); this.grade(Number(event.key)); }
            else if (event.key === "ArrowRight" || event.key === "s") { event.preventDefault(); this.showItem((item.index + 1) % item.total); }
            else if (event.key === "ArrowLeft" || event.key === "b") { event.preventDefault(); this.showItem(item.index - 1); }
            else if (event.key === "Escape") { this.closeGrading(); }
        },

        // ── Dictionary ──────────────────────────────────────────
        async loadSuggestions() {
            this.dict.busy = true;
            try {
                const file = await api.post(`${this.base}/files/read`, { password: this.dict.password, tool: "eval-corpus", name: "dictionary.suggested.jsonl" });
                this.dict.suggestions = (file.content || "").split("\n").filter((l) => l.trim()).map((l) => {
                    const s = JSON.parse(l);
                    return { term: s.term, kind: evalRunKinds.includes(s.kind) ? s.kind : "person", count: s.count, source: s.source, accept: false, entity: "" };
                });
                this.dict.loaded = true;
            } catch (e) {
                Alpine.store("toast").error("Suggestions: " + (e.body?.error || e.message));
            }
            this.dict.busy = false;
        },

        addTerm() {
            const term = this.dict.draft.term.trim();
            if (!term) return;
            this.dict.extra.push({ term, kind: this.dict.draft.kind, entity: this.dict.draft.entity.trim() });
            this.dict.draft = { term: "", kind: this.dict.draft.kind, entity: "" };
        },

        get acceptedTerms() {
            return this.dict.suggestions.filter((s) => s.accept).concat(this.dict.extra)
                .map((s) => ({ term: s.term, kind: s.kind, entity: s.entity || null }));
        },

        async approveDictionary() {
            const ok = await this.control("dictionary", { password: this.dict.password, terms: this.acceptedTerms },
                `${this.acceptedTerms.length} terms added; freezing the sanitised copy`);
            if (ok) this.dict = { password: "", suggestions: [], extra: [], draft: { term: "", kind: "person", entity: "" }, loaded: false, busy: false };
        },

        // ── Export ──────────────────────────────────────────────
        get passphraseProblem() {
            if (this.exp.passphrase.length < 12) return "At least 12 characters.";
            if (this.exp.passphrase !== this.exp.confirm) return "The two entries differ.";
            return "";
        },

        async exportPackage() {
            if (this.passphraseProblem) return;
            await this.control("export", { password: this.exp.password, passphrase: this.exp.passphrase }, "Exporting the package");
            this.exp = { password: "", passphrase: "", confirm: "" };
        },

        // ── Results ─────────────────────────────────────────────
        async fetchReport(key) {
            const resp = await fetch(`${this.base}/run/reports/${encodeURIComponent(key)}`, { cache: "no-store" });
            if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
            return resp.text();
        },

        async loadSummary() {
            try {
                this.summary = JSON.parse(await this.fetchReport("summary"));
            } catch {
                this.summary = null;
            }
        },

        async openReport(key) {
            try {
                this.report = { key, text: await this.fetchReport(key) };
            } catch (e) {
                Alpine.store("toast").error("Report: " + e.message);
            }
        },

        delta(v) {
            if (!v) return "—";
            const f = (x) => (x >= 0 ? "+" : "") + Number(x).toFixed(3);
            return `${f(v.mean)} [${f(v.low)}, ${f(v.high)}]`;
        },

        difference(raw, sanitised) {
            return raw && sanitised ? ((raw.mean - sanitised.mean >= 0 ? "+" : "") + (raw.mean - sanitised.mean).toFixed(3)) : "—";
        },

        number(x, digits = 3) { return x === null || x === undefined ? "—" : Number(x).toFixed(digits); },

        forget() {
            this.start.password = "";
            this.resume.password = "";
            this.closeGrading();
            this.dict.password = "";
            this.exp = { password: "", passphrase: "", confirm: "" };
        },
    };
}
