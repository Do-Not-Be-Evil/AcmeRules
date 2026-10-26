using System.Globalization;
using System.Text.Json;

namespace QuoteEstimator.RulesAdmin.Models;

/// <summary>Short, readable summaries of rules for the rules table.</summary>
public static class RuleDisplay
{
    public static string Conditions(IReadOnlyList<Condition> conditions) =>
        conditions.Count == 0 ? "Always" : string.Join(" and ", conditions.Select(Condition));

    public static string Condition(Condition condition) =>
        $"{condition.Field} {Op(condition.Op)} {Value(condition.Value)}";

    public static string Outcome(Rule rule) => rule.Kind switch
    {
        RuleKind.Eligibility => $"Reject: {rule.Reject?.Code}",
        RuleKind.Rating => $"Base {rule.BasePremium?.ToString(CultureInfo.InvariantCulture)}, {rule.Factors.Count} factor{(rule.Factors.Count == 1 ? "" : "s")}",
        _ => "",
    };

    private static string Op(ConditionOp op) => op switch
    {
        ConditionOp.Eq => "=",
        ConditionOp.Neq => "≠",
        ConditionOp.Lt => "<",
        ConditionOp.Lte => "≤",
        ConditionOp.Gt => ">",
        ConditionOp.Gte => "≥",
        ConditionOp.In => "in",
        _ => op.ToString(),
    };

    private static string Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(Value)) + "]",
        _ => value.GetRawText(),
    };
}
