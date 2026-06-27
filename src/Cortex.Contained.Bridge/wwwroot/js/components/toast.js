"use strict";

/**
 * Unified toast notification component.
 * Usage: Alpine.store('toast').show('Message', 'success')
 * Types: success, error, warning, info
 */
document.addEventListener("alpine:init", () => {
    Alpine.store("toast", {
        visible: false,
        message: "",
        type: "info",
        _timeout: null,

        show(message, type = "info", durationMs = 4000) {
            this.message = message;
            this.type = type;
            this.visible = true;
            clearTimeout(this._timeout);
            this._timeout = setTimeout(() => { this.visible = false; }, durationMs);
        },

        success(msg) { this.show(msg, "success"); },
        error(msg)   { this.show(msg, "error", 6000); },
        warning(msg) { this.show(msg, "warning", 5000); },
        info(msg)    { this.show(msg, "info"); },
    });
});
