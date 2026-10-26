extern alias RulesAdmin;

using System.Text.Json.Nodes;
using RulesAdmin::QuoteEstimator.RulesAdmin.Rules;

namespace QuoteEstimator.Tests.Admin;

public sealed class RuleFileStoreTests : IDisposable
{
    private readonly TempRuleFile _file = new();
    private readonly RuleFileStore _store;

    public RuleFileStoreTests() => _store = new RuleFileStore(_file.Path);

    public void Dispose() => _file.Dispose();

    private static JsonObject EligibilityRule(string id = "elig-test") => JsonNode.Parse($$"""
        { "id": "{{id}}", "kind": "eligibility", "priority": 50,
          "when": [{ "field": "yearBuilt", "op": "lt", "value": 1920 }],
          "reject": { "code": "OLD_HOME", "message": "Homes built before 1920 can't be quoted." } }
        """)!.AsObject();

    private RuleSet Reload()
    {
        var result = RuleSetLoader.Parse(_file.Text);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        return result.RuleSet!;
    }

    [Fact]
    public void ReadReturnsShippedRules()
    {
        var snapshot = _store.Read();

        Assert.True(snapshot.CanEdit);
        Assert.NotNull(snapshot.Etag);
        Assert.Contains(snapshot.Load.RuleSet!.Rules, r => r.Id == "rate-condo");
    }

    [Fact]
    public void CreateAddsRuleSetsVersionAndKeepsHeaderComment()
    {
        var change = _store.Create(_store.Read().Etag, "v2", EligibilityRule());

        var saved = Assert.IsType<RuleFileChange.Saved>(change);
        Assert.Equal("v2", saved.Version);
        Assert.Equal(_store.Read().Etag, saved.Etag);
        Assert.StartsWith("// =====", _file.Text);
        Assert.Contains("PLACEHOLDER RULE SET", _file.Text);
        Assert.Contains("can't be quoted", _file.Text);

        var ruleSet = Reload();
        Assert.Equal("v2", ruleSet.Version);
        var rule = Assert.Single(ruleSet.Rules, r => r.Id == "elig-test");
        Assert.Equal("OLD_HOME", rule.Reject!.Code);
    }

    [Fact]
    public void RewriteKeepsDecimalTextAndUntouchedSections()
    {
        var before = RuleSetLoader.Parse(_file.Text).RuleSet!;

        _store.Create(_store.Read().Etag, "v2", EligibilityRule());

        Assert.Contains("0.0035", _file.Text);
        Assert.Contains("0.90", _file.Text);
        var after = Reload();
        Assert.Equal(before.Currency, after.Currency);
        Assert.Equal(before.Fields.Select(f => f.Name), after.Fields.Select(f => f.Name));
        Assert.Equal(before.Rounding.Mode, after.Rounding.Mode);
    }

    [Fact]
    public void UpdateReplacesRuleAndCanRenameIt()
    {
        var rule = JsonNode.Parse("""{ "id": "rate-condo-2", "kind": "rating", "priority": 10, "basePremium": 125 }""")!.AsObject();

        var change = _store.Update(_store.Read().Etag, "v2", "rate-condo", rule);

        Assert.IsType<RuleFileChange.Saved>(change);
        var ruleSet = Reload();
        Assert.DoesNotContain(ruleSet.Rules, r => r.Id == "rate-condo");
        Assert.Equal(125m, Assert.Single(ruleSet.Rules, r => r.Id == "rate-condo-2").BasePremium);
    }

    [Fact]
    public void DeleteRemovesRule()
    {
        var change = _store.Delete(_store.Read().Etag, "v2", "elig-mobile-home");

        Assert.IsType<RuleFileChange.Saved>(change);
        Assert.DoesNotContain(Reload().Rules, r => r.Id == "elig-mobile-home");
    }

    [Fact]
    public void CreateWithExistingIdIsRefused()
    {
        var change = _store.Create(_store.Read().Etag, "v2", EligibilityRule("elig-claims"));

        Assert.Equal("elig-claims", Assert.IsType<RuleFileChange.DuplicateId>(change).RuleId);
    }

    [Fact]
    public void RenameToExistingIdIsRefused()
    {
        var change = _store.Update(_store.Read().Etag, "v2", "elig-year-built", EligibilityRule("elig-claims"));

        Assert.IsType<RuleFileChange.DuplicateId>(change);
    }

    [Fact]
    public void ChangingMissingRuleIsNotFound()
    {
        var etag = _store.Read().Etag;

        Assert.IsType<RuleFileChange.NotFound>(_store.Update(etag, "v2", "nope", EligibilityRule()));
        Assert.IsType<RuleFileChange.NotFound>(_store.Delete(etag, "v2", "nope"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingVersionIsRefused(string? version)
    {
        var before = _file.Text;

        var change = _store.Create(_store.Read().Etag, version, EligibilityRule());

        Assert.IsType<RuleFileChange.InvalidVersion>(change);
        Assert.Equal(before, _file.Text);
    }

    [Fact]
    public void UnchangedVersionIsRefused()
    {
        var before = _file.Text;

        var change = _store.Create(_store.Read().Etag, ShippedRules.Version, EligibilityRule());

        Assert.IsType<RuleFileChange.InvalidVersion>(change);
        Assert.Equal(before, _file.Text);
    }

    [Fact]
    public void StaleEtagIsRefused()
    {
        var etag = _store.Read().Etag;
        Assert.IsType<RuleFileChange.Saved>(_store.Create(etag, "v2", EligibilityRule("first")));

        var change = _store.Create(etag, "v3", EligibilityRule("second"));

        Assert.IsType<RuleFileChange.Stale>(change);
        Assert.DoesNotContain(Reload().Rules, r => r.Id == "second");
    }

    [Fact]
    public void InvalidChangeIsNotWritten()
    {
        var before = _file.Text;
        var rule = JsonNode.Parse("""{ "id": "elig-bad", "kind": "eligibility", "priority": 1, "reject": { "code": "X", "message": "x" } }""")!.AsObject();

        var change = _store.Create(_store.Read().Etag, "v2", rule);

        var invalid = Assert.IsType<RuleFileChange.Invalid>(change);
        Assert.Contains(invalid.Errors, e => e.Contains("at least one condition", StringComparison.Ordinal));
        Assert.Equal(before, _file.Text);
    }

    [Fact]
    public void UnknownPropertyIsNotWritten()
    {
        var rule = EligibilityRule();
        rule["prioirty"] = 3;

        Assert.IsType<RuleFileChange.Invalid>(_store.Create(_store.Read().Etag, "v2", rule));
    }

    [Fact]
    public void DeletingLastRatingRuleIsRefused()
    {
        Assert.IsType<RuleFileChange.Saved>(_store.Delete(_store.Read().Etag, "v2", "rate-high-value"));
        Assert.IsType<RuleFileChange.Saved>(_store.Delete(_store.Read().Etag, "v3", "rate-standard"));

        var change = _store.Delete(_store.Read().Etag, "v4", "rate-condo");

        var invalid = Assert.IsType<RuleFileChange.Invalid>(change);
        Assert.Contains(invalid.Errors, e => e.Contains("At least one rating rule", StringComparison.Ordinal));
        Assert.Contains(Reload().Rules, r => r.Id == "rate-condo");
    }

    [Fact]
    public void RuleWithoutIdIsRefused()
    {
        var rule = EligibilityRule();
        rule.Remove("id");

        Assert.IsType<RuleFileChange.InvalidRequest>(_store.Create(_store.Read().Etag, "v2", rule));
        Assert.IsType<RuleFileChange.InvalidRequest>(_store.Create(_store.Read().Etag, "v2", null));
    }

    [Fact]
    public void InvalidFileCannotBeEdited()
    {
        File.WriteAllText(_file.Path, """{ "version": "v1" }""");
        var snapshot = _store.Read();

        Assert.False(snapshot.CanEdit);
        Assert.IsType<RuleFileChange.Unavailable>(_store.Create(snapshot.Etag, "v2", EligibilityRule()));
    }

    [Fact]
    public void MissingFileCannotBeEdited()
    {
        var store = new RuleFileStore(Path.Combine(Path.GetDirectoryName(_file.Path)!, "missing.json"));

        var snapshot = store.Read();

        Assert.False(snapshot.CanEdit);
        Assert.Null(snapshot.Etag);
        Assert.NotEmpty(snapshot.Load.Errors);
    }
}
