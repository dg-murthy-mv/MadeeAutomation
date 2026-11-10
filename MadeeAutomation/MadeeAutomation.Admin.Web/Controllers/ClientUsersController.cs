using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Services;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// Staff only (no [ClientPortal]): gives a client their own sign-in to the client portal.
/// A portal user can only ever see the one client they were created for.
/// </summary>
public class ClientUsersController : Controller
{
    private readonly IAdminUserRepository _users;
    private readonly IClientRepository _clients;
    private readonly AdminAuthService _auth;
    private readonly ILogger<ClientUsersController> _logger;

    public ClientUsersController(IAdminUserRepository users, IClientRepository clients, AdminAuthService auth, ILogger<ClientUsersController> logger)
    {
        _users = users;
        _clients = clients;
        _auth = auth;
        _logger = logger;
    }

    public async Task<IActionResult> Create(Guid clientConfigId)
    {
        var client = await _clients.GetWithWorkflowsAsync(clientConfigId);
        if (client is null) return NotFound();
        return View(new ClientUserFormModel { ClientConfigId = client.Id, ClientName = client.ClientName });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ClientUserFormModel model)
    {
        var client = await _clients.GetWithWorkflowsAsync(model.ClientConfigId);
        if (client is null) return NotFound();
        model.ClientName = client.ClientName;

        if (ModelState.IsValid && await _users.GetByEmailAsync(model.Email) is not null)
            ModelState.AddModelError(nameof(model.Email), "There is already an account with this email.");
        if (!ModelState.IsValid) return View(model);

        var temporaryPassword = AdminAuthService.GenerateTemporaryPassword();
        var user = new AdminUser
        {
            Email = model.Email.Trim().ToLowerInvariant(),
            DisplayName = model.DisplayName.Trim(),
            ClientConfigId = client.Id,
            TimeZoneId = client.TimeZoneId,
            IsActive = true,
            MustChangePassword = true
        };
        user.PasswordHash = _auth.HashPassword(user, temporaryPassword);
        await _users.AddAsync(user);

        _logger.LogInformation("{By} added portal user {Email} for client {ClientCode}", User.Identity?.Name, user.Email, client.ClientCode);
        TempData["Success"] = $"Portal user {user.Email} added for {client.ClientName}. Temporary password: {temporaryPassword} - give it to them privately; " +
                              "they must choose their own password when they first sign in. It won't be shown again.";
        return RedirectToAction("Details", "Clients", new { id = client.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(Guid id)
    {
        var user = await _users.GetByIdAsync(id);
        if (user?.ClientConfigId is not { } clientId) return NotFound();

        user.IsActive = !user.IsActive;
        if (!user.IsActive) user.RotateSecurityStamp();   // signs them out everywhere
        await _users.UpdateAsync(user);

        _logger.LogInformation("{By} {Action} portal user {Email}", User.Identity?.Name, user.IsActive ? "re-activated" : "deactivated", user.Email);
        TempData["Success"] = $"{user.Email} {(user.IsActive ? "can sign in again" : "is deactivated and has been signed out")}.";
        return RedirectToAction("Details", "Clients", new { id = clientId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(Guid id)
    {
        var user = await _users.GetByIdAsync(id);
        if (user?.ClientConfigId is not { } clientId) return NotFound();

        var temporaryPassword = AdminAuthService.GenerateTemporaryPassword();
        user.PasswordHash = _auth.HashPassword(user, temporaryPassword);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.RotateSecurityStamp();
        await _users.UpdateAsync(user);
        await _users.UseAllResetTokensAsync(user.Id);

        _logger.LogInformation("{By} reset the password of portal user {Email}", User.Identity?.Name, user.Email);
        TempData["Success"] = $"New temporary password for {user.Email}: {temporaryPassword} - give it to them privately; " +
                              "they must choose their own password when they sign in. It won't be shown again.";
        return RedirectToAction("Details", "Clients", new { id = clientId });
    }
}
