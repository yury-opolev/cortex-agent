"use strict";

/**
 * In-page confirmation dialog (replaces the browser's native confirm()).
 * Usage: if (!(await Alpine.store("confirm").ask({ title, message, confirmText, danger }))) return;
 * Resolves true on confirm, false on cancel, Esc or a backdrop click. A second ask() while one is open resolves the first with false.
 */
document.addEventListener("alpine:init", () => {
    Alpine.store("confirm", {
        open: false,
        title: "",
        message: "",
        confirmText: "OK",
        cancelText: "Cancel",
        danger: false,
        _resolve: null,

        ask({ title = "Please confirm", message = "", confirmText = "OK", cancelText = "Cancel", danger = false } = {}) {
            this._settle(false);
            Object.assign(this, { title, message, confirmText, cancelText, danger, open: true });
            return new Promise((resolve) => { this._resolve = resolve; });
        },

        accept() { this._settle(true); },
        cancel() { this._settle(false); },

        _settle(value) {
            const resolve = this._resolve;
            this._resolve = null;
            this.open = false;
            if (resolve) resolve(value);
        },
    });
});
