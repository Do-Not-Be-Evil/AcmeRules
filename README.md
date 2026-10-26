# Quote Estimator

Applies configurable quote-generation rules when insurance estimators generate quotes (see [feature_summary.md](feature_summary.md)).

ASP.NET Core MVC on .NET 10 with vanilla JavaScript. No front-end framework and no extra server packages.

> **The rules, fields, rates, rounding mode, precedence policy, messages and fixtures are PLACEHOLDERS.**
> They are not approved for production pricing. See [Before release](#before-release).

## Run

```sh
dotnet run --project src/QuoteEstimator.Web --launch-profile http
```

1. Open <http://localhost:5221/quotes>.
2. In Development you are redirected to `/dev/login`. Sign in as **Estimator**, or as **Viewer** to see the access-denied behavior.

## Rules admin

A separate app for creating, editing and deleting the rules in `src/QuoteEstimator.Web/Config/rules.json`:

```sh
dotnet run --project src/QuoteEstimator.RulesAdmin --launch-profile http
```

1. Open <http://localhost:5231/rules>.
2. In Development you are redirected to `/dev/login`. Sign in as **RulesAdmin**, or as **Viewer** to see the access-denied behavior.

- Each save validates the whole rule file with the same loader the quote app uses. A save that would make the file invalid is rejected, and nothing is written.
- Each save needs a new rule set version, so quotes stay traceable to the change.
- If the file changed since the page loaded, the save is rejected and you are asked to reload.
- The quote app reloads the file automatically, so changes take effect without a restart.
- Fields, currency and rounding are read-only here.
- Saving rewrites the file: the header comment is kept, but the formatting is expanded.

## Test

```sh
dotnet test
```

- **Unit tests** cover the evaluator: each operator, bounds, precedence, conflicts, rounding, determinism and an empty or missing configuration. They also cover the config loader.
- **Fixture tests** run every case in `tests/QuoteEstimator.Tests/Fixtures/*.json` against the shipped `Config/rules.json`.
- **Integration tests** run the full app and cover: authorization (401/403), antiforgery, field errors (400), rule violations (422), an unavailable configuration (503), rate limiting (429 + `Retry-After`) and unhandled errors (500 with no quote data).

## How it works

| Piece | Location |
|---|---|
| Rule configuration | [src/QuoteEstimator.Web/Config/rules.json](src/QuoteEstimator.Web/Config/rules.json) |
| Load + strict validation | [Rules/RuleSetLoader.cs](src/QuoteEstimator.Web/Rules/RuleSetLoader.cs), [Rules/JsonRuleSetProvider.cs](src/QuoteEstimator.Web/Rules/JsonRuleSetProvider.cs) |
| Evaluator (pure, decimal) | [Rules/QuoteEvaluator.cs](src/QuoteEstimator.Web/Rules/QuoteEvaluator.cs) |
| API `POST /api/quotes` + form `GET /quotes` | [Controllers/QuotesController.cs](src/QuoteEstimator.Web/Controllers/QuotesController.cs) |
| Form behavior | [wwwroot/js/quote-form.js](src/QuoteEstimator.Web/wwwroot/js/quote-form.js) |
| Auth + rate limiting | [Auth/AuthSetup.cs](src/QuoteEstimator.Web/Auth/AuthSetup.cs), [RateLimiting.cs](src/QuoteEstimator.Web/RateLimiting.cs) |

**Evaluation order.** If no valid configuration is loaded, the result is *unavailable*. Otherwise:

1. **Field validation.** All field errors are reported together.
2. **Eligibility rules.** Every matching rule is reported, and no quote is produced.
3. **Rating rules.** The highest priority wins. A tie at the top priority is a *conflict*, and no match is *no applicable rule*. Neither produces a quote.
4. Rounding is applied once, at the end.

**Rule versions.** The rule file is watched and reloaded atomically. Each request evaluates against a single snapshot, and every response includes its `ruleSetVersion`. If the file becomes invalid, generation becomes unavailable; it never falls back to stale rules.

**Rule config format.** Fields are `number | integer | string | enum`, with `required`, `min`/`max`, `maxLength`, `allowedValues` and `valueLabels`. The same field definitions drive server validation and the rendered form.

Rules:
- Each rule has `id`, `kind` (`eligibility | rating`), `priority`, and `when`. `when` is a list of `{field, op, value}` conditions that must all match. The ops are `eq neq lt lte gt gte in`.
- Eligibility rules set `reject: {code, message}`.
- Rating rules set `basePremium` and ordered `factors`. Each factor is `add` or `multiply`, and has an optional `perField` (add only) and an optional `when`.

Validation is strict. Unknown properties, unknown fields, enum typos and missing rating rules all make the configuration invalid.

**API responses.**

| Status | Meaning | `code` |
|---|---|---|
| 200 | Quote: `{premium, currency, appliedRuleIds, ruleSetVersion}` | none |
| 400 | Field errors (`errors` keyed by field) | `FIELD_ERRORS` |
| 401 / 403 | Not signed in / not an Estimator (empty body) | none |
| 409 | Rules conflict | `RULE_CONFLICT` |
| 422 | Rule violations, or no applicable rule (`violations`) | `RULE_VIOLATION` or `NO_APPLICABLE_RULE` |
| 429 | Rate limited, with a `Retry-After` header | none |
| 503 | Rule configuration unavailable | `GENERATION_UNAVAILABLE` |

## Before release

- [ ] Replace `Config/rules.json` with the approved rules: fields, conditions, rates, rounding, precedence and messages.
- [ ] Replace `tests/.../Fixtures/placeholder-fixtures.json` with the approved input and expected-outcome fixtures.
- [ ] Connect a real identity provider that issues the `Estimator` role. Keep `Auth:UseDevAuth` false outside Development. With dev auth off, nobody can sign in, so the app fails closed.
- [ ] Confirm the client timeout (15s, `data-timeout-ms`), the rate-limit values (`RateLimit` in appsettings) and the error wording.
- [ ] Confirm the feature-flag and rollback procedure.
