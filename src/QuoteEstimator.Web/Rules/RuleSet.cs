using System.Text.Json;

namespace QuoteEstimator.Web.Rules;

/// <summary>
/// An immutable, versioned set of quote-generation rules loaded from configuration.
/// A single instance is used for the whole of one evaluation so results are traceable to <see cref="Version"/>.
/// </summary>
public sealed class RuleSet
{
    public string Version { get; init; } = "";
    public string Currency { get; init; } = "";
    public IReadOnlyList<FieldDefinition> Fields { get; init; } = [];
    public IReadOnlyList<Rule> Rules { get; init; } = [];
    public RoundingOptions Rounding { get; init; } = new();
}

public enum FieldType { Number, Integer, String, Enum }

public sealed class FieldDefinition
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public FieldType Type { get; init; }
    public bool Required { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public int? MaxLength { get; init; }
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>Optional display labels for enum values, keyed by value.</summary>
    public IReadOnlyDictionary<string, string>? ValueLabels { get; init; }

    public string? Help { get; init; }

    public bool IsNumeric => Type is FieldType.Number or FieldType.Integer;
}

public enum RuleKind { Eligibility, Rating }

public sealed class Rule
{
    public string Id { get; init; } = "";
    public RuleKind Kind { get; init; }

    /// <summary>Higher wins. Rating rules tied at the highest matching priority are a conflict.</summary>
    public int Priority { get; init; }

    /// <summary>All conditions must match (AND). Conditions on an absent optional field do not match.</summary>
    public IReadOnlyList<Condition> When { get; init; } = [];

    /// <summary>Eligibility rules only: the validation failure produced when the rule matches.</summary>
    public RejectOutcome? Reject { get; init; }

    /// <summary>Rating rules only.</summary>
    public decimal? BasePremium { get; init; }

    /// <summary>Rating rules only: applied in order to the running premium.</summary>
    public IReadOnlyList<Factor> Factors { get; init; } = [];
}

public sealed class RejectOutcome
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

public enum ConditionOp { Eq, Neq, Lt, Lte, Gt, Gte, In }

public sealed class Condition
{
    public string Field { get; init; } = "";
    public ConditionOp Op { get; init; }
    public JsonElement Value { get; init; }
}

public enum FactorOp { Add, Multiply }

public sealed class Factor
{
    public FactorOp Op { get; init; }
    public decimal Value { get; init; }

    /// <summary>Add only: when set, adds <see cref="Value"/> multiplied by this numeric field's input.</summary>
    public string? PerField { get; init; }

    public IReadOnlyList<Condition> When { get; init; } = [];
}

public sealed class RoundingOptions
{
    public int Decimals { get; init; } = 2;
    public MidpointRounding Mode { get; init; } = MidpointRounding.AwayFromZero;
}
