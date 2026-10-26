using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using QuoteEstimator.Web.Auth;
using QuoteEstimator.Web.Models;
using QuoteEstimator.Web.Rules;

namespace QuoteEstimator.Web.Controllers;

[Authorize(Policy = AuthPolicies.Estimator)]
public sealed class QuotesController(IRuleSetProvider rules, QuoteEvaluator evaluator) : Controller
{
    [HttpGet("/")]
    [HttpGet("/quotes")]
    public IActionResult Index() => View(new QuoteFormViewModel(rules.GetCurrent()));

    [HttpPost("/api/quotes")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.QuotesPolicy)]
    public IActionResult Generate([FromBody] Dictionary<string, JsonElement>? inputs)
    {
        if (inputs is null)
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Request body must be a JSON object of quote inputs.");

        // One snapshot for the whole request: a concurrent reload cannot mix rule versions.
        var ruleSet = rules.GetCurrent();

        return evaluator.Evaluate(ruleSet, inputs) switch
        {
            EvaluationResult.Success s => Ok(new
            {
                premium = s.Premium,
                currency = s.Currency,
                appliedRuleIds = s.AppliedRuleIds,
                ruleSetVersion = s.RuleSetVersion,
            }),

            EvaluationResult.FieldErrors e => FieldErrorsResult(e),

            EvaluationResult.RuleViolations v => ProblemWith(
                StatusCodes.Status422UnprocessableEntity,
                "The inputs do not satisfy the quote rules.",
                "RULE_VIOLATION",
                v.RuleSetVersion,
                ("violations", v.Violations.Select(x => new { ruleId = x.RuleId, code = x.Code, message = x.Message }))),

            EvaluationResult.NoApplicableRule n => ProblemWith(
                StatusCodes.Status422UnprocessableEntity,
                "No quote rule applies to these inputs.",
                "NO_APPLICABLE_RULE",
                n.RuleSetVersion,
                ("violations", new[] { new { ruleId = (string?)null, code = "NO_APPLICABLE_RULE", message = "No quote rule applies to these inputs. Contact underwriting." } })),

            EvaluationResult.Conflict c => ProblemWith(
                StatusCodes.Status409Conflict,
                "More than one quote rule applies with the same priority, so no quote can be produced.",
                "RULE_CONFLICT",
                c.RuleSetVersion,
                ("ruleIds", c.RuleIds)),

            EvaluationResult.Unavailable => ProblemWith(
                StatusCodes.Status503ServiceUnavailable,
                "Quote generation is currently unavailable.",
                "GENERATION_UNAVAILABLE",
                ruleSetVersion: null),

            var other => throw new InvalidOperationException($"Unhandled evaluation result {other.GetType().Name}."),
        };
    }

    private ObjectResult FieldErrorsResult(EvaluationResult.FieldErrors e)
    {
        var problem = new ValidationProblemDetails(e.Errors.ToDictionary(kv => kv.Key, kv => kv.Value))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Some quote inputs are missing or invalid.",
        };
        problem.Extensions["code"] = "FIELD_ERRORS";
        problem.Extensions["ruleSetVersion"] = e.RuleSetVersion;
        return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { "application/problem+json" } };
    }

    private static ObjectResult ProblemWith(
        int status, string title, string code, string? ruleSetVersion, params (string Key, object Value)[] extensions)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        if (ruleSetVersion is not null)
            problem.Extensions["ruleSetVersion"] = ruleSetVersion;
        foreach (var (key, value) in extensions)
            problem.Extensions[key] = value;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
