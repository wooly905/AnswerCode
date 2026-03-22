using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;

namespace AnswerCode.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IWebHostEnvironment _env;

    public AuthController(IWebHostEnvironment env)
    {
        _env = env;
    }

    /// <summary>
    /// Initiate Google OAuth login. Redirects to Google's consent screen.
    /// </summary>
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl = "/")
    {
        if (string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl))
            returnUrl = "/";

        var properties = new AuthenticationProperties { RedirectUri = returnUrl };
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Development-only: create a fake auth cookie without Google OAuth.
    /// </summary>
    [HttpGet("dev-login")]
    public async Task<IActionResult> DevLogin([FromQuery] string? returnUrl = "/")
    {
        if (!_env.IsDevelopment())
            return NotFound();

        if (string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl))
            returnUrl = "/";

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Dev User"),
            new(ClaimTypes.Email, "dev@localhost"),
            new("picture", "")
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });

        return Redirect(returnUrl);
    }

    /// <summary>
    /// Logout: clear the auth cookie and redirect to home.
    /// </summary>
    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/");
    }

    /// <summary>
    /// Get current user info. Returns 401 if not authenticated.
    /// </summary>
    [HttpGet("me")]
    public IActionResult GetCurrentUser()
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { authenticated = false });

        return Ok(new
        {
            authenticated = true,
            name = User.FindFirst(ClaimTypes.Name)?.Value,
            email = User.FindFirst(ClaimTypes.Email)?.Value,
            picture = User.FindFirst("picture")?.Value
        });
    }
}
