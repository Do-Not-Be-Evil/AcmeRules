using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuoteEstimator.Rules;

public sealed record RuleSetLoadResult(RuleSet? RuleSet, IReadOnlyList<string> Errors)
{
    public bool IsValid => RuleSet is not null;
}

/// <summary>
/// Parses and validates rule configuration. Any problem yields no rule set, so a broken
/// or empty configuration can never be mistaken for one that passes every input.
/// </summary>
public static class RuleSetLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A misspelled property (e.g. "prioirty") must fail loudly rather than silently default.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static RuleSetLoadResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Invalid("Rule configuration is empty.");

        RuleSet? ruleSet;
        try
        {
            ruleSet = JsonSerializer.Deserialize<RuleSet>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Invalid($"Rule configuration is not valid JSON: {ex.Message}");
        }

        if (ruleSet is null)
            return Invalid("Rule configuration is empty.");

        var errors = Validate(ruleSet);
        return errors.Count == 0 ? new(ruleSet, []) : new(null, errors);
    }

    private static RuleSetLoadResult Invalid(string error) => new(null, [error]);

    private static List<string> Validate(RuleSet ruleSet)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ruleSet.Version))
            errors.Add("version is required.");
        if (ruleSet.Currency is not { Length: 3 })
            errors.Add("currency must be a 3-letter ISO code.");
        if (ruleSet.Rounding.Decimals is < 0 or > 6)
            errors.Add("rounding.decimals must be between 0 and 6.");
        if (!Enum.IsDefined(ruleSet.Rounding.Mode))
            errors.Add("rounding.mode is not a valid MidpointRounding value.");

        var fields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        if (ruleSet.Fields.Count == 0)
            errors.Add("At least one field must be defined.");
        foreach (var field in ruleSet.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                errors.Add("Every field needs a name.");
                continue;
            }
            if (!fields.TryAdd(field.Name, field))
                errors.Add($"Field '{field.Name}' is defined more than once.");
            if (string.IsNullOrWhiteSpace(field.Label))
                errors.Add($"Field '{field.Name}' needs a label.");
            if (field.Min is { } min && field.Max is { } max && min > max)
                errors.Add($"Field '{field.Name}' has min greater than max.");
            if (field.Type == FieldType.Enum && field.AllowedValues is not { Count: > 0 })
                errors.Add($"Enum field '{field.Name}' needs allowedValues.");
        }

        if (ruleSet.Rules.Count(r => r.Kind == RuleKind.Rating) == 0)
            errors.Add("At least one rating rule must be defined.");

        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in ruleSet.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                errors.Add("Every rule needs an id.");
                continue;
            }
            if (!ruleIds.Add(rule.Id))
                errors.Add($"Rule '{rule.Id}' is defined more than once.");

            var where = $"Rule '{rule.Id}'";
            ValidateConditions(rule.When, fields, where, errors);

            switch (rule.Kind)
            {
                case RuleKind.Eligibility:
                    if (rule.When.Count == 0)
                        errors.Add($"{where}: eligibility rules need at least one condition.");
                    if (rule.Reject is null || string.IsNullOrWhiteSpace(rule.Reject.Code) || string.IsNullOrWhiteSpace(rule.Reject.Message))
                        errors.Add($"{where}: eligibility rules need reject.code and reject.message.");
                    if (rule.BasePremium is not null || rule.Factors.Count > 0)
                        errors.Add($"{where}: eligibility rules cannot set basePremium or factors.");
                    break;

                case RuleKind.Rating:
                    if (rule.BasePremium is not >= 0)
                        errors.Add($"{where}: rating rules need a non-negative basePremium.");
                    if (rule.Reject is not null)
                        errors.Add($"{where}: rating rules cannot set reject.");
                    for (var i = 0; i < rule.Factors.Count; i++)
                    {
                        var factor = rule.Factors[i];
                        var factorWhere = $"{where} factor {i + 1}";
                        if (!Enum.IsDefined(factor.Op))
                            errors.Add($"{factorWhere}: unknown op.");
                        if (factor.PerField is not null)
                        {
                            if (factor.Op != FactorOp.Add)
                                errors.Add($"{factorWhere}: perField is only supported with op 'add'.");
                            if (!fields.TryGetValue(factor.PerField, out var perField) || !perField.IsNumeric)
                                errors.Add($"{factorWhere}: perField '{factor.PerField}' must be a numeric field.");
                        }
                        ValidateConditions(factor.When, fields, factorWhere, errors);
                    }
                    break;

                default:
                    errors.Add($"{where}: unknown kind.");
                    break;
            }
        }

        return errors;
    }

    private static void ValidateConditions(
        IReadOnlyList<Condition> conditions,
        Dictionary<string, FieldDefinition> fields,
        string where,
        List<string> errors)
    {
        foreach (var condition in conditions)
        {
            if (!fields.TryGetValue(condition.Field, out var field))
            {
                errors.Add($"{where}: condition references unknown field '{condition.Field}'.");
                continue;
            }

            var label = $"{where}: condition on '{field.Name}'";
            if (!Enum.IsDefined(condition.Op))
            {
                errors.Add($"{label} has an unknown op.");
                continue;
            }

            if (!field.IsNumeric && condition.Op is not (ConditionOp.Eq or ConditionOp.Neq or ConditionOp.In))
                errors.Add($"{label}: op '{condition.Op}' is only valid for numeric fields.");

            var values = condition.Op == ConditionOp.In
                ? condition.Value.ValueKind == JsonValueKind.Array ? condition.Value.EnumerateArray().ToList() : null
                : [condition.Value];

            if (values is null || values.Count == 0)
            {
                errors.Add($"{label}: op 'in' needs a non-empty array value.");
                continue;
            }

            foreach (var value in values)
            {
                if (field.IsNumeric)
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out _))
                        errors.Add($"{label}: value must be a number.");
                }
                else if (value.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"{label}: value must be a string.");
                }
                else if (field.Type == FieldType.Enum && !field.AllowedValues!.Contains(value.GetString()!, StringComparer.Ordinal))
                {
                    errors.Add($"{label}: '{value.GetString()}' is not one of the field's allowedValues.");
                }
            }
        }
    }
}
