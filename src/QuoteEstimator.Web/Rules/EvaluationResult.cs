namespace QuoteEstimator.Web.Rules;

/// <summary>The outcome of evaluating quote inputs. Only <see cref="Success"/> carries a quote.</summary>
public abstract record EvaluationResult
{
    private EvaluationResult() { }

    public sealed record Success(
        decimal Premium,
        string Currency,
        IReadOnlyList<string> AppliedRuleIds,
        string RuleSetVersion) : EvaluationResult;

    /// <summary>Missing or invalid inputs, keyed by field name.</summary>
    public sealed record FieldErrors(
        IReadOnlyDictionary<string, string[]> Errors,
        string RuleSetVersion) : EvaluationResult;

    /// <summary>One or more eligibility rules rejected the inputs.</summary>
    public sealed record RuleViolations(
        IReadOnlyList<Violation> Violations,
        string RuleSetVersion) : EvaluationResult;

    /// <summary>Inputs are valid but no rating rule applies; no quote is invented.</summary>
    public sealed record NoApplicableRule(string RuleSetVersion) : EvaluationResult;

    /// <summary>More than one rating rule applies at the same highest priority.</summary>
    public sealed record Conflict(
        IReadOnlyList<string> RuleIds,
        string RuleSetVersion) : EvaluationResult;

    /// <summary>No valid rule configuration is loaded.</summary>
    public sealed record Unavailable : EvaluationResult;
}

public sealed record Violation(string RuleId, string Code, string Message);
