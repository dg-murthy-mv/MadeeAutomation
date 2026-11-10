using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// A client's structured menu: add, edit, delete, sold-out switch, daily limit, import from the old free text.
/// Used by staff (any client) and by a client's portal user (their own menu only - see ClientScopedController).
/// </summary>
public class MenuController : ClientScopedController
{
    private readonly IBusinessProfileRepository _profile;
    private readonly IClientRepository _clients;
    private readonly IOrderRepository _orders;

    public MenuController(IBusinessProfileRepository profile, IClientRepository clients, IOrderRepository orders)
    {
        _profile = profile;
        _clients = clients;
        _orders = orders;
    }

    public async Task<IActionResult> Index(Guid clientConfigId)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        return View(await PageAsync(client, new MenuItemFormModel { ClientConfigId = client.Id }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add([Bind(Prefix = "NewItem")] MenuItemFormModel model)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(model.ClientConfigId));
        model.ClientConfigId = client?.Id ?? Guid.Empty;
        if (client is null) return NotFound();

        model.Name = (model.Name ?? "").Trim();
        if (ModelState.IsValid && await _profile.MenuNameExistsAsync(client.Id, model.Name))
            ModelState.AddModelError("NewItem.Name", "This menu already has an item with that name.");
        if (!ModelState.IsValid) return View("Index", await PageAsync(client, model));

        var items = await _profile.GetMenuAsync(client.Id);
        await _profile.AddMenuItemsAsync(new[]
        {
            new MenuItem
            {
                ClientConfigId = client.Id,
                Name = model.Name,
                Category = Clean(model.Category),
                Price = model.Price,
                Aliases = CleanAliases(model.Aliases),
                DailyLimit = model.DailyLimit,
                IsAvailable = true,
                SortOrder = items.Count == 0 ? 0 : items.Max(i => i.SortOrder) + 1
            }
        });

        TempData["Success"] = $"\"{model.Name}\" added.";
        return RedirectToAction(nameof(Index), new { clientConfigId = client.Id });
    }

    public async Task<IActionResult> Edit(Guid id)
    {
        var item = await _profile.GetMenuItemAsync(id);
        if (item is null || !CanAccess(item.ClientConfigId)) return NotFound();
        ViewBag.Client = await _clients.GetWithWorkflowsAsync(item.ClientConfigId);
        return View(new MenuItemFormModel
        {
            Id = item.Id, ClientConfigId = item.ClientConfigId, Name = item.Name, Category = item.Category,
            Price = item.Price, Aliases = item.Aliases, IsAvailable = item.IsAvailable, SortOrder = item.SortOrder,
            DailyLimit = item.DailyLimit
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, MenuItemFormModel model)
    {
        var item = await _profile.GetMenuItemAsync(id);
        if (item is null || !CanAccess(item.ClientConfigId)) return NotFound();
        model.Id = item.Id;
        model.ClientConfigId = item.ClientConfigId;
        model.Name = (model.Name ?? "").Trim();

        if (ModelState.IsValid && await _profile.MenuNameExistsAsync(item.ClientConfigId, model.Name, item.Id))
            ModelState.AddModelError(nameof(model.Name), "This menu already has an item with that name.");
        if (!ModelState.IsValid)
        {
            ViewBag.Client = await _clients.GetWithWorkflowsAsync(item.ClientConfigId);
            return View(model);
        }

        item.Name = model.Name;
        item.Category = Clean(model.Category);
        item.Price = model.Price;
        item.Aliases = CleanAliases(model.Aliases);
        item.IsAvailable = model.IsAvailable;
        item.SortOrder = model.SortOrder;
        item.DailyLimit = model.DailyLimit;
        await _profile.UpdateMenuItemAsync(item);

        TempData["Success"] = $"\"{item.Name}\" saved.";
        return RedirectToAction(nameof(Index), new { clientConfigId = item.ClientConfigId });
    }

    /// <summary>Sold out today / back in stock - one click.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleAvailable(Guid id)
    {
        var item = await _profile.GetMenuItemAsync(id);
        if (item is null || !CanAccess(item.ClientConfigId)) return NotFound();
        item.IsAvailable = !item.IsAvailable;
        await _profile.UpdateMenuItemAsync(item);
        TempData["Success"] = item.IsAvailable ? $"\"{item.Name}\" is available again." : $"\"{item.Name}\" marked sold out - the bot won't take orders for it.";
        return RedirectToAction(nameof(Index), new { clientConfigId = item.ClientConfigId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var item = await _profile.GetMenuItemAsync(id);
        if (item is null || !CanAccess(item.ClientConfigId)) return NotFound();
        await _profile.DeleteMenuItemAsync(id);
        TempData["Success"] = $"\"{item.Name}\" deleted.";
        return RedirectToAction(nameof(Index), new { clientConfigId = item.ClientConfigId });
    }

    /// <summary>Turns the old free-text menu into items, then clears the old text so there is one source of truth.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportFromText(Guid clientConfigId)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();

        var lines = MenuCatalog.Parse(client.MenuText);
        var existing = (await _profile.GetMenuAsync(client.Id)).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = existing.Count;
        var toAdd = new List<MenuItem>();
        foreach (var line in lines)
        {
            var name = line.Name.Length > 200 ? line.Name[..200] : line.Name;
            if (string.IsNullOrWhiteSpace(name) || !existing.Add(name)) continue;
            toAdd.Add(new MenuItem { ClientConfigId = client.Id, Name = name, Price = line.Price, IsAvailable = true, SortOrder = order++ });
        }

        if (toAdd.Count > 0) await _profile.AddMenuItemsAsync(toAdd);
        client.MenuText = null;
        await _clients.UpdateAsync(client);

        var unpriced = toAdd.Count(i => i.Price is null);
        TempData["Success"] = $"Imported {toAdd.Count} item(s) from the old menu text" +
                              (unpriced > 0 ? $" - {unpriced} had no price; add one with Edit." : ".");
        return RedirectToAction(nameof(Index), new { clientConfigId = client.Id });
    }

    private async Task<MenuPageModel> PageAsync(ClientConfig client, MenuItemFormModel newItem)
    {
        var items = await _profile.GetMenuAsync(client.Id);
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(DateTime.UtcNow, client.TimeZoneId));
        return new MenuPageModel
        {
            Client = client,
            Items = items,
            NewItem = newItem,
            OrderedToday = items.Any(i => i.DailyLimit.HasValue)
                ? await _orders.GetOrderedQuantitiesAsync(client.Id, today)
                : new Dictionary<Guid, int>()
        };
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static string? CleanAliases(string? v)
    {
        var parts = (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
