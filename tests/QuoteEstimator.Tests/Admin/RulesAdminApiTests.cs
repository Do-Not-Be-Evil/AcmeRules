extern alias RulesAdmin;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RulesAdmin::QuoteEstimator.RulesAdmin.Controllers;

namespace QuoteEstimator.Tests.Admin;

/// <summary>
/// Hosts the rules admin app. RulesController marks its assembly because both apps have a
/// generated Program class, and the admin project is referenced through an extern alias.
/// </summary>
public sealed class RulesAdminFactory : WebApplicationFactory<RulesController>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Auth:UseDevAuth", "true");
    }
}

public sealed partial class RulesAdminApiTests(RulesAdminFactory factory) : IClassFixture<RulesAdminFactory>, IDisposable
{
    private readonly TempRuleFile _file = new();

    public void Dispose() => _file.Dispose();

    private static readonly object NewEligibilityRule = new
    {
        id = "elig-test",
        kind = "eligibility",
        priority = 50,
        when = new[] { new { field = "yearBuilt", op = "lt", value = 1920 } },
        reject = new { code = "OLD_HOME", message = "Too old." },
    };

    [GeneratedRegex("""name="__RequestVerificationToken" type="hidden" value="([^"]+)" """)]
    private static partial Regex AntiforgeryTokenPattern();

    private WebApplicationFactory<RulesController> App() =>
        factory.WithWebHostBuilder(b => b.UseSetting("Rules:Path", _file.Path));

    private static async Task<string> GetAntiforgeryToken(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = AntiforgeryTokenPattern().Match(html);
        Assert.True(match.Success, $"No antiforgery token found on {path}.");
        return match.Groups[1].Value;
    }

    private static async Task<HttpClient> SignedInClient(WebApplicationFactory<RulesController> app, string role = "RulesAdmin")
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginToken = await GetAntiforgeryToken(client, "/dev/login");
        var login = await client.PostAsync("/dev/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = $"test-{role}",
            ["role"] = role,
            ["__RequestVerificationToken"] = loginToken,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        if (role == "RulesAdmin")
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await GetAntiforgeryToken(client, "/rules"));
        return client;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> CurrentEtag(HttpClient client) =>
        (await ReadJson(await client.GetAsync("/api/rules"))).GetProperty("etag").GetString()!;

    private static HttpRequestMessage DeleteRequest(string id, object body) =>
        new(HttpMethod.Delete, $"/api/rules/{id}") { Content = JsonContent.Create(body) };

    [Fact]
    public async Task UnauthenticatedApiRequestIsRejected()
    {
        using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/rules");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnauthenticatedPageRequestRedirectsToSignIn()
    {
        using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/rules");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/dev/login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task UserWithoutRulesAdminRoleIsForbidden()
    {
        using var app = App();
        using var client = await SignedInClient(app, "Viewer");

        var response = await client.GetAsync("/api/rules");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChangeWithoutAntiforgeryTokenIsRejected()
    {
        using var app = App();
        using var client = await SignedInClient(app);
        var etag = await CurrentEtag(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var before = _file.Text;

        var response = await client.PostAsJsonAsync("/api/rules", new { etag, version = "v2", rule = NewEligibilityRule });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, _file.Text);
    }

    [Fact]
    public async Task CreateEditDeleteRoundTrip()
    {
        using var app = App();
        using var client = await SignedInClient(app);

        var created = await client.PostAsJsonAsync("/api/rules", new { etag = await CurrentEtag(client), version = "v2", rule = NewEligibilityRule });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdBody = await ReadJson(created);
        Assert.Equal("v2", createdBody.GetProperty("version").GetString());

        var listed = await ReadJson(await client.GetAsync("/api/rules"));
        Assert.Equal("v2", listed.GetProperty("version").GetString());
        Assert.Contains(listed.GetProperty("rules").EnumerateArray(), r => r.GetProperty("id").GetString() == "elig-test");

        var edited = await client.PutAsJsonAsync("/api/rules/elig-test", new
        {
            etag = createdBody.GetProperty("etag").GetString(),
            version = "v3",
            rule = new
            {
                id = "elig-test",
                kind = "eligibility",
                priority = 50,
                when = new[] { new { field = "yearBuilt", op = "lt", value = 1910 } },
                reject = new { code = "OLD_HOME", message = "Still too old." },
            },
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Contains("Still too old.", _file.Text);

        var deleted = await client.SendAsync(DeleteRequest("elig-test", new { etag = (await ReadJson(edited)).GetProperty("etag").GetString(), version = "v4" }));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.DoesNotContain("elig-test", _file.Text);
        Assert.Equal("v4", RuleSetLoader.Parse(_file.Text).RuleSet!.Version);
    }

    [Fact]
    public async Task InvalidRuleReturnsLoaderErrors()
    {
        using var app = App();
        using var client = await SignedInClient(app);

        var response = await client.PostAsJsonAsync("/api/rules", new
        {
            etag = await CurrentEtag(client),
            version = "v2",
            rule = new { id = "elig-bad", kind = "eligibility", priority = 1, reject = new { code = "X", message = "x" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("INVALID_RULES", body.GetProperty("code").GetString());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("at least one condition"));
    }

    [Fact]
    public async Task UnchangedVersionIsUnprocessable()
    {
        using var app = App();
        using var client = await SignedInClient(app);

        var response = await client.PostAsJsonAsync("/api/rules", new
        {
            etag = await CurrentEtag(client),
            version = ShippedRules.Version,
            rule = NewEligibilityRule,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("INVALID_VERSION", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task StaleEtagIsConflict()
    {
        using var app = App();
        using var client = await SignedInClient(app);

        var response = await client.PostAsJsonAsync("/api/rules", new { etag = "stale", version = "v2", rule = NewEligibilityRule });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("STALE", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task DuplicateIdIsConflict()
    {
        using var app = App();
        using var client = await SignedInClient(app);

        var response = await client.PostAsJsonAsync("/api/rules", new
        {
            etag = await CurrentEtag(client),
            version = "v2",
            rule = new { id = "elig-claims", kind = "eligibility", priority = 1, when = new[] { new { field = "priorClaims", op = "gte", value = 9 } }, reject = new { code = "X", message = "x" } },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("DUPLICATE_ID", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeletingMissingRuleIsNotFound()
    {
        using var app = App();
        using var client = await SignedInClient(app);

        var response = await client.SendAsync(DeleteRequest("nope", new { etag = await CurrentEtag(client), version = "v2" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("RULE_NOT_FOUND", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidRuleFileIsUnavailable()
    {
        File.WriteAllText(_file.Path, "{ not json");
        using var app = App();
        using var client = await SignedInClient(app);

        var response = await client.GetAsync("/api/rules");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("RULES_UNAVAILABLE", (await ReadJson(response)).GetProperty("code").GetString());
        Assert.Contains("cannot be edited", await client.GetStringAsync("/rules"));
    }

    [Fact]
    public async Task PagesRenderRulesAndEditor()
    {
        using var app = App();
        using var client = await SignedInClient(app);

        var index = await client.GetStringAsync("/rules");
        Assert.Contains("rate-condo", index);
        Assert.Contains(ShippedRules.Version, index);

        Assert.Contains("data-rule-id=\"rate-condo\"", await client.GetStringAsync("/rules/rate-condo/edit"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/rules/nope/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/rules/new")).StatusCode);
    }

    [Fact]
    public async Task DevLoginIsDisabledUnlessConfigured()
    {
        using var app = factory.WithWebHostBuilder(b => b
            .UseSetting("Rules:Path", _file.Path)
            .UseSetting("Auth:UseDevAuth", "false"));
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/login")).StatusCode);
    }
}
