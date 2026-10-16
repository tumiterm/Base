using Base.Infrastructure.Identity;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Base.Web.Controllers;

[Route("account")]
public sealed partial class AccountController(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    TimeProvider time,
    ILogger<AccountController> logger) : Controller
{
    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationExtensions.AuthRateLimitPolicy)]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Users are tenant-scoped, so this lookup only sees accounts of the resolved tenant.
        var result = await signInManager.PasswordSignInAsync(model.Email, model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            var user = await userManager.FindByNameAsync(model.Email);
            if (user is not null)
            {
                user.LastSignInAt = time.GetUtcNow();
                await userManager.UpdateAsync(user);
            }

            return LocalRedirect(Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl : "/");
        }

        if (result.IsLockedOut)
        {
            LogLockedOut(logger);
        }

        // One message for every failure, so the form can't be used to discover accounts.
        ModelState.AddModelError(string.Empty, "Invalid email or password, or the account is locked.");
        return View(model);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return RedirectToAction(nameof(HomeController.Index), "Home");
    }

    [AllowAnonymous]
    [HttpGet("access-denied")]
    public IActionResult AccessDenied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("~/Views/Shared/Error.cshtml", new ErrorViewModel(StatusCodes.Status403Forbidden, null));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in rejected: account locked out")]
    private static partial void LogLockedOut(ILogger logger);
}
