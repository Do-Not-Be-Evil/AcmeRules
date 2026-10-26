using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuoteEstimator.RulesAdmin.Auth;

namespace QuoteEstimator.RulesAdmin.Controllers;

/// <summary>
/// Development-only stand-in for an identity provider. Returns 404 unless Auth:UseDevAuth is true.
/// </summary>
[AllowAnonymous]
[Route("dev")]
public sealed class DevLoginController(IConfiguration configuration) : Controller
{
    public static readonly string[] Roles = [AuthPolicies.RulesAdminRole, "Viewer"];

    private bool Enabled => configuration.GetValue<bool>(AuthSetup.UseDevAuthKey);

    [HttpGet("login")]
    public IActionResult Login(string? returnUrl)
    {
        if (!Enabled) return NotFound();
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string? name, string? role, string? returnUrl)
    {
        if (!Enabled) return NotFound();
        if (string.IsNullOrWhiteSpace(name) || role is null || !Roles.Contains(role))
        {
            ModelState.AddModelError("", "Enter a name and choose a role.");
            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name.Trim()), new Claim(ClaimTypes.Role, role)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/rules");
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Enabled ? LocalRedirect("/dev/login") : LocalRedirect("/");
    }
}
