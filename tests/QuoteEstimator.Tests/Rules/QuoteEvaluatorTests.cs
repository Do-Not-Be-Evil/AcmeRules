using QuoteEstimator.Web.Rules;

namespace QuoteEstimator.Tests.Rules;

public class QuoteEvaluatorTests
{
    private const string Fields = """
        "fields": [
          { "name": "kind", "label": "Kind", "type": "enum", "required": true, "allowedValues": ["a", "b", "c", "d"] },
          { "name": "amount", "label": "Amount", "type": "number", "required": true, "min": 10, "max": 100 },
          { "name": "count", "label": "Count", "type": "integer", "required": false, "min": 0, "max": 5 },
          { "name": "note", "label": "Note", "type": "string", "required": false, "maxLength": 5 }
        ]
        """;

    private const string TestRules = $$"""
        {
          "version": "test-1", "currency": "USD", "rounding": { "decimals": 2, "mode": "awayFromZero" },
          {{Fields}},
          "rules": [
            { "id": "elig-c", "kind": "eligibility", "priority": 100,
              "when": [{ "field": "kind", "op": "eq", "value": "c" }],
              "reject": { "code": "C_BLOCKED", "message": "C is blocked." } },
            { "id": "rate-a", "kind": "rating", "priority": 1,
              "when": [{ "field": "kind", "op": "eq", "value": "a" }],
              "basePremium": 10,
              "factors": [
                { "op": "add", "value": 0.5, "perField": "amount" },
                { "op": "multiply", "value": 1.1, "when": [{ "field": "count", "op": "gte", "value": 2 }] }
              ] },
            { "id": "rate-a-big", "kind": "rating", "priority": 5,
              "when": [{ "field": "kind", "op": "eq", "value": "a" }, { "field": "amount", "op": "gt", "value": 50 }],
              "basePremium": 100 },
            { "id": "rate-b1", "kind": "rating", "priority": 1, "when": [{ "field": "kind", "op": "eq", "value": "b" }], "basePremium": 1 },
            { "id": "rate-b2", "kind": "rating", "priority": 1, "when": [{ "field": "kind", "op": "eq", "value": "b" }], "basePremium": 2 }
          ]
        }
        """;

    private static readonly QuoteEvaluator Evaluator = new();

    private static RuleSet Load(string json)
    {
        var result = RuleSetLoader.Parse(json);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        return result.RuleSet!;
    }

    private static EvaluationResult Evaluate(object inputs, string rules = TestRules) =>
        Evaluator.Evaluate(Load(rules), TestInputs.From(inputs));

    [Fact]
    public void ValidInputsProduceQuoteFromMatchingRatingRule()
    {
        var result = Assert.IsType<EvaluationResult.Success>(Evaluate(new { kind = "a", amount = 20 }));

        Assert.Equal(20.00m, result.Premium); // 10 + 20 * 0.5
        Assert.Equal(["rate-a"], result.AppliedRuleIds);
        Assert.Equal("USD", result.Currency);
        Assert.Equal("test-1", result.RuleSetVersion);
    }

    [Fact]
    public void ConditionalFactorAppliesOnlyWhenItsConditionMatches()
    {
        var without = Assert.IsType<EvaluationResult.Success>(Evaluate(new { kind = "a", amount = 20, count = 1 }));
        var with = Assert.IsType<EvaluationResult.Success>(Evaluate(new { kind = "a", amount = 20, count = 2 }));

        Assert.Equal(20.00m, without.Premium);
        Assert.Equal(22.00m, with.Premium);
    }

    [Fact]
    public void HigherPriorityRatingRuleWins()
    {
        var result = Assert.IsType<EvaluationResult.Success>(Evaluate(new { kind = "a", amount = 60 }));

        Assert.Equal(["rate-a-big"], result.AppliedRuleIds);
        Assert.Equal(100m, result.Premium);
    }

    [Fact]
    public void TieAtHighestPriorityIsAConflictWithNoQuote()
    {
        var result = Assert.IsType<EvaluationResult.Conflict>(Evaluate(new { kind = "b", amount = 20 }));

        Assert.Equal(["rate-b1", "rate-b2"], result.RuleIds);
    }

    [Fact]
    public void NoMatchingRatingRuleProducesNoQuote() =>
        Assert.IsType<EvaluationResult.NoApplicableRule>(Evaluate(new { kind = "d", amount = 20 }));

    [Fact]
    public void MatchingEligibilityRuleRejectsWithItsCodeAndMessage()
    {
        var result = Assert.IsType<EvaluationResult.RuleViolations>(Evaluate(new { kind = "c", amount = 20 }));

        var violation = Assert.Single(result.Violations);
        Assert.Equal(new Violation("elig-c", "C_BLOCKED", "C is blocked."), violation);
    }

    [Fact]
    public void MissingRequiredFieldsAreAllReported()
    {
        var result = Assert.IsType<EvaluationResult.FieldErrors>(Evaluate(new { }));

        Assert.Equal(["amount", "kind"], result.Errors.Keys.Order());
        Assert.Equal(["Kind is required."], result.Errors["kind"]);
    }

    [Fact]
    public void NullAndBlankValuesCountAsMissing()
    {
        var result = Assert.IsType<EvaluationResult.FieldErrors>(Evaluate(new { kind = "  ", amount = (decimal?)null }));

        Assert.Equal(["amount", "kind"], result.Errors.Keys.Order());
    }

    [Fact]
    public void FieldErrorsTakePrecedenceOverEligibility()
    {
        // kind "c" would be rejected by elig-c, but the invalid amount is reported first.
        var result = Assert.IsType<EvaluationResult.FieldErrors>(Evaluate(new { kind = "c", amount = 5 }));

        Assert.Equal(["amount"], result.Errors.Keys);
    }

    [Theory]
    [InlineData(9.99, false)]
    [InlineData(10, true)]
    [InlineData(100, true)]
    [InlineData(100.01, false)]
    public void NumericBoundsAreInclusive(decimal amount, bool valid)
    {
        var result = Evaluate(new { kind = "a", amount });

        if (valid) Assert.IsType<EvaluationResult.Success>(result);
        else Assert.Equal(["amount"], Assert.IsType<EvaluationResult.FieldErrors>(result).Errors.Keys);
    }

    [Fact]
    public void BoundsErrorsNameTheLimit()
    {
        var low = Assert.IsType<EvaluationResult.FieldErrors>(Evaluate(new { kind = "a", amount = 1 }));
        var high = Assert.IsType<EvaluationResult.FieldErrors>(Evaluate(new { kind = "a", amount = 101 }));

        Assert.Equal(["Amount must be at least 10."], low.Errors["amount"]);
        Assert.Equal(["Amount must be at most 100."], high.Errors["amount"]);
    }

    [Theory]
    [InlineData("""{ "kind": "a", "amount": "20" }""", "amount", "Amount must be a number.")]
    [InlineData("""{ "kind": "a", "amount": 20, "count": 1.5 }""", "count", "Count must be a whole number.")]
    [InlineData("""{ "kind": "z", "amount": 20 }""", "kind", "Kind must be one of the listed options.")]
    [InlineData("""{ "kind": 1, "amount": 20 }""", "kind", "Kind must be one of the listed options.")]
    [InlineData("""{ "kind": "a", "amount": 20, "note": "toolong" }""", "note", "Note must be at most 5 characters.")]
    [InlineData("""{ "kind": "a", "amount": 20, "extra": 1 }""", "extra", "Unknown field.")]
    public void InvalidValuesProduceFieldErrors(string json, string field, string message)
    {
        var inputs = TestInputs.FromJson(System.Text.Json.JsonDocument.Parse(json).RootElement);
        var result = Assert.IsType<EvaluationResult.FieldErrors>(Evaluator.Evaluate(Load(TestRules), inputs));

        Assert.Equal([message], result.Errors[field]);
    }

    [Fact]
    public void ConditionOnAbsentOptionalFieldDoesNotMatch()
    {
        // The count >= 2 factor must not apply when count is omitted.
        var result = Assert.IsType<EvaluationResult.Success>(Evaluate(new { kind = "a", amount = 20 }));

        Assert.Equal(20.00m, result.Premium);
    }

    [Theory]
    [InlineData("eq", "50", 50, true)]
    [InlineData("eq", "50", 51, false)]
    [InlineData("neq", "50", 51, true)]
    [InlineData("neq", "50", 50, false)]
    [InlineData("lt", "50", 49, true)]
    [InlineData("lt", "50", 50, false)]
    [InlineData("lte", "50", 50, true)]
    [InlineData("lte", "50", 51, false)]
    [InlineData("gt", "50", 51, true)]
    [InlineData("gt", "50", 50, false)]
    [InlineData("gte", "50", 50, true)]
    [InlineData("gte", "50", 49, false)]
    [InlineData("in", "[20, 30]", 30, true)]
    [InlineData("in", "[20, 30]", 25, false)]
    public void ConditionOperators(string op, string value, decimal amount, bool matches)
    {
        var rules = $$"""
            {
              "version": "ops", "currency": "USD",
              {{Fields}},
              "rules": [
                { "id": "probe", "kind": "eligibility", "priority": 1,
                  "when": [{ "field": "amount", "op": "{{op}}", "value": {{value}} }],
                  "reject": { "code": "MATCHED", "message": "matched" } },
                { "id": "rate", "kind": "rating", "priority": 1, "basePremium": 1 }
              ]
            }
            """;

        var result = Evaluate(new { kind = "a", amount }, rules);

        if (matches) Assert.IsType<EvaluationResult.RuleViolations>(result);
        else Assert.IsType<EvaluationResult.Success>(result);
    }

    [Theory]
    [InlineData("awayFromZero", 10.01)]
    [InlineData("toEven", 10.00)]
    public void RoundingFollowsConfiguredMode(string mode, decimal expected)
    {
        var rules = $$"""
            {
              "version": "rounding", "currency": "USD", "rounding": { "decimals": 2, "mode": "{{mode}}" },
              {{Fields}},
              "rules": [{ "id": "rate", "kind": "rating", "priority": 1, "basePremium": 10.005 }]
            }
            """;

        var result = Assert.IsType<EvaluationResult.Success>(Evaluate(new { kind = "a", amount = 20 }, rules));

        Assert.Equal(expected, result.Premium);
    }

    [Fact]
    public void IdenticalInputsAndRulesProduceIdenticalResults()
    {
        var ruleSet = Load(TestRules);
        var results = Enumerable.Range(0, 50)
            .Select(_ => Assert.IsType<EvaluationResult.Success>(
                Evaluator.Evaluate(ruleSet, TestInputs.From(new { kind = "a", amount = 33.33m, count = 3 }))))
            .ToList();

        Assert.All(results, r =>
        {
            Assert.Equal(results[0].Premium, r.Premium);
            Assert.Equal(results[0].AppliedRuleIds, r.AppliedRuleIds);
        });
    }

    [Fact]
    public void MissingRuleSetIsUnavailable() =>
        Assert.IsType<EvaluationResult.Unavailable>(Evaluator.Evaluate(null, TestInputs.From(new { kind = "a", amount = 20 })));

    [Fact]
    public void EmptyRuleSetIsUnavailableNotAPass() =>
        Assert.IsType<EvaluationResult.Unavailable>(Evaluator.Evaluate(new RuleSet(), TestInputs.From(new { })));
}
