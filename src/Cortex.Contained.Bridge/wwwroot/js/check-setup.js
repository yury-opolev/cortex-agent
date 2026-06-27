// First-run setup redirect: if no providers configured, redirect to setup wizard.
(async function checkSetup() {
    try {
        const resp = await fetch("/api/setup/status");
        if (resp.ok) {
            const data = await resp.json();
            if (data.needsSetup) {
                window.location.href = "/setup.html";
            }
        }
    } catch (_) { /* setup API not available, continue normally */ }
})();
