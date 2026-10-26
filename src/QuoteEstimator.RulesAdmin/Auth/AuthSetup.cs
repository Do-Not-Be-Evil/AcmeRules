using Microsoft.AspNetCore.Authentication.Cookies;

namespace QuoteEstimator.RulesAdmin.Auth;

public static class AuthPolicies
{
    public const string RulesAdmin = "RulesAdmin";
    public const string RulesAdminRole = "RulesAdmin";
}

public static class AuthSetup
{
    /// <summary>When true, /dev/login can sign anyone in with any role. Never enable outside Development.</summary>
    public const string UseDevAuthKey = "Auth:UseDevAuth";

    /// <summary>
    /// Cookie authentication with a RulesAdmin role policy. No identity provider exists yet: in
    /// Development the cookie is issued by /dev/login; elsewhere nothing issues it, so every
    /// request is rejected until a real identity provider is connected.
    /// </summary>
    public static IServiceCollection AddRulesAdminAuth(this IServiceCollection services)
    {
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = ".QuoteEstimator.RulesAdmin.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.LoginPath = "/dev/login";
                options.AccessDeniedPath = "/dev/login";

                // API callers get status codes, never a redirect or any rule data.
                options.Events.OnRedirectToLogin = context => RejectApiOrRedirect(context, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => RejectApiOrRedirect(context, StatusCodes.Status403Forbidden);
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.RulesAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(AuthPolicies.RulesAdminRole));

        return services;
    }

    private static Task RejectApiOrRedirect(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.StatusCode = statusCode;
        else
            context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}
