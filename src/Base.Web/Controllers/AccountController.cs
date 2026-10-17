using Base.Application.Settings;
using Base.Application.Users;
using Base.Infrastructure.Identity;
using Base.Web.Results;
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
    IAccountService accounts,
    IApplicationSettingService settings,
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
    [HttpGet("forgot-password")]
    public async Task<IActionResult> ForgotPassword(CancellationToken cancellationToken) =>
        await settings.GetBoolAsync(SettingCodes.Security.PasswordResetEnabled, cancellationToken) ? View(new ForgotPasswordViewModel()) : NotFound();

    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationExtensions.AuthRateLimitPolicy)]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Same answer whether or not the account exists.
        this.Flash(await accounts.RequestPasswordResetAsync(model.Email, cancellationToken));
        return RedirectToAction(nameof(Login));
    }

    /// <summary>Target of invite and reset emails.</summary>
    [AllowAnonymous]
    [HttpGet("set-password")]
    public IActionResult SetPassword(Guid userId, string token, PasswordTokenKind kind = PasswordTokenKind.Reset)
    {
        // The token is in the URL: keep the page out of caches.
        Response.Headers.CacheControl = "no-store";
        return View(new SetPasswordViewModel { UserId = userId, Token = token, Kind = kind });
    }

    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationExtensions.AuthRateLimitPolicy)]
    [HttpPost("set-password")]
    public async Task<IActionResult> SetPassword(SetPasswordViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        Response.Headers.CacheControl = "no-store";
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await accounts.SetPasswordAsync(
            new SetPasswordRequest(model.UserId, model.Token, model.Kind, model.NewPassword, model.ConfirmPassword), cancellationToken);
        if (result.IsSuccess)
        {
            this.Flash(result);
            return RedirectToAction(nameof(Login));
        }

        result.AddToModelState(ModelState);
        return View(model);
    }

    [HttpGet("password")]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await accounts.ChangePasswordAsync(new ChangePasswordRequest(model.CurrentPassword, model.NewPassword, model.ConfirmPassword), cancellationToken);
        if (!result.IsSuccess)
        {
            result.AddToModelState(ModelState);
            return View(model);
        }

        // The password change rotated the security stamp; re-issue this browser's cookie so it stays signed in.
        // Other sessions are signed out at their next principal refresh.
        if (await userManager.GetUserAsync(User) is { } user)
        {
            await signInManager.RefreshSignInAsync(user);
        }

        this.Flash(result);
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
