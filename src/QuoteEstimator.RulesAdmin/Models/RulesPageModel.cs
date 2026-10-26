using QuoteEstimator.RulesAdmin.Rules;

namespace QuoteEstimator.RulesAdmin.Models;

/// <param name="FilePath">Full path of the rule file being edited.</param>
/// <param name="SavedMessage">Confirmation from the previous change, if any.</param>
public sealed record RulesPageModel(RuleFileSnapshot Snapshot, string FilePath, string? SavedMessage);

/// <param name="RuleId">The rule being edited, or null for a new rule.</param>
public sealed record RuleEditPageModel(string? RuleId);
