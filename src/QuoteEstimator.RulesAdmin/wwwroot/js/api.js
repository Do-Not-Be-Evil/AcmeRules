// Shared helpers for calling the rules API. The server validates and authorizes every change;
// nothing here is a security boundary.
window.rulesApi = (() => {
    "use strict";

    const timeoutMs = 15000;

    /** Sends a request and resolves to { response, body }. Rejects only on network failure or timeout. */
    async function send(method, url, payload, antiforgeryToken) {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), timeoutMs);
        try {
            const headers = { "Accept": "application/json" };
            if (payload !== undefined) headers["Content-Type"] = "application/json";
            if (antiforgeryToken) headers["X-CSRF-TOKEN"] = antiforgeryToken;
            const response = await fetch(url, {
                method,
                credentials: "same-origin",
                headers,
                body: payload === undefined ? undefined : JSON.stringify(payload),
                signal: controller.signal,
            });
            return { response, body: await readJson(response) };
        } finally {
            clearTimeout(timer);
        }
    }

    async function readJson(response) {
        try {
            return await response.json();
        } catch {
            return null;
        }
    }

    /** Turns a failed response into a heading and a list of messages to show. */
    function describeFailure(response, body) {
        const title = body && body.title;
        const errors = body && Array.isArray(body.errors) ? body.errors : [];
        switch (response.status) {
            case 401:
                return { heading: "Your session has expired. Reload the page to sign in again.", items: [] };
            case 403:
                return { heading: "You do not have permission to change rules.", items: [] };
            case 409:
                return body && body.code === "STALE"
                    ? { heading: title, items: ["Reload the page to get the latest rules, then make your change again."], stale: true }
                    : { heading: title || "That change conflicts with the current rules.", items: [] };
            case 400:
            case 404:
            case 422:
            case 503:
                return { heading: title || "The change was not saved.", items: errors };
            default:
                return { heading: "The change could not be saved. Try again.", items: [] };
        }
    }

    function describeNetworkError(error) {
        return error && error.name === "AbortError"
            ? "The request timed out. Nothing on this page was lost; try again."
            : "Could not reach the server. Check your connection and try again.";
    }

    /** Renders a heading and message list into an alert element. */
    function showMessages(container, heading, items) {
        const nodes = [];
        if (heading) {
            const p = document.createElement("p");
            p.textContent = heading;
            nodes.push(p);
        }
        if (items && items.length > 0) {
            const list = document.createElement("ul");
            for (const item of items) {
                const li = document.createElement("li");
                li.textContent = item;
                list.append(li);
            }
            nodes.push(list);
        }
        container.replaceChildren(...nodes);
        container.hidden = false;
        container.focus();
    }

    function clearMessages(container) {
        container.replaceChildren();
        container.hidden = true;
    }

    function setButtonPending(button, pending, idleLabel, pendingLabel) {
        button.disabled = pending;
        button.querySelector(".spinner").hidden = !pending;
        button.querySelector(".button-label").textContent = pending ? pendingLabel : idleLabel;
    }

    return { send, describeFailure, describeNetworkError, showMessages, clearMessages, setButtonPending };
})();
