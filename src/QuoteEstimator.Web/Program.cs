using Microsoft.Extensions.Options;
using QuoteEstimator.Web;
using QuoteEstimator.Web.Auth;
using QuoteEstimator.Web.Rules;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddProblemDetails();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

builder.Services.Configure<RulesOptions>(builder.Configuration.GetSection(RulesOptions.Section));
builder.Services.AddSingleton<IRuleSetProvider>(sp => new JsonRuleSetProvider(
    Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, sp.GetRequiredService<IOptions<RulesOptions>>().Value.Path),
    sp.GetRequiredService<ILogger<JsonRuleSetProvider>>()));
builder.Services.AddSingleton<QuoteEvaluator>();

builder.Services.AddEstimatorAuth();
builder.Services.AddQuoteRateLimiting(builder.Configuration);

var app = builder.Build();

// Unhandled errors become a generic problem response; no partial quote data is ever returned.
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.Run();

public partial class Program;
