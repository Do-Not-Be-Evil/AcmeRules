using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuoteEstimator.RulesAdmin.Auth;
using QuoteEstimator.RulesAdmin.Models;
using QuoteEstimator.RulesAdmin.Rules;

namespace QuoteEstimator.RulesAdmin.Controllers;

[Authorize(Policy = AuthPolicies.RulesAdmin)]
public sealed class RulesController(RuleFileStore store) : Controller
{
    [HttpGet("/")]
    [HttpGet("/rules")]
    public IActionResult Index(string? saved)
    {
        var snapshot = store.Read();
        // Only confirm a save that the file actually reflects, so the message cannot be spoofed by a link.
        var message = saved is not null && saved == snapshot.Load.RuleSet?.Version
            ? $"Saved. The rule set is now version {saved}."
            : null;
        return View(new RulesPageModel(snapshot, store.Path, message));
    }

    [HttpGet("/rules/new")]
    public IActionResult New() => View("Edit", new RuleEditPageModel(null));

    [HttpGet("/rules/{id}/edit")]
    public IActionResult Edit(string id)
    {
        var snapshot = store.Read();
        if (snapshot.Load.RuleSet is { } ruleSet && !ruleSet.Rules.Any(r => r.Id == id))
            return NotFound();
        return View(new RuleEditPageModel(id));
    }

    /// <summary>The raw rule file content the editor works on, plus the ETag every change must send back.</summary>
    [HttpGet("/api/rules")]
    public IActionResult Get()
    {
        var snapshot = store.Read();
        if (!snapshot.CanEdit)
            return ProblemWith(
                StatusCodes.Status503ServiceUnavailable,
                "The rule file cannot be edited because it is missing or invalid.",
                "RULES_UNAVAILABLE",
                ("errors", snapshot.Load.Errors));

        var document = snapshot.Document!;
        return Ok(new
        {
            etag = snapshot.Etag,
            version = snapshot.Load.RuleSet!.Version,
            currency = snapshot.Load.RuleSet.Currency,
            fields = document["fields"],
            rules = document["rules"],
        });
    }

    [HttpPost("/api/rules")]
    [ValidateAntiForgeryToken]
    public IActionResult Create([FromBody] RuleSaveRequest? request) =>
        request is null
            ? InvalidRequest()
            : ToResult(store.Create(request.Etag, request.Version, request.Rule));

    [HttpPut("/api/rules/{id}")]
    [ValidateAntiForgeryToken]
    public IActionResult Update(string id, [FromBody] RuleSaveRequest? request) =>
        request is null
            ? InvalidRequest()
            : ToResult(store.Update(request.Etag, request.Version, id, request.Rule));

    [HttpDelete("/api/rules/{id}")]
    [ValidateAntiForgeryToken]
    public IActionResult Delete(string id, [FromBody] RuleDeleteRequest? request) =>
        request is null
            ? InvalidRequest()
            : ToResult(store.Delete(request.Etag, request.Version, id));

    private IActionResult ToResult(RuleFileChange change) => change switch
    {
        RuleFileChange.Saved s => Ok(new { version = s.Version, etag = s.Etag }),

        RuleFileChange.InvalidRequest r => ProblemWith(
            StatusCodes.Status400BadRequest, r.Message, "INVALID_REQUEST"),

        RuleFileChange.Invalid i => ProblemWith(
            StatusCodes.Status400BadRequest,
            "The change would make the rule configuration invalid, so it was not saved.",
            "INVALID_RULES",
            ("errors", i.Errors)),

        RuleFileChange.NotFound n => ProblemWith(
            StatusCodes.Status404NotFound, $"Rule '{n.RuleId}' does not exist.", "RULE_NOT_FOUND"),

        RuleFileChange.DuplicateId d => ProblemWith(
            StatusCodes.Status409Conflict, $"A rule with id '{d.RuleId}' already exists.", "DUPLICATE_ID"),

        RuleFileChange.Stale => ProblemWith(
            StatusCodes.Status409Conflict,
            "The rule file changed after this page loaded. Reload to see the latest rules.",
            "STALE"),

        RuleFileChange.InvalidVersion v => ProblemWith(
            StatusCodes.Status422UnprocessableEntity, v.Message, "INVALID_VERSION"),

        RuleFileChange.Unavailable u => ProblemWith(
            StatusCodes.Status503ServiceUnavailable,
            "The rule file cannot be edited because it is missing or invalid.",
            "RULES_UNAVAILABLE",
            ("errors", u.Errors)),

        var other => throw new InvalidOperationException($"Unhandled change result {other.GetType().Name}."),
    };

    private static ObjectResult InvalidRequest() =>
        ProblemWith(StatusCodes.Status400BadRequest, "Request body must be a JSON object.", "INVALID_REQUEST");

    private static ObjectResult ProblemWith(int status, string title, string code, params (string Key, object Value)[] extensions)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        foreach (var (key, value) in extensions)
            problem.Extensions[key] = value;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
