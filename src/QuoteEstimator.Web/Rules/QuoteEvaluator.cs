using System.Globalization;
using System.Text.Json;

namespace QuoteEstimator.Web.Rules;

/// <summary>
/// Applies a rule set to quote inputs. Pure and deterministic: the result depends only on the
/// rule set and the inputs (no clock, I/O or randomness), and all arithmetic is decimal.
/// </summary>
public sealed class QuoteEvaluator
{
    public EvaluationResult Evaluate(RuleSet? ruleSet, IReadOnlyDictionary<string, JsonElement> inputs)
    {
        if (ruleSet is null || ruleSet.Fields.Count == 0 || ruleSet.Rules.Count == 0)
            return new EvaluationResult.Unavailable();

        var (values, fieldErrors) = ValidateInputs(ruleSet, inputs);
        if (fieldErrors.Count > 0)
            return new EvaluationResult.FieldErrors(fieldErrors, ruleSet.Version);

        var violations = ruleSet.Rules
            .Where(r => r.Kind == RuleKind.Eligibility && Matches(r.When, values))
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new Violation(r.Id, r.Reject!.Code, r.Reject.Message))
            .ToList();
        if (violations.Count > 0)
            return new EvaluationResult.RuleViolations(violations, ruleSet.Version);

        var applicable = ruleSet.Rules
            .Where(r => r.Kind == RuleKind.Rating && Matches(r.When, values))
            .ToList();
        if (applicable.Count == 0)
            return new EvaluationResult.NoApplicableRule(ruleSet.Version);

        var topPriority = applicable.Max(r => r.Priority);
        var winners = applicable.Where(r => r.Priority == topPriority).ToList();
        if (winners.Count > 1)
        {
            var ids = winners.Select(r => r.Id).Order(StringComparer.Ordinal).ToList();
            return new EvaluationResult.Conflict(ids, ruleSet.Version);
        }

        var rule = winners[0];
        var premium = Math.Round(CalculatePremium(rule, values), ruleSet.Rounding.Decimals, ruleSet.Rounding.Mode);
        return new EvaluationResult.Success(premium, ruleSet.Currency, [rule.Id], ruleSet.Version);
    }

    private static decimal CalculatePremium(Rule rule, IReadOnlyDictionary<string, object> values)
    {
        var premium = rule.BasePremium!.Value;
        foreach (var factor in rule.Factors)
        {
            if (!Matches(factor.When, values))
                continue;

            premium = factor.Op switch
            {
                FactorOp.Add when factor.PerField is { } perField =>
                    premium + factor.Value * (values.TryGetValue(perField, out var v) ? (decimal)v : 0m),
                FactorOp.Add => premium + factor.Value,
                FactorOp.Multiply => premium * factor.Value,
                _ => throw new InvalidOperationException($"Unsupported factor op '{factor.Op}'."),
            };
        }
        return premium;
    }

    private static (Dictionary<string, object> Values, Dictionary<string, string[]> Errors) ValidateInputs(
        RuleSet ruleSet, IReadOnlyDictionary<string, JsonElement> inputs)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var defined = ruleSet.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var key in inputs.Keys.Where(k => !defined.Contains(k)).Order(StringComparer.Ordinal))
            errors[key] = ["Unknown field."];

        foreach (var field in ruleSet.Fields)
        {
            var hasValue = inputs.TryGetValue(field.Name, out var raw)
                && raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && !(raw.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(raw.GetString()));

            if (!hasValue)
            {
                if (field.Required)
                    errors[field.Name] = [$"{field.Label} is required."];
                continue;
            }

            var error = field.IsNumeric
                ? ParseNumber(field, raw, values)
                : ParseString(field, raw, values);
            if (error is not null)
                errors[field.Name] = [error];
        }

        return (values, errors);
    }

    private static string? ParseNumber(FieldDefinition field, JsonElement raw, Dictionary<string, object> values)
    {
        if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetDecimal(out var number))
            return $"{field.Label} must be a number.";
        if (field.Type == FieldType.Integer && number != decimal.Truncate(number))
            return $"{field.Label} must be a whole number.";
        if (field.Min is { } min && number < min)
            return $"{field.Label} must be at least {Format(field, min)}.";
        if (field.Max is { } max && number > max)
            return $"{field.Label} must be at most {Format(field, max)}.";

        values[field.Name] = number;
        return null;
    }

    private static string? ParseString(FieldDefinition field, JsonElement raw, Dictionary<string, object> values)
    {
        if (raw.ValueKind != JsonValueKind.String)
            return field.Type == FieldType.Enum ? $"{field.Label} must be one of the listed options." : $"{field.Label} must be text.";

        var text = raw.GetString()!.Trim();
        if (field.Type == FieldType.Enum && !field.AllowedValues!.Contains(text, StringComparer.Ordinal))
            return $"{field.Label} must be one of the listed options.";
        if (field.MaxLength is { } maxLength && text.Length > maxLength)
            return $"{field.Label} must be at most {maxLength} characters.";

        values[field.Name] = text;
        return null;
    }

    // Integers (years, counts) read better without grouping; amounts with it.
    private static string Format(FieldDefinition field, decimal value) =>
        value.ToString(field.Type == FieldType.Integer ? "0" : "#,0.##", CultureInfo.InvariantCulture);

    private static bool Matches(IReadOnlyList<Condition> conditions, IReadOnlyDictionary<string, object> values) =>
        conditions.All(c => values.TryGetValue(c.Field, out var actual) && Matches(c, actual));

    private static bool Matches(Condition condition, object actual) => condition.Op switch
    {
        ConditionOp.In => condition.Value.EnumerateArray().Any(v => AreEqual(v, actual)),
        ConditionOp.Eq => AreEqual(condition.Value, actual),
        ConditionOp.Neq => !AreEqual(condition.Value, actual),
        ConditionOp.Lt => (decimal)actual < condition.Value.GetDecimal(),
        ConditionOp.Lte => (decimal)actual <= condition.Value.GetDecimal(),
        ConditionOp.Gt => (decimal)actual > condition.Value.GetDecimal(),
        ConditionOp.Gte => (decimal)actual >= condition.Value.GetDecimal(),
        _ => throw new InvalidOperationException($"Unsupported condition op '{condition.Op}'."),
    };

    private static bool AreEqual(JsonElement expected, object actual) => actual switch
    {
        decimal number => expected.GetDecimal() == number,
        string text => string.Equals(expected.GetString(), text, StringComparison.Ordinal),
        _ => false,
    };
}
