// Rules list: deletes a rule after confirming in an in-page dialog that also asks for the new version.
(() => {
    "use strict";

    const table = document.getElementById("rules-table");
    const dialog = document.getElementById("delete-dialog");
    if (!table || !dialog) return;

    const api = window.rulesApi;
    const form = document.getElementById("delete-form");
    const ruleName = dialog.querySelector("[data-rule-name]");
    const versionInput = document.getElementById("delete-version");
    const messages = document.getElementById("delete-messages");
    const confirmButton = document.getElementById("delete-confirm");
    const cancelButton = document.getElementById("delete-cancel");
    const antiforgeryToken = form.querySelector("input[name='__RequestVerificationToken']").value;

    let ruleId = null;
    let opener = null;
    let inFlight = false;

    table.addEventListener("click", (event) => {
        const button = event.target.closest("[data-delete-rule]");
        if (!button) return;
        ruleId = button.dataset.deleteRule;
        opener = button;
        ruleName.textContent = ruleId;
        api.clearMessages(messages);
        versionInput.removeAttribute("aria-invalid");
        dialog.showModal();
        versionInput.focus();
    });

    cancelButton.addEventListener("click", () => {
        if (!inFlight) dialog.close();
    });

    // Escape must not close the dialog while the delete is in flight.
    dialog.addEventListener("cancel", (event) => {
        if (inFlight) event.preventDefault();
    });

    dialog.addEventListener("close", () => {
        if (opener) opener.focus();
    });

    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        if (inFlight || !ruleId) return;

        api.clearMessages(messages);
        const version = versionInput.value.trim();
        if (version === "") {
            versionInput.setAttribute("aria-invalid", "true");
            api.showMessages(messages, "Enter a new rule set version.", []);
            versionInput.focus();
            return;
        }
        versionInput.removeAttribute("aria-invalid");

        setPending(true);
        try {
            const { response, body } = await api.send(
                "DELETE",
                `/api/rules/${encodeURIComponent(ruleId)}`,
                { etag: table.dataset.etag, version },
                antiforgeryToken);
            if (response.ok) {
                window.location.assign(`/rules?saved=${encodeURIComponent(body.version)}`);
                return;
            }
            setPending(false);
            const failure = api.describeFailure(response, body);
            api.showMessages(messages, failure.heading, failure.items);
        } catch (error) {
            setPending(false);
            api.showMessages(messages, api.describeNetworkError(error), []);
        }
    });

    function setPending(pending) {
        inFlight = pending;
        versionInput.disabled = pending;
        cancelButton.disabled = pending;
        form.setAttribute("aria-busy", String(pending));
        api.setButtonPending(confirmButton, pending, "Delete rule", "Deleting…");
    }
})();
