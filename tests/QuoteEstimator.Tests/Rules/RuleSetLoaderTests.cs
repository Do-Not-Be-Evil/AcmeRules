using Microsoft.Extensions.Logging.Abstractions;
using QuoteEstimator.Web.Rules;

namespace QuoteEstimator.Tests.Rules;

public class RuleSetLoaderTests
{
    private const string Field = """{ "name": "kind", "label": "Kind", "type": "enum", "required": true, "allowedValues": ["a", "b"] }""";
    private const string Rating = """{ "id": "rate", "kind": "rating", "priority": 1, "basePremium": 1 }""";

    private static string RuleSet(string rules = Rating, string fields = Field) => $$"""
        { "version": "v", "currency": "USD", "fields": [{{fields}}], "rules": [{{rules}}] }
        """;

    private static void AssertInvalid(string json, string expectedError)
    {
        var result = RuleSetLoader.Parse(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidRuleSetLoadsWithComments()
    {
        var result = RuleSetLoader.Parse("// comment\n" + RuleSet());

        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void EmptyConfigurationIsInvalid(string json) => AssertInvalid(json, "empty");

    [Fact]
    public void EmptyObjectIsInvalid() => AssertInvalid("{}", "At least one rating rule");

    [Fact]
    public void MalformedJsonIsInvalid() => AssertInvalid("{ \"version\": ", "not valid JSON");

    [Fact]
    public void MisspelledPropertyIsInvalid() =>
        AssertInvalid(RuleSet("""{ "id": "rate", "kind": "rating", "prioirty": 1, "basePremium": 1 }"""), "not valid JSON");

    [Fact]
    public void NoRatingRuleIsInvalid() =>
        AssertInvalid(RuleSet("""{ "id": "e", "kind": "eligibility", "when": [{ "field": "kind", "op": "eq", "value": "a" }], "reject": { "code": "X", "message": "x" } }"""),
            "At least one rating rule");

    [Fact]
    public void DuplicateRuleIdIsInvalid() => AssertInvalid(RuleSet(Rating + "," + Rating), "defined more than once");

    [Fact]
    public void UnknownConditionFieldIsInvalid() =>
        AssertInvalid(RuleSet("""{ "id": "rate", "kind": "rating", "basePremium": 1, "when": [{ "field": "nope", "op": "eq", "value": "a" }] }"""),
            "unknown field 'nope'");

    [Fact]
    public void OrderingOperatorOnTextFieldIsInvalid() =>
        AssertInvalid(RuleSet("""{ "id": "rate", "kind": "rating", "basePremium": 1, "when": [{ "field": "kind", "op": "gt", "value": "a" }] }"""),
            "only valid for numeric fields");

    [Fact]
    public void EnumConditionValueOutsideAllowedValuesIsInvalid() =>
        AssertInvalid(RuleSet("""{ "id": "rate", "kind": "rating", "basePremium": 1, "when": [{ "field": "kind", "op": "eq", "value": "typo" }] }"""),
            "not one of the field's allowedValues");

    [Fact]
    public void EligibilityRuleWithoutConditionsIsInvalid() =>
        AssertInvalid(RuleSet(Rating + """, { "id": "e", "kind": "eligibility", "reject": { "code": "X", "message": "x" } }"""),
            "need at least one condition");

    [Fact]
    public void RatingRuleWithoutBasePremiumIsInvalid() =>
        AssertInvalid(RuleSet("""{ "id": "rate", "kind": "rating" }"""), "non-negative basePremium");

    [Fact]
    public void MissingFileMakesProviderUnavailable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");
        using var provider = new JsonRuleSetProvider(path, NullLogger<JsonRuleSetProvider>.Instance, watchForChanges: false);

        Assert.Null(provider.GetCurrent());
    }

    [Fact]
    public void InvalidFileMakesProviderUnavailable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        try
        {
            using var provider = new JsonRuleSetProvider(path, NullLogger<JsonRuleSetProvider>.Instance, watchForChanges: false);
            Assert.Null(provider.GetCurrent());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
