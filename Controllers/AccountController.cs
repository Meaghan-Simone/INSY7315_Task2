using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

public class AccountController : AppController
{
    private readonly AuthService _auth;
    private readonly UserService _users;
    private readonly ICurrentUser _me;
    private readonly IAuditService _audit;

    public AccountController(AuthService auth, UserService users, ICurrentUser me, IAuditService audit)
    { _auth = auth; _users = users; _me = me; _audit = audit; }

    private async Task SignInAsync(Domain.User user, bool persistent)
    {
        var principal = ClaimsFactory.Create(user, AuthSchemes.Cookie);
        await HttpContext.SignInAsync(AuthSchemes.Cookie, principal, new AuthenticationProperties
        {
            IsPersistent = persistent,
            ExpiresUtc = persistent ? DateTimeOffset.UtcNow.AddDays(7) : null,
            AllowRefresh = true
        });
    }

    // ---------------------------------------------------------------- sign in / out
    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginInput());
    }

    [AllowAnonymous, HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginInput input, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        if (!ModelState.IsValid) return View(input);
        var result = await _auth.AuthenticateAsync(input.Email, input.Password);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Invalid email or password.");
            input.Password = "";
            return View(input);
        }
        await SignInAsync(result.Value!, input.RememberMe);
        if (result.Value!.MustChangePassword) return RedirectToAction(nameof(ChangePassword));
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Home");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await _audit.LogAsync("Logout", "User", _me.Id);
        await HttpContext.SignOutAsync(AuthSchemes.Cookie);
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    // ---------------------------------------------------------------- forgot / reset
    [AllowAnonymous, HttpGet]
    public IActionResult ForgotPassword() => View(new ForgotPasswordInput());

    [AllowAnonymous, HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordInput input)
    {
        if (!ModelState.IsValid) return View(input);
        await _auth.RequestPasswordResetAsync(input.Email);
        ViewData["Sent"] = true; // identical response whether or not the account exists
        return View(new ForgotPasswordInput());
    }

    [AllowAnonymous, HttpGet]
    public async Task<IActionResult> ResetPassword(string? token)
    {
        if (string.IsNullOrEmpty(token) || !(await _auth.ResolveResetTokenAsync(token)).Succeeded) return View("ResetInvalid");
        return View(new ResetPasswordInput { Token = token });
    }

    [AllowAnonymous, HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword(ResetPasswordInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var r = await _auth.ResetPasswordAsync(input.Token, input.NewPassword);
        if (!r.Succeeded)
        {
            if (r.Errors == null) return View("ResetInvalid");
            AddErrors(r); return View(input);
        }
        Flash("Your password has been updated. Please sign in.");
        return RedirectToAction(nameof(Login));
    }

    // ---------------------------------------------------------------- signed-in account pages
    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordInput());

    [HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword(ChangePasswordInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var r = await _auth.ChangePasswordAsync(_me.Id, input.CurrentPassword, input.NewPassword);
        if (!r.Succeeded) { AddErrors(r); return View(input); }
        await SignInAsync(r.Value!, false); // new security stamp: refresh this session, all others are signed out
        Flash("Password changed. Any other devices have been signed out.");
        return RedirectToAction(nameof(Profile));
    }

    [HttpGet]
    public IActionResult Profile() => View(new ProfileInput { FullName = _me.Name });

    [HttpPost]
    public async Task<IActionResult> Profile(ProfileInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var r = await _users.UpdateProfileAsync(input.FullName);
        if (!r.Succeeded) { AddErrors(r); return View(input); }
        var user = await _auth.GetActiveUserAsync(_me.Id, null);
        if (user.Succeeded) await SignInAsync(user.Value!, false);
        Flash("Profile updated.");
        return RedirectToAction(nameof(Profile));
    }
}
