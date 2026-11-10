using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Services;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>Who can sign in to Admin.Web. Every admin can manage the others (there is one role).</summary>
public class AdminUsersController : Controller
{
    private readonly IAdminUserRepository _users;
    private readonly AdminAuthService _auth;
    private readonly ILogger<AdminUsersController> _logger;

    public AdminUsersController(IAdminUserRepository users, AdminAuthService auth, ILogger<AdminUsersController> logger)
    {
        _users = users;
        _auth = auth;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        ViewBag.CurrentUserId = AdminAuthService.CurrentUserId(User);
        return View(await _users.ListAsync());
    }

    public IActionResult Create() => View(new AdminUserFormModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AdminUserFormModel model)
    {
        if (ModelState.IsValid && await _users.GetByEmailAsync(model.Email) is not null)
            ModelState.AddModelError(nameof(model.Email), "There is already an account with this email.");
        if (!ModelState.IsValid) return View(model);

        var temporaryPassword = AdminAuthService.GenerateTemporaryPassword();
        var user = new AdminUser
        {
            Email = model.Email.Trim().ToLowerInvariant(),
            DisplayName = model.DisplayName.Trim(),
            IsActive = true,
            MustChangePassword = true
        };
        user.PasswordHash = _auth.HashPassword(user, temporaryPassword);
        await _users.AddAsync(user);

        _logger.LogInformation("{By} added admin {Email}", User.Identity?.Name, user.Email);
        TempData["Success"] = $"Admin {user.Email} added. Temporary password: {temporaryPassword} - give it to them privately; " +
                              "they must choose their own password when they first sign in. It won't be shown again.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(Guid id)
    {
        if (id == AdminAuthService.CurrentUserId(User))
        {
            TempData["Error"] = "You can't deactivate your own account.";
            return RedirectToAction(nameof(Index));
        }

        var user = await _users.GetByIdAsync(id);
        if (user is null || user.IsClientUser) return NotFound();   // portal users are managed on their client's page

        user.IsActive = !user.IsActive;
        if (!user.IsActive) user.RotateSecurityStamp();   // signs them out everywhere
        await _users.UpdateAsync(user);

        _logger.LogInformation("{By} {Action} admin {Email}", User.Identity?.Name, user.IsActive ? "re-activated" : "deactivated", user.Email);
        TempData["Success"] = $"{user.Email} {(user.IsActive ? "can sign in again" : "is deactivated and has been signed out")}.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Gives another admin a new temporary password (and unlocks them) - for when email isn't set up.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(Guid id)
    {
        if (id == AdminAuthService.CurrentUserId(User))
        {
            TempData["Error"] = "Use Change password for your own account.";
            return RedirectToAction(nameof(Index));
        }

        var user = await _users.GetByIdAsync(id);
        if (user is null || user.IsClientUser) return NotFound();   // portal users are managed on their client's page

        var temporaryPassword = AdminAuthService.GenerateTemporaryPassword();
        user.PasswordHash = _auth.HashPassword(user, temporaryPassword);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.RotateSecurityStamp();
        await _users.UpdateAsync(user);
        await _users.UseAllResetTokensAsync(user.Id);

        _logger.LogInformation("{By} reset the password of admin {Email}", User.Identity?.Name, user.Email);
        TempData["Success"] = $"New temporary password for {user.Email}: {temporaryPassword} - give it to them privately; " +
                              "they must choose their own password when they sign in. It won't be shown again.";
        return RedirectToAction(nameof(Index));
    }
}
