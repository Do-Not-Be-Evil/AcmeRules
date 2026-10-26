// Rule editor: builds a form for one rule from the rule file's field definitions and saves it
// through the rules API. The server re-validates the whole rule set before anything is written.
(() => {
    "use strict";

    const form = document.getElementById("rule-form");
    if (!form) return;

    const api = window.rulesApi;
    const originalId = form.dataset.ruleId || null;
    const antiforgeryToken = form.querySelector("input[name='__RequestVerificationToken']").value;

    const fieldset = document.getElementById("rule-fields");
    const loadStatus = document.getElementById("load-status");
    const messages = document.getElementById("form-messages");
    const saveButton = document.getElementById("save-button");
    const saveStatus = document.getElementById("save-status");
    const idInput = document.getElementById("rule-id");
    const kindSelect = document.getElementById("rule-kind");
    const priorityInput = document.getElementById("rule-priority");
    const rejectCode = document.getElementById("reject-code");
    const rejectMessage = document.getElementById("reject-message");
    const basePremium = document.getElementById("base-premium");
    const versionInput = document.getElementById("new-version");
    const eligibilitySection = document.getElementById("eligibility-section");
    const ratingSection = document.getElementById("rating-section");

    const OPS = [
        { value: "eq", label: "equals", numericOnly: false },
        { value: "neq", label: "does not equal", numericOnly: false },
        { value: "lt", label: "is less than", numericOnly: true },
        { value: "lte", label: "is at most", numericOnly: true },
        { value: "gt", label: "is greater than", numericOnly: true },
        { value: "gte", label: "is at least", numericOnly: true },
        { value: "in", label: "is one of", numericOnly: false },
    ];
    const NUMBER = /^-?\d+(\.\d+)?$/;

    let fields = [];
    let etag = null;
    let conditions = null;
    let factors = null;
    let inFlight = false;
    let nextId = 0;

    load();

    kindSelect.addEventListener("change", showKindSections);
    form.addEventListener("submit", (event) => {
        event.preventDefault();
        save();
    });

    async function load() {
        try {
            const { response, body } = await api.send("GET", "/api/rules");
            if (!response.ok) {
                const failure = api.describeFailure(response, body);
                loadStatus.textContent = "";
                api.showMessages(messages, failure.heading, failure.items);
                return;
            }

            fields = body.fields || [];
            etag = body.etag;
            document.querySelector("[data-current-version]").textContent = body.version;

            let rule = { kind: "eligibility", priority: 0, when: [] };
            if (originalId !== null) {
                rule = (body.rules || []).find((r) => r.id === originalId);
                if (!rule) {
                    loadStatus.textContent = "";
                    api.showMessages(messages, `Rule '${originalId}' no longer exists.`, []);
                    return;
                }
            }

            fill(rule);
            fieldset.disabled = false;
            saveButton.disabled = false;
            form.setAttribute("aria-busy", "false");
            loadStatus.textContent = "";
            loadStatus.hidden = true;
        } catch (error) {
            loadStatus.textContent = "";
            api.showMessages(messages, api.describeNetworkError(error), []);
        }
    }

    function fill(rule) {
        idInput.value = rule.id || "";
        kindSelect.value = rule.kind === "rating" ? "rating" : "eligibility";
        priorityInput.value = rule.priority ?? 0;
        rejectCode.value = (rule.reject && rule.reject.code) || "";
        rejectMessage.value = (rule.reject && rule.reject.message) || "";
        basePremium.value = rule.basePremium ?? "";
        conditions = conditionList(
            document.getElementById("rule-conditions"),
            document.getElementById("add-condition"),
            rule.when || []);
        factors = factorList(
            document.getElementById("rule-factors"),
            document.getElementById("add-factor"),
            rule.factors || []);
        showKindSections();
    }

    function showKindSections() {
        const rating = kindSelect.value === "rating";
        ratingSection.hidden = !rating;
        eligibilitySection.hidden = rating;
    }

    // ---- Conditions ---------------------------------------------------------------------------

    function conditionList(list, addButton, initial) {
        const rows = [];

        function add(condition, focus) {
            const row = conditionRow(condition, () => {
                rows.splice(rows.indexOf(row), 1);
                row.element.remove();
                addButton.focus();
            });
            rows.push(row);
            list.append(row.element);
            if (focus) row.focus();
        }

        for (const condition of initial) add(condition, false);
        addButton.addEventListener("click", () => add(null, true));

        return {
            collect: (errors) => rows.map((row) => row.collect(errors)),
        };
    }

    function conditionRow(condition, onRemove) {
        const element = document.createElement("li");
        element.className = "row";

        const fieldSelect = select(fields.map((f) => [f.name, f.label || f.name]));
        const opSelect = select([]);
        const valueSlot = document.createElement("div");
        valueSlot.className = "field value-slot";

        const remove = button("Remove", "link-button danger");
        remove.addEventListener("click", onRemove);

        element.append(
            labelled("Field", fieldSelect),
            labelled("Operator", opSelect),
            valueSlot,
            actions(remove));

        let value = null;

        const initialField = condition && fields.some((f) => f.name === condition.field) ? condition.field : fields[0]?.name;
        fieldSelect.value = initialField || "";
        refreshOps(condition ? condition.op : "eq");
        renderValue(condition ? condition.value : undefined);

        fieldSelect.addEventListener("change", () => {
            refreshOps(opSelect.value);
            renderValue(undefined);
        });
        opSelect.addEventListener("change", () => renderValue(undefined));

        function currentField() {
            return fields.find((f) => f.name === fieldSelect.value);
        }

        function refreshOps(preferred) {
            const numeric = isNumeric(currentField());
            const allowed = OPS.filter((op) => numeric || !op.numericOnly);
            opSelect.replaceChildren(...allowed.map((op) => option(op.value, op.label)));
            opSelect.value = allowed.some((op) => op.value === preferred) ? preferred : "eq";
        }

        function renderValue(initial) {
            value = valueControl(currentField(), opSelect.value, initial);
            valueSlot.replaceChildren(...value.nodes);
        }

        return {
            element,
            focus: () => fieldSelect.focus(),
            collect(errors) {
                return { field: fieldSelect.value, op: opSelect.value, value: value.read(errors) };
            },
        };
    }

    /** The value input for a condition, chosen by field type and operator. */
    function valueControl(field, op, initial) {
        const many = op === "in";
        const initialValues = initial === undefined ? [] : [].concat(initial);

        if (field && field.type === "enum") {
            const labels = field.valueLabels || {};
            if (many) {
                const group = document.createElement("fieldset");
                group.className = "checks";
                const legend = document.createElement("legend");
                legend.textContent = "Values";
                group.append(legend);
                const boxes = (field.allowedValues || []).map((v) => {
                    const box = document.createElement("input");
                    box.type = "checkbox";
                    box.value = v;
                    box.id = uid("check");
                    box.checked = initialValues.includes(v);
                    const label = document.createElement("label");
                    label.htmlFor = box.id;
                    label.textContent = labels[v] || v;
                    const wrap = document.createElement("span");
                    wrap.className = "check";
                    wrap.append(box, label);
                    group.append(wrap);
                    return box;
                });
                return {
                    nodes: [group],
                    read(errors) {
                        const chosen = boxes.filter((b) => b.checked).map((b) => b.value);
                        if (chosen.length === 0) errors.push({ element: boxes[0] || group, message: `Choose at least one ${field.label || field.name} value.` });
                        return chosen;
                    },
                };
            }
            const input = select((field.allowedValues || []).map((v) => [v, labels[v] || v]));
            if (initialValues.length > 0) input.value = String(initialValues[0]);
            return { nodes: [...labelled("Value", input).childNodes], read: () => input.value };
        }

        const numeric = isNumeric(field);
        const input = document.createElement("input");
        input.type = "text";
        input.autocomplete = "off";
        input.id = uid("value");
        if (numeric) input.inputMode = "decimal";
        input.value = initialValues.map(String).join(", ");

        const label = document.createElement("label");
        label.htmlFor = input.id;
        label.textContent = many ? (numeric ? "Values (comma-separated numbers)" : "Values (comma-separated)") : "Value";

        return {
            nodes: [label, input],
            read(errors) {
                const parts = many ? input.value.split(",").map((s) => s.trim()).filter((s) => s !== "") : [input.value.trim()];
                if (parts.length === 0 || parts[0] === "") {
                    errors.push({ element: input, message: `Enter a value for ${field ? field.label || field.name : "the condition"}.` });
                    return many ? [] : "";
                }
                if (numeric) {
                    const bad = parts.find((p) => !NUMBER.test(p));
                    if (bad !== undefined) {
                        errors.push({ element: input, message: `'${bad}' is not a number.` });
                        return many ? [] : 0;
                    }
                    const numbers = parts.map(Number);
                    return many ? numbers : numbers[0];
                }
                return many ? parts : parts[0];
            },
        };
    }

    // ---- Factors ------------------------------------------------------------------------------

    function factorList(list, addButton, initial) {
        const rows = [];

        function add(factor, focus) {
            const row = factorRow(factor, {
                remove() {
                    rows.splice(rows.indexOf(row), 1);
                    row.element.remove();
                    renumber();
                    addButton.focus();
                },
                move(delta) {
                    const from = rows.indexOf(row);
                    const to = from + delta;
                    if (to < 0 || to >= rows.length) return;
                    rows.splice(from, 1);
                    rows.splice(to, 0, row);
                    list.replaceChildren(...rows.map((r) => r.element));
                    renumber();
                    row.focusMove(delta);
                },
            });
            rows.push(row);
            list.append(row.element);
            renumber();
            if (focus) row.focus();
        }

        function renumber() {
            rows.forEach((row, i) => row.setPosition(i, rows.length));
        }

        for (const factor of initial) add(factor, false);
        addButton.addEventListener("click", () => add(null, true));

        return {
            collect: (errors) => rows.map((row) => row.collect(errors)),
        };
    }

    function factorRow(factor, controls) {
        const element = document.createElement("li");
        element.className = "row factor";

        const heading = document.createElement("h4");
        const opSelect = select([["multiply", "Multiply by"], ["add", "Add"]]);
        opSelect.value = factor && factor.op === "add" ? "add" : "multiply";

        const valueInput = document.createElement("input");
        valueInput.type = "text";
        valueInput.inputMode = "decimal";
        valueInput.autocomplete = "off";
        valueInput.value = factor && factor.value !== undefined ? String(factor.value) : "";

        const numericFields = fields.filter(isNumeric);
        const perFieldSelect = select([["", "(fixed amount)"], ...numericFields.map((f) => [f.name, `× ${f.label || f.name}`])]);
        perFieldSelect.value = (factor && factor.perField) || "";
        const perFieldWrap = labelled("Per", perFieldSelect);

        const up = button("Move up", "link-button");
        const down = button("Move down", "link-button");
        const remove = button("Remove factor", "link-button danger");
        up.addEventListener("click", () => controls.move(-1));
        down.addEventListener("click", () => controls.move(1));
        remove.addEventListener("click", controls.remove);

        const conditionsList = document.createElement("ol");
        conditionsList.className = "row-list nested";
        const addCondition = button("Add condition", "");

        const whenHeading = document.createElement("p");
        whenHeading.className = "help";
        whenHeading.textContent = "Applies only when (leave empty to always apply):";

        const inputs = document.createElement("div");
        inputs.className = "row-inputs";
        inputs.append(labelled("Operation", opSelect), labelled("Value", valueInput), perFieldWrap);

        element.append(heading, inputs, whenHeading, conditionsList, actions(addCondition), actions(up, down, remove));

        const when = conditionList(conditionsList, addCondition, (factor && factor.when) || []);

        function showPerField() {
            perFieldWrap.hidden = opSelect.value !== "add";
        }
        opSelect.addEventListener("change", showPerField);
        showPerField();

        return {
            element,
            focus: () => opSelect.focus(),
            focusMove(delta) {
                (delta < 0 && !up.disabled ? up : !down.disabled ? down : up).focus();
            },
            setPosition(index, count) {
                heading.textContent = `Factor ${index + 1}`;
                up.disabled = index === 0;
                down.disabled = index === count - 1;
            },
            collect(errors) {
                const text = valueInput.value.trim();
                if (!NUMBER.test(text)) errors.push({ element: valueInput, message: `${heading.textContent}: enter a number.` });
                const result = { op: opSelect.value, value: NUMBER.test(text) ? Number(text) : 0 };
                if (opSelect.value === "add" && perFieldSelect.value) result.perField = perFieldSelect.value;
                const conditions = when.collect(errors);
                if (conditions.length > 0) result.when = conditions;
                return result;
            },
        };
    }

    // ---- Save ---------------------------------------------------------------------------------

    function collect() {
        const errors = [];
        for (const input of form.querySelectorAll("[aria-invalid]")) input.removeAttribute("aria-invalid");

        const id = idInput.value.trim();
        if (id === "") errors.push({ element: idInput, message: "Enter a rule id." });

        const priorityText = priorityInput.value.trim();
        if (!/^-?\d+$/.test(priorityText)) errors.push({ element: priorityInput, message: "Priority must be a whole number." });

        const rule = { id, kind: kindSelect.value, priority: Number(priorityText) || 0 };
        const when = conditions.collect(errors);
        if (when.length > 0) rule.when = when;

        if (rule.kind === "eligibility") {
            rule.reject = { code: rejectCode.value.trim(), message: rejectMessage.value.trim() };
            if (rule.reject.code === "") errors.push({ element: rejectCode, message: "Enter a rejection code." });
            if (rule.reject.message === "") errors.push({ element: rejectMessage, message: "Enter a rejection message." });
        } else {
            const text = basePremium.value.trim();
            if (!NUMBER.test(text)) errors.push({ element: basePremium, message: "Base premium must be a number." });
            rule.basePremium = Number(text) || 0;
            const factorList = factors.collect(errors);
            if (factorList.length > 0) rule.factors = factorList;
        }

        const version = versionInput.value.trim();
        if (version === "") errors.push({ element: versionInput, message: "Enter a new rule set version." });

        return { rule, version, errors };
    }

    async function save() {
        if (inFlight) return;

        api.clearMessages(messages);
        saveStatus.textContent = "";
        const { rule, version, errors } = collect();
        if (errors.length > 0) {
            for (const error of errors) error.element.setAttribute("aria-invalid", "true");
            api.showMessages(messages, "Fix these problems and save again.", errors.map((e) => e.message));
            errors[0].element.focus();
            return;
        }

        setPending(true);
        try {
            const { response, body } = originalId === null
                ? await api.send("POST", "/api/rules", { etag, version, rule }, antiforgeryToken)
                : await api.send("PUT", `/api/rules/${encodeURIComponent(originalId)}`, { etag, version, rule }, antiforgeryToken);
            if (response.ok) {
                window.location.assign(`/rules?saved=${encodeURIComponent(body.version)}`);
                return;
            }
            setPending(false);
            const failure = api.describeFailure(response, body);
            api.showMessages(messages, failure.heading, failure.items);
            saveStatus.textContent = "Not saved. Your changes are still on this page.";
        } catch (error) {
            setPending(false);
            api.showMessages(messages, api.describeNetworkError(error), []);
            saveStatus.textContent = "Not saved. Your changes are still on this page.";
        }
    }

    function setPending(pending) {
        inFlight = pending;
        fieldset.disabled = pending;
        form.setAttribute("aria-busy", String(pending));
        api.setButtonPending(saveButton, pending, "Save rule", "Saving…");
        if (pending) saveStatus.textContent = "Saving…";
    }

    // ---- DOM helpers --------------------------------------------------------------------------

    function isNumeric(field) {
        return !!field && (field.type === "number" || field.type === "integer");
    }

    function uid(prefix) {
        nextId += 1;
        return `${prefix}-${nextId}`;
    }

    function option(value, text) {
        const o = document.createElement("option");
        o.value = value;
        o.textContent = text;
        return o;
    }

    function select(pairs) {
        const s = document.createElement("select");
        s.id = uid("select");
        s.append(...pairs.map(([value, text]) => option(value, text)));
        return s;
    }

    function button(text, className) {
        const b = document.createElement("button");
        b.type = "button";
        b.textContent = text;
        if (className) b.className = className;
        return b;
    }

    function labelled(text, control) {
        if (!control.id) control.id = uid("control");
        const wrap = document.createElement("div");
        wrap.className = "field";
        const label = document.createElement("label");
        label.htmlFor = control.id;
        label.textContent = text;
        wrap.append(label, control);
        return wrap;
    }

    function actions(...buttons) {
        const wrap = document.createElement("div");
        wrap.className = "row-actions";
        wrap.append(...buttons);
        return wrap;
    }
})();
