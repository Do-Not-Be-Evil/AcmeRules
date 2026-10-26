// Quote form: submits inputs to the quote API and renders the quote or errors.
// Client-side checks here are for usability only; the server validates and authorizes every request.
(() => {
    "use strict";

    const form = document.getElementById("quote-form");
    if (!form) return;

    const fieldset = document.getElementById("quote-fields");
    const button = document.getElementById("generate-button");
    const spinner = button.querySelector(".spinner");
    const buttonLabel = button.querySelector(".button-label");
    const status = document.getElementById("quote-status");
    const formMessages = document.getElementById("form-messages");
    const failure = document.getElementById("quote-failure");
    const failureMessage = failure.querySelector(".failure-message");
    const retryButton = document.getElementById("retry-button");
    const result = document.getElementById("quote-result");
    const inputs = Array.from(form.querySelectorAll("[data-field]"));
    const antiforgeryToken = form.querySelector("input[name='__RequestVerificationToken']").value;
    const timeoutMs = Number(form.dataset.timeoutMs) || 15000;

    let inFlight = false;

    form.addEventListener("submit", (event) => {
        event.preventDefault();
        generate();
    });
    retryButton.addEventListener("click", generate);

    // A displayed quote must always match the displayed inputs.
    for (const input of inputs) {
        input.addEventListener("input", () => {
            clearFieldError(input);
            if (!result.hidden) {
                result.hidden = true;
                status.textContent = "Inputs changed. Generate again for an updated quote.";
            }
        });
    }

    async function generate() {
        if (inFlight) return;

        clearFeedback();
        const payload = collectInputs();
        if (!payload) return;

        setPending(true);
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), timeoutMs);
        try {
            const response = await fetch(form.action, {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    "Content-Type": "application/json",
                    "Accept": "application/json",
                    "X-CSRF-TOKEN": antiforgeryToken,
                },
                body: JSON.stringify(payload),
                signal: controller.signal,
            });
            const body = await readJson(response);
            // Leave the pending state before rendering so focus can move to re-enabled controls.
            setPending(false);
            render(response, body);
        } catch (error) {
            setPending(false);
            showFailure(error.name === "AbortError"
                ? "Quote generation timed out. Your inputs are kept; try again."
                : "Could not reach the quote service. Check your connection and try again.");
        } finally {
            clearTimeout(timer);
            if (inFlight) setPending(false);
        }
    }

    function collectInputs() {
        const payload = {};
        const badInputs = [];
        for (const input of inputs) {
            if (input.dataset.type === "number") {
                // Browsers report unparsable number input as an empty value; catch it here rather than send "missing".
                if (input.validity.badInput) {
                    badInputs.push(input);
                    continue;
                }
                if (input.value.trim() !== "") payload[input.dataset.field] = Number(input.value);
            } else if (input.value.trim() !== "") {
                payload[input.dataset.field] = input.value.trim();
            }
        }
        if (badInputs.length > 0) {
            for (const input of badInputs) showFieldError(input, "Enter a valid number.");
            badInputs[0].focus();
            return null;
        }
        return payload;
    }

    function render(response, body) {
        if (response.ok && body && typeof body.premium === "number") {
            showQuote(body);
            return;
        }

        switch (response.status) {
            case 400:
                if (body && body.errors) {
                    showFieldErrors(body.errors);
                } else {
                    showMessages([(body && body.title) || "The request was not valid."]);
                }
                return;
            case 401:
                showMessages(["Your session has expired. Reload the page to sign in again."]);
                return;
            case 403:
                showMessages(["You do not have permission to generate quotes."]);
                return;
            case 409:
            case 422: {
                const items = body && Array.isArray(body.violations) && body.violations.length > 0
                    ? body.violations.map((v) => v.message)
                    : [(body && body.title) || "These inputs cannot be quoted."];
                showMessages(items, body && body.title);
                return;
            }
            case 429: {
                const seconds = Number(response.headers.get("Retry-After"));
                showFailure(seconds > 0
                    ? `Too many quote requests. Try again in ${seconds} seconds.`
                    : "Too many quote requests. Wait a moment and try again.");
                return;
            }
            case 503:
                showFailure("Quote generation is currently unavailable. Try again later.");
                return;
            default:
                showFailure("Quote generation failed. Your inputs are kept; try again.");
        }
    }

    async function readJson(response) {
        try {
            return await response.json();
        } catch {
            return null;
        }
    }

    function setPending(pending) {
        inFlight = pending;
        button.disabled = pending;
        retryButton.disabled = pending;
        // Lock inputs so the result always corresponds to what was submitted; values are kept.
        fieldset.disabled = pending;
        form.setAttribute("aria-busy", String(pending));
        spinner.hidden = !pending;
        buttonLabel.textContent = pending ? "Generating…" : "Generate quote";
        if (pending) status.textContent = "Generating quote…";
        else if (status.textContent === "Generating quote…") status.textContent = "";
    }

    function clearFeedback() {
        result.hidden = true;
        failure.hidden = true;
        formMessages.hidden = true;
        formMessages.replaceChildren();
        status.textContent = "";
        for (const input of inputs) clearFieldError(input);
    }

    function showQuote(quote) {
        result.querySelector("[data-premium]").textContent = formatMoney(quote.premium, quote.currency);
        result.querySelector("[data-version]").textContent = quote.ruleSetVersion;
        result.querySelector("[data-rules]").textContent = (quote.appliedRuleIds || []).join(", ");
        result.hidden = false;
        status.textContent = "Quote generated.";
        result.focus();
    }

    function showFieldErrors(errors) {
        const general = [];
        let first = null;
        for (const [name, messages] of Object.entries(errors)) {
            const input = inputs.find((i) => i.dataset.field === name);
            const text = [].concat(messages).join(" ");
            if (input) {
                showFieldError(input, text);
                first = first || input;
            } else {
                general.push(`${name}: ${text}`);
            }
        }
        if (general.length > 0) showMessages(general);
        status.textContent = "Fix the highlighted fields and generate again.";
        if (first) first.focus();
    }

    function showFieldError(input, message) {
        const error = document.getElementById(`${input.id}-error`);
        error.textContent = message;
        error.hidden = false;
        input.setAttribute("aria-invalid", "true");
    }

    function clearFieldError(input) {
        const error = document.getElementById(`${input.id}-error`);
        error.textContent = "";
        error.hidden = true;
        input.removeAttribute("aria-invalid");
    }

    function showMessages(messages, heading) {
        const nodes = [];
        if (heading) {
            const p = document.createElement("p");
            p.textContent = heading;
            nodes.push(p);
        }
        const list = document.createElement("ul");
        for (const message of messages) {
            const li = document.createElement("li");
            li.textContent = message;
            list.append(li);
        }
        nodes.push(list);
        formMessages.replaceChildren(...nodes);
        formMessages.hidden = false;
        formMessages.focus();
    }

    function showFailure(message) {
        failureMessage.textContent = message;
        failure.hidden = false;
        retryButton.focus();
    }

    function formatMoney(amount, currency) {
        try {
            return new Intl.NumberFormat(undefined, { style: "currency", currency }).format(amount);
        } catch {
            return `${amount} ${currency}`;
        }
    }
})();
