using System.Security.Claims;
using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Admin.Web.Services;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>Sign in / out, first-admin setup, change password, forgot / reset password. Used by staff and by clients' portal users.</summary>
[ClientPortal]
public class AccountController : Controller
{
    private const string GenericLoginError = "Invalid email or password.";

    private readonly IAdminUserRepository _users;
    private readonly AdminAuthService _auth;
    private readonly IEmailSender _email;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AccountController> _logger;

    public AccountController(IAdminUserRepository users, AdminAuthService auth, IEmailSender email,
        IWebHostEnvironment environment, ILogger<AccountController> logger)
    {
        _users = users;
        _auth = auth;
        _email = email;
        _environment = environment;
        _logger = logger;
    }

    // ================= Sign in / out =================

    [AllowAnonymous]
    public async Task<IActionResult> Login(string? returnUrl = null)
    {
        if (await _users.CountAsync() == 0) return RedirectToAction(nameof(Setup));
        if (User.Identity?.IsAuthenticated == true) return LocalRedirectOrHome(returnUrl);
        return View(new LoginModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _users.GetByEmailAsync(model.Email);
        var now = DateTime.UtcNow;

        if (user is null || !user.IsActive)
        {
            _logger.LogWarning("Failed sign-in for {Email} (unknown or inactive account)", model.Email);
            ModelState.AddModelError(string.Empty, GenericLoginError);
            return View(model);
        }

        if (user.IsLockedOut(now))
        {
            _logger.LogWarning("Sign-in blocked for {Email} - locked until {Until}", user.Email, user.LockoutEndUtc);
            ModelState.AddModelError(string.Empty, "Too many failed attempts. Try again in 15 minutes, or use Forgot password.");
            return View(model);
        }

        if (!_auth.VerifyPassword(user, model.Password, out var needsRehash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= AdminAuthService.MaxFailedLogins)
            {
                user.LockoutEndUtc = now.Add(AdminAuthService.LockoutDuration);
                user.FailedLoginCount = 0;
                _logger.LogWarning("Account {Email} locked for 15 minutes after {Max} failed sign-ins", user.Email, AdminAuthService.MaxFailedLogins);
            }
            await _users.UpdateAsync(user);
            ModelState.AddModelError(string.Empty, GenericLoginError);
            return View(model);
        }

        if (needsRehash) user.PasswordHash = _auth.HashPassword(user, model.Password);
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginAtUtc = now;
        await _users.UpdateAsync(user);

        await _auth.SignInAsync(HttpContext, user, model.RememberMe);
        _logger.LogInformation("{Email} signed in", user.Email);

        return user.MustChangePassword
            ? RedirectToAction(nameof(ChangePassword))
            : LocalRedirectOrHome(model.ReturnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        TempData["Success"] = "You have been signed out.";
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public IActionResult AccessDenied() => RedirectToAction("Index", "Error", new { statusCode = 403 });

    // ================= First admin =================

    /// <summary>Only available while there are no admin accounts at all.</summary>
    [AllowAnonymous]
    public async Task<IActionResult> Setup()
    {
        if (await _users.CountAsync() > 0) return NotFound();
        return View(new SetupModel());
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Setup(SetupModel model)
    {
        if (await _users.CountAsync() > 0) return NotFound();

        if (AdminAuthService.CheckPasswordRules(model.Password, model.Email) is { } problem)
            ModelState.AddModelError(nameof(model.Password), problem);
        if (!ModelState.IsValid) return View(model);

        var user = new AdminUser
        {
            Email = model.Email.Trim().ToLowerInvariant(),
            DisplayName = model.DisplayName.Trim(),
            IsActive = true,
            LastLoginAtUtc = DateTime.UtcNow
        };
        user.PasswordHash = _auth.HashPassword(user, model.Password);
        await _users.AddAsync(user);

        _logger.LogInformation("First admin account created: {Email}", user.Email);
        await _auth.SignInAsync(HttpContext, user, rememberMe: false);
        TempData["Success"] = "Your admin account is ready.";
        return RedirectToAction("Index", "Home");
    }

    // ================= Change password =================

    public IActionResult ChangePassword() =>
        View(new ChangePasswordModel { MustChange = User.FindFirst(AdminAuthService.MustChangeClaim) is not null });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordModel model)
    {
        model.MustChange = User.FindFirst(AdminAuthService.MustChangeClaim) is not null;
        var userId = AdminAuthService.CurrentUserId(User);
        var user = userId is null ? null : await _users.GetByIdAsync(userId.Value);
        if (user is null) return RedirectToAction(nameof(Login));

        if (ModelState.IsValid && !_auth.VerifyPassword(user, model.CurrentPassword, out _))
            ModelState.AddModelError(nameof(model.CurrentPassword), "That is not your current password.");
        if (ModelState.IsValid && model.NewPassword == model.CurrentPassword)
            ModelState.AddModelError(nameof(model.NewPassword), "Choose a password you haven't been using.");
        if (ModelState.IsValid && AdminAuthService.CheckPasswordRules(model.NewPassword, user.Email) is { } problem)
            ModelState.AddModelError(nameof(model.NewPassword), problem);
        if (!ModelState.IsValid) return View(model);

        user.PasswordHash = _auth.HashPassword(user, model.NewPassword);
        user.MustChangePassword = false;
        user.RotateSecurityStamp();          // signs out every other browser
        await _users.UpdateAsync(user);
        await _users.UseAllResetTokensAsync(user.Id);

        await _auth.SignInAsync(HttpContext, user, rememberMe: false);
        _logger.LogInformation("{Email} changed their password", user.Email);
        TempData["Success"] = "Password changed. Other browsers signed in to your account have been signed out.";
        return RedirectToAction("Index", "Home");
    }

    // ================= My account =================

    public async Task<IActionResult> MyAccount()
    {
        var user = await CurrentUserAsync();
        if (user is null) return RedirectToAction(nameof(Login));
        return View(new MyAccountModel { Email = user.Email, DisplayName = user.DisplayName, TimeZoneId = user.TimeZoneId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MyAccount(MyAccountModel model)
    {
        var user = await CurrentUserAsync();
        if (user is null) return RedirectToAction(nameof(Login));
        model.Email = user.Email;

        // A client's portal user has no time zone choice: their pages use the business's own zone.
        if (user.IsClientUser)
        {
            ModelState.Remove(nameof(model.TimeZoneId));
            model.TimeZoneId = user.TimeZoneId;
        }
        else if (!MadeeAutomation.Domain.Regional.Markets.TimeZoneIds.Contains(model.TimeZoneId))
            ModelState.AddModelError(nameof(model.TimeZoneId), "Choose one of the listed time zones.");
        if (!ModelState.IsValid) return View(model);

        user.DisplayName = model.DisplayName.Trim();
        user.TimeZoneId = model.TimeZoneId;
        await _users.UpdateAsync(user);

        await _auth.SignInAsync(HttpContext, user, rememberMe: false);   // refresh name + time zone in the cookie
        TempData["Success"] = "Your account settings are saved.";
        return RedirectToAction(nameof(MyAccount));
    }

    private async Task<AdminUser?> CurrentUserAsync()
    {
        var id = AdminAuthService.CurrentUserId(User);
        return id is null ? null : await _users.GetByIdAsync(id.Value);
    }

    // ================= Forgot / reset password =================

    [AllowAnonymous]
    public IActionResult ForgotPassword() => View(new ForgotPasswordModel());

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordModel model)
    {
        if (!ModelState.IsValid) return View(model);

        // Same answer whether or not the account exists, so this page can't be used to find admin emails.
        TempData["Success"] = "If that email belongs to an account, a reset link is on its way. The link works for 1 hour.";

        var user = await _users.GetByEmailAsync(model.Email);
        if (user is null || !user.IsActive)
        {
            _logger.LogWarning("Password reset asked for unknown/inactive email {Email}", model.Email);
            return RedirectToAction(nameof(Login));
        }

        await _users.UseAllResetTokensAsync(user.Id);   // only the newest link works
        var (token, hash) = AdminAuthService.NewResetToken();
        await _users.AddResetTokenAsync(new PasswordResetToken
        {
            AdminUserId = user.Id,
            TokenHash = hash,
            ExpiresAtUtc = DateTime.UtcNow.Add(AdminAuthService.ResetLinkLifetime)
        });

        var link = Url.Action(nameof(ResetPassword), "Account", new { email = user.Email, token }, Request.Scheme)!;
        var body =
            $"Hello {user.DisplayName},\n\n" +
            "Someone (hopefully you) asked to reset your MadeeAutomation password.\n" +
            $"Open this link within 1 hour to choose a new password:\n\n{link}\n\n" +
            "If you didn't ask for this, ignore this email - your password stays the same.\n";

        var result = await _email.SendAsync(user.Email, "Reset your MadeeAutomation password", body);
        if (!result.Success)
        {
            _logger.LogError("Reset email to {Email} not sent: {Error}", user.Email, result.Error);
            if (_environment.IsDevelopment())
                _logger.LogWarning("DEVELOPMENT ONLY - email is not working, so here is the reset link for {Email}: {Link}", user.Email, link);
        }

        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(string? email, string? token)
    {
        var problem = await CheckResetLinkAsync(email, token);
        if (problem is not null)
        {
            ViewBag.LinkProblem = problem;
            return View("ResetPasswordInvalid");
        }
        return View(new ResetPasswordModel { Email = email!, Token = token! });
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordModel model)
    {
        var problem = await CheckResetLinkAsync(model.Email, model.Token);
        if (problem is not null)
        {
            ViewBag.LinkProblem = problem;
            return View("ResetPasswordInvalid");
        }

        var user = (await _users.GetByEmailAsync(model.Email))!;
        if (ModelState.IsValid && AdminAuthService.CheckPasswordRules(model.NewPassword, user.Email) is { } rule)
            ModelState.AddModelError(nameof(model.NewPassword), rule);
        if (!ModelState.IsValid) return View(model);

        user.PasswordHash = _auth.HashPassword(user, model.NewPassword);
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.RotateSecurityStamp();
        await _users.UpdateAsync(user);
        await _users.UseAllResetTokensAsync(user.Id);

        _logger.LogInformation("{Email} reset their password with an email link", user.Email);
        TempData["Success"] = "Your password has been reset. Sign in with the new password.";
        return RedirectToAction(nameof(Login));
    }

    private async Task<string?> CheckResetLinkAsync(string? email, string? token)
    {
        const string bad = "This reset link is not valid. Ask for a new one.";
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token)) return bad;

        var record = await _users.GetResetTokenAsync(AdminAuthService.HashToken(token));
        var user = await _users.GetByEmailAsync(email);
        if (record is null || user is null || record.AdminUserId != user.Id || !user.IsActive) return bad;
        if (record.UsedAtUtc is not null) return "This reset link has already been used. Ask for a new one.";
        if (record.ExpiresAtUtc < DateTime.UtcNow) return "This reset link has expired (links work for 1 hour). Ask for a new one.";
        return null;
    }

    private IActionResult LocalRedirectOrHome(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction("Index", "Home");
}
