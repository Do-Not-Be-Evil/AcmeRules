using System.Text.Json;
using QuoteEstimator.Web.Rules;

namespace QuoteEstimator.Tests.Rules;

/// <summary>Runs every input → expected-outcome fixture against the shipped rule file.</summary>
public class FixtureTests
{
    private static readonly string BaseDir = AppContext.BaseDirectory;

    private static readonly Lazy<Dictionary<string, JsonElement>> Fixtures = new(() =>
        Directory.GetFiles(Path.Combine(BaseDir, "Fixtures"), "*.json")
            .SelectMany(file => JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
                .RootElement.EnumerateArray().Select(e => e.Clone()))
            .ToDictionary(e => e.GetProperty("name").GetString()!));

    public static TheoryData<string> FixtureNames => new(Fixtures.Value.Keys.Order());

    private static RuleSet LoadShippedRules()
    {
        var result = RuleSetLoader.Parse(File.ReadAllText(Path.Combine(BaseDir, "Config", "rules.json")));
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        return result.RuleSet!;
    }

    [Fact]
    public void ShippedRuleFileIsValid() => LoadShippedRules();

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void FixtureProducesExpectedOutcome(string name)
    {
        var fixture = Fixtures.Value[name];
        var expected = fixture.GetProperty("expected");
        var result = new QuoteEvaluator().Evaluate(LoadShippedRules(), TestInputs.FromJson(fixture.GetProperty("input")));

        switch (expected.GetProperty("outcome").GetString())
        {
            case "success":
                var success = Assert.IsType<EvaluationResult.Success>(result);
                Assert.Equal(expected.GetProperty("premium").GetDecimal(), success.Premium);
                Assert.Equal(Strings(expected, "appliedRuleIds"), success.AppliedRuleIds);
                break;
            case "fieldErrors":
                var fieldErrors = Assert.IsType<EvaluationResult.FieldErrors>(result);
                Assert.Equal(Strings(expected, "fields").Order(), fieldErrors.Errors.Keys.Order());
                break;
            case "ruleViolations":
                var violations = Assert.IsType<EvaluationResult.RuleViolations>(result);
                Assert.Equal(Strings(expected, "codes").Order(), violations.Violations.Select(v => v.Code).Order());
                break;
            case "noApplicableRule":
                Assert.IsType<EvaluationResult.NoApplicableRule>(result);
                break;
            case "conflict":
                Assert.IsType<EvaluationResult.Conflict>(result);
                break;
            case var other:
                Assert.Fail($"Fixture '{name}' has unknown expected outcome '{other}'.");
                break;
        }
    }

    private static string[] Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();
}
