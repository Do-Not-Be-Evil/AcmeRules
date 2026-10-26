# Quote-Generation Rules for Insurance Estimators

## Feature Summary

Apply quote-generation rules when insurance estimators generate quotes. The rule definitions and existing implementation details are open questions; confirm them before coding.

- **In scope:** Evaluate quote inputs against approved rules and return the resulting quote or validation failures.
- **Out of scope:** Rule authoring, policy issuance, and changes to insurance pricing not explicitly approved.
- **Prerequisites:** Approved rules, representative inputs, and expected quote outcomes.
- **Depends on:** Existing estimator workflow, authorization, and quote-generation service; availability is unconfirmed.

## Acceptance Criteria

- An authorized estimator submitting inputs that satisfy approved rules receives the expected quote for each approved test fixture.
- Inputs that violate an approved rule produce its specified validation failure and no successful quote.
- Missing required inputs produce field-level errors identifying the missing fields.
- While generation is pending, the interface displays a loading state and prevents repeat submission.
- Failed generation displays an error and allows retry without clearing entered inputs.
- Unauthorized requests are rejected server-side without returning quote data.
- Identical inputs evaluated against the same rules produce the approved expected outcome.

> Exact rule conditions, required fields, quote outputs, and error wording remain open questions.

## User Flow

1. **Entry point:** Estimator opens the existing quote-generation interface; its location and component are to be confirmed.
2. **Action:** Estimator enters the required quote inputs and requests generation.
3. **Feedback:** The interface marks generation as pending and disables repeat submission.
4. **Result:** The interface displays the quote or actionable validation errors.
5. **Next step:** Estimator can correct inputs and generate again.

On a service failure, retain inputs and offer retry. Existing navigation and cancellation behavior should remain unchanged.

## Edge Cases & Error States

- **Missing or invalid inputs:** Reject according to approved field requirements; show field-level errors.
- **No applicable rule or conflicting rules:** Do not invent a quote. Resolution behavior and rule precedence require approval.
- **Missing rule configuration:** Do not treat an empty rule set as successful validation; show generation unavailable.
- **Network failure, timeout, or server error:** Exit loading state, retain inputs, and offer retry without presenting a successful quote.
- **Unauthorized access:** Enforce existing server-side permissions; do not expose quote data.
- **Rules changing during generation:** Rule-version consistency and retry behavior are open questions.
- **Rate limiting:** If supported by the existing service, surface its retry guidance.

## Technical Approach

Repository paths, framework, API contract, and storage model were not supplied. Exact files and symbols must be identified before implementation; the following are responsibilities, not confirmed repository names.

- **Quote form component** — path/symbol open: Add pending state, field errors, failure feedback, and repeat-submission prevention.
- **Quote-generation handler** — path/symbol open: Enforce authorization and validate inputs before generation.
- **Rule evaluator** — path/symbol open: Apply approved rules with explicit outcomes for invalid inputs, missing configuration, and rule conflicts.
- **Quote-generation tests** — paths open: Add approved fixtures and failure-path coverage.

### Data flow

1. The interface submits quote inputs through the existing generation contract.
2. The server checks permission and validates inputs.
3. The evaluator applies the approved rules.
4. Existing generation logic produces a quote only when evaluation permits it.
5. The interface renders the quote or errors and clears pending state.

Do not introduce schema changes or new packages until existing capabilities are confirmed.

## Dependencies & Risks

- **Blocking questions:** What are the rules, their precedence, required inputs, calculation and rounding requirements, and expected outputs?
- **Integration questions:** Where are the estimator interface, generation handler, permission checks, and rules stored?
- **Security:** Client-side validation alone must not authorize generation.
- **Correctness:** Undefined precedence or rounding could produce incorrect quotes; require approved fixtures before release.
- **Performance:** Expected rule volume and request volume are unknown.
- **Rollout:** Use the existing release mechanism. Feature-flag availability and rollback procedure require confirmation; retain the previous implementation for rollback.

## Test Plan

### Manual

- Generate a quote using each approved valid fixture and compare the result.
- Submit missing and invalid inputs; verify errors and absence of a successful quote.
- Simulate slow responses and failures; verify loading, repeat-submission prevention, input retention, and retry.
- Attempt generation without permission; verify rejection.

### Automated

- Unit-test every approved rule, boundary condition, and precedence decision.
- Integration-test server authorization, invalid inputs, missing configuration, and generation failures.
- Test the complete valid-generation and correction flows through the interface.

### Regression

- Verify existing estimator navigation and input handling.
- Verify previously approved quote fixtures remain correct unless an approved rule change explicitly changes them.
