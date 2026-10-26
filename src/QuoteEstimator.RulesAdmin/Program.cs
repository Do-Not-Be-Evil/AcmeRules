using Microsoft.Extensions.Options;
using QuoteEstimator.RulesAdmin.Auth;
using QuoteEstimator.RulesAdmin.Rules;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddProblemDetails();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

builder.Services.Configure<RulesOptions>(builder.Configuration.GetSection(RulesOptions.Section));
builder.Services.AddSingleton(sp => new RuleFileStore(
    Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, sp.GetRequiredService<IOptions<RulesOptions>>().Value.Path)));

builder.Services.AddRulesAdminAuth();

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
