using System.Text.Json.Nodes;

namespace QuoteEstimator.RulesAdmin.Models;

/// <param name="Etag">The <c>etag</c> from <c>GET /api/rules</c> that the edit was based on.</param>
/// <param name="Version">The new rule set version to write with this change.</param>
/// <param name="Rule">The rule as it should appear in the rule file; <see cref="RuleSetLoader"/> validates it.</param>
public sealed record RuleSaveRequest(string? Etag, string? Version, JsonObject? Rule);

public sealed record RuleDeleteRequest(string? Etag, string? Version);
