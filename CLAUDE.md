# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Quote Estimator: an ASP.NET Core MVC app (.NET 10, vanilla JS, no front-end framework) that applies configurable quote rules for insurance estimators. A second MVC app, `src/QuoteEstimator.RulesAdmin`, creates, edits and deletes the rules in that app's `Config/rules.json`. Both apps share the rule model and validation through the `src/QuoteEstimator.Rules` class library. `feature_summary.md` holds the acceptance criteria. `README.md` documents the rule config format and API status codes.

**The rules, rates, rounding mode, precedence policy, messages and fixtures are placeholders and not approved for production.** Do not invent or change pricing logic or rates unless the user asks. Feature-summary constraint: add no new packages and no schema/storage changes until existing capabilities are confirmed.

## Commands
6
```sh
dotnet build                                                  # whole solution (QuoteEstimator.slnx)
dotnet run --project src/QuoteEstimator.Web --launch-profile http   # http://localhost:5221/quotes
dotnet run --project src/QuoteEstimator.RulesAdmin --launch-profile http   # http://localhost:5231/rules
dotnet test                                                   # all tests (xUnit)
dotnet test --filter "FullyQualifiedName~QuoteEvaluatorTests"  # one class
dotnet test --filter "FullyQualifiedName~QuotesApiTests.AuthorizedEstimatorReceivesQuote"  # one test
dotnet test --filter "DisplayName~midpoint-rounds-away-from-zero"  # one fixture case
```

In Development, `/quotes` redirects to `/dev/login`. Sign in as `Estimator`, or as `Viewer` to see a 403. The admin app works the same way with the `RulesAdmin` role.

## Architecture

**Request flow:** `QuotesController.Generate` (`POST /api/quotes`, Estimator policy, antiforgery via the `X-CSRF-TOKEN` header, rate limited) takes **one** `RuleSet` snapshot from `IRuleSetProvider.GetCurrent()`. It passes the snapshot to `QuoteEvaluator.Evaluate`. The controller then pattern-matches the closed `EvaluationResult` union (`Success | FieldErrors | RuleViolations | NoApplicableRule | Conflict | Unavailable`) to a status code and a `problem+json` body with a stable `code`. When you add a new outcome, add a record to `EvaluationResult`, a branch in the controller switch, and an outcome name in `FixtureTests`.

**Rules pipeline:**
- `Config/rules.json` is JSON with comments. Its path comes from `Rules:Path` and is resolved against the content root.
- `RuleSet` and `RuleSetLoader` live in `src/QuoteEstimator.Rules` (namespace `QuoteEstimator.Rules`, a global using in each consuming csproj). `QuoteEvaluator`, `EvaluationResult` and `JsonRuleSetProvider` stay in the Web project.
- `RuleSetLoader.Parse` validates strictly: unknown properties, unknown fields and enum typos all make the config invalid. It returns errors and never throws for bad config.
- `JsonRuleSetProvider` watches the file and swaps in an immutable snapshot with `Volatile`. If the file is invalid or missing, the provider sets the snapshot to `null`, and the result is `Unavailable` (503). It never falls back to stale rules.
- `QuoteEvaluator` is pure and deterministic: decimal arithmetic only, with no clock, I/O or randomness. It runs in this order: field validation (all errors together) → eligibility rules (every match is reported) → rating rules (the highest priority wins, a tie is a `Conflict`) → rounding, once, at the end.
- The same `fields` definitions drive both server validation and the rendered form. `QuoteFormViewModel` and `Views/Quotes/Index.cshtml` build the form, and `wwwroot/js/quote-form.js` handles the pending state, field errors and retry. The client timeout is set through `data-timeout-ms`.

**Auth:** `Auth/AuthSetup.cs` sets up cookie auth plus the `Estimator` role policy. There is no real identity provider yet. `DevLoginController` issues cookies only when `Auth:UseDevAuth` is true, which is set only in `appsettings.Development.json`. With dev auth off, no one can sign in, so the app fails closed. For `/api/*` paths, auth failures return a bare 401/403, never a redirect. `Program.cs` uses `UseExceptionHandler` so unhandled errors become a generic 500 with no quote data.

**Rules admin (`src/QuoteEstimator.RulesAdmin`):** `Rules:Path` defaults to `../QuoteEstimator.Web/Config/rules.json`. `RuleFileStore` is the only code that reads or writes the file. Every change goes through `Mutate`, which:
- requires the ETag (a SHA-256 of the file) that the editor loaded, and returns 409 `STALE` if the file changed since;
- requires a new, different `version`;
- edits only the `rules` array as `JsonNode`, so other sections and decimal text such as `0.90` are kept;
- re-validates the whole file with `RuleSetLoader` and writes nothing if it is invalid;
- writes a temp file and then `File.Move`s it over the original, keeping the leading `//` header comment.

Inline comments and compact one-line formatting are lost on rewrite. `RulesController` maps the closed `RuleFileChange` union to `problem+json` codes. The editor (`wwwroot/js/rule-editor.js`) builds the form from the file's `fields`. `api.js` holds the shared fetch and error helpers.

## Testing notes

- The test project links `src/QuoteEstimator.Web/Config/rules.json` into its output. `FixtureTests` runs every case in `tests/QuoteEstimator.Tests/Fixtures/*.json` against the **shipped** rule file, so editing `rules.json` without updating the fixtures breaks the tests on purpose. Fixture premiums are calculated by hand from the rules and noted in comments; do not regenerate them from the code.
- The integration tests (`Api/QuotesApiTests.cs`) use `QuoteAppFactory`: the `Testing` environment, dev auth forced on, and a raised rate limit. They also hard-code premiums from the shipped rules, for example 1140.00. `SignedInClient` signs in through `/dev/login` and scrapes the antiforgery token from the page HTML.
- Use `TestInputs.From(new { ... })` to build evaluator inputs in the same shape the API receives them (`Dictionary<string, JsonElement>`).
- .NET 10 generates a public `Program` class in both apps, so the tests reference RulesAdmin through `Aliases="RulesAdmin"`. Admin test files start with `extern alias RulesAdmin;`, and `RulesAdminFactory` uses `RulesController` as its entry marker. Admin tests write only to a `TempRuleFile` copy, never the shipped file.
- Saving through the admin app against the real `rules.json` breaks `FixtureTests` and the API premium assertions until the fixtures are updated. That is intended.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

---

**These guidelines are working if:** fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.