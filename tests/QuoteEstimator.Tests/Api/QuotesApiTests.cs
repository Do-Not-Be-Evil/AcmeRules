using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using QuoteEstimator.Web.Rules;

namespace QuoteEstimator.Tests.Api;

public sealed class QuoteAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Auth:UseDevAuth", "true");
        builder.UseSetting("RateLimit:PermitLimit", "1000");
    }
}

public partial class QuotesApiTests(QuoteAppFactory factory) : IClassFixture<QuoteAppFactory>
{
    private static readonly object ValidInput = new
    {
        propertyType = "singleFamily",
        dwellingCoverage = 300000,
        deductible = "1000",
        constructionType = "masonry",
        yearBuilt = 1995,
        priorClaims = 0,
    };

    [GeneratedRegex("""name="__RequestVerificationToken" type="hidden" value="([^"]+)" """)]
    private static partial Regex AntiforgeryTokenPattern();

    private static HttpClient NewClient(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> GetAntiforgeryToken(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = AntiforgeryTokenPattern().Match(html);
        Assert.True(match.Success, $"No antiforgery token found on {path}.");
        return match.Groups[1].Value;
    }

    /// <summary>Signs in through the dev login page and returns a client ready to call the quote API.</summary>
    private static async Task<HttpClient> SignedInClient(WebApplicationFactory<Program> app, string role = "Estimator")
    {
        var client = NewClient(app);
        var loginToken = await GetAntiforgeryToken(client, "/dev/login");
        var login = await client.PostAsync("/dev/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = $"test-{role}",
            ["role"] = role,
            ["__RequestVerificationToken"] = loginToken,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        if (role == "Estimator")
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await GetAntiforgeryToken(client, "/quotes"));
        return client;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task AuthorizedEstimatorReceivesQuote()
    {
        using var client = await SignedInClient(factory);

        var response = await client.PostAsJsonAsync("/api/quotes", ValidInput);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(1140.00m, body.GetProperty("premium").GetDecimal());
        Assert.Equal("USD", body.GetProperty("currency").GetString());
        Assert.Equal("rate-standard", body.GetProperty("appliedRuleIds")[0].GetString());
        Assert.Equal(ShippedRules.Version, body.GetProperty("ruleSetVersion").GetString());
    }

    [Fact]
    public async Task UnauthenticatedRequestIsRejectedWithoutQuoteData()
    {
        using var client = NewClient(factory);

        var response = await client.PostAsJsonAsync("/api/quotes", ValidInput);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UserWithoutEstimatorRoleIsForbiddenWithoutQuoteData()
    {
        using var client = await SignedInClient(factory, role: "Viewer");

        var response = await client.PostAsJsonAsync("/api/quotes", ValidInput);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnauthenticatedPageRequestRedirectsToSignIn()
    {
        using var client = NewClient(factory);

        var response = await client.GetAsync("/quotes");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/dev/login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task RequestWithoutAntiforgeryTokenIsRejected()
    {
        using var client = await SignedInClient(factory);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        var response = await client.PostAsJsonAsync("/api/quotes", ValidInput);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("premium", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingFieldsReturnFieldLevelErrors()
    {
        using var client = await SignedInClient(factory);

        var response = await client.PostAsJsonAsync("/api/quotes", new { propertyType = "condo" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("FIELD_ERRORS", body.GetProperty("code").GetString());
        var fields = body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order();
        Assert.Equal(["constructionType", "deductible", "dwellingCoverage", "priorClaims", "yearBuilt"], fields);
        Assert.False(body.TryGetProperty("premium", out _));
    }

    [Fact]
    public async Task MalformedBodyIsRejected()
    {
        using var client = await SignedInClient(factory);

        var response = await client.PostAsync("/api/quotes", new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RuleViolationReturnsItsCodeAndNoQuote()
    {
        using var client = await SignedInClient(factory);

        var response = await client.PostAsJsonAsync("/api/quotes", new
        {
            propertyType = "mobileHome",
            dwellingCoverage = 80000,
            deductible = "500",
            constructionType = "frame",
            yearBuilt = 2000,
            priorClaims = 0,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("MOBILE_HOME_INELIGIBLE", body.GetProperty("violations")[0].GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("premium", out _));
    }

    [Fact]
    public async Task MissingRuleConfigurationReturnsUnavailable()
    {
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IRuleSetProvider>(new StubProvider(() => null))));
        using var client = await SignedInClient(app);

        var response = await client.PostAsJsonAsync("/api/quotes", ValidInput);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("GENERATION_UNAVAILABLE", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task EmptyRuleFileReturnsUnavailable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        try
        {
            using var app = factory.WithWebHostBuilder(b => b.UseSetting("Rules:Path", path));
            using var client = await SignedInClient(app);

            var response = await client.PostAsJsonAsync("/api/quotes", ValidInput);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnexpectedFailureReturnsServerErrorWithoutQuote()
    {
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IRuleSetProvider>(new StubProvider(() => throw new InvalidOperationException("boom")))));
        using var client = NewClient(app);
        var loginToken = await GetAntiforgeryToken(client, "/dev/login");
        await client.PostAsync("/dev/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "test", ["role"] = "Estimator", ["__RequestVerificationToken"] = loginToken,
        }));
        // The quote page also reads the rule set, so take the API token from the login page instead;
        // antiforgery tokens are tied to the signed-in user, so fetch a fresh one now.
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await GetAntiforgeryToken(client, "/dev/login"));

        var response = await client.PostAsJsonAsync("/api/quotes", ValidInput);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("premium", content);
        Assert.DoesNotContain("boom", content);
    }

    [Fact]
    public async Task RateLimitedRequestCarriesRetryAfter()
    {
        using var app = factory.WithWebHostBuilder(b => b.UseSetting("RateLimit:PermitLimit", "2"));
        using var client = await SignedInClient(app);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/quotes", ValidInput);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) limited = response;
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.NotNull(limited!.Headers.RetryAfter);
        Assert.DoesNotContain("premium", await limited.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task QuotePageRendersFieldsFromRuleSet()
    {
        using var client = await SignedInClient(factory);

        var html = await client.GetStringAsync("/quotes");

        Assert.Contains("id=\"quote-form\"", html);
        Assert.Contains("id=\"field-dwellingCoverage\"", html);
        Assert.Contains("min=\"50000\"", html);
    }

    [Fact]
    public async Task DevLoginIsDisabledUnlessConfigured()
    {
        using var app = factory.WithWebHostBuilder(b => b.UseSetting("Auth:UseDevAuth", "false"));
        using var client = NewClient(app);

        var response = await client.GetAsync("/dev/login");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class StubProvider(Func<RuleSet?> get) : IRuleSetProvider
    {
        public RuleSet? GetCurrent() => get();
    }
}
