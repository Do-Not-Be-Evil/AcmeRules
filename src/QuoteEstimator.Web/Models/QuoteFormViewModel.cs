using QuoteEstimator.Web.Rules;

namespace QuoteEstimator.Web.Models;

/// <param name="RuleSet">Null when no valid rule configuration is loaded.</param>
public sealed record QuoteFormViewModel(RuleSet? RuleSet);
