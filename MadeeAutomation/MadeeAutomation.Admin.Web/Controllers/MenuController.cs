using MadeeAutomation.Domain.Enums;
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

    private static readonly int[] PageSizes = { 10, 25, 50, 100 };

    /// <summary>q = search, cat = category ("-" = no category), st = available | soldout | limited, ps = page size.</summary>
    public async Task<IActionResult> Index(Guid clientConfigId, string? q = null, string? cat = null, string? st = null, int page = 1, int ps = 25)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        // Spas and salons sell services by time, not menu items.
        if (client.Vertical == VerticalPack.BeautyWellness) return RedirectToAction("Index", "Services", new { clientConfigId = client.Id });
        return View(await PageAsync(client, new MenuItemFormModel { ClientConfigId = client.Id }, q, cat, st, page, ps));
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

        var items = await _profile.GetMenuAsync(client.Id);   // all items: the new one goes last
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

    public async Task<IActionResult> Edit(Guid id, string? returnUrl = null)
    {
        ViewBag.ReturnUrl = returnUrl;
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
    public async Task<IActionResult> Edit(Guid id, MenuItemFormModel model, string? returnUrl = null)
    {
        ViewBag.ReturnUrl = returnUrl;
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
        return BackTo(returnUrl, RedirectToAction(nameof(Index), new { clientConfigId = item.ClientConfigId }));
    }

    /// <summary>Sold out today / back in stock - one click.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleAvailable(Guid id, string? returnUrl = null)
    {
        var item = await _profile.GetMenuItemAsync(id);
        if (item is null || !CanAccess(item.ClientConfigId)) return NotFound();
        item.IsAvailable = !item.IsAvailable;
        await _profile.UpdateMenuItemAsync(item);
        TempData["Success"] = item.IsAvailable ? $"\"{item.Name}\" is available again." : $"\"{item.Name}\" marked sold out - the bot won't take orders for it.";
        return BackTo(returnUrl, RedirectToAction(nameof(Index), new { clientConfigId = item.ClientConfigId }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, string? returnUrl = null)
    {
        var item = await _profile.GetMenuItemAsync(id);
        if (item is null || !CanAccess(item.ClientConfigId)) return NotFound();
        await _profile.DeleteMenuItemAsync(id);
        TempData["Success"] = $"\"{item.Name}\" deleted.";
        return BackTo(returnUrl, RedirectToAction(nameof(Index), new { clientConfigId = item.ClientConfigId }));
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

    private async Task<MenuPageModel> PageAsync(ClientConfig client, MenuItemFormModel newItem,
        string? q = null, string? cat = null, string? st = null, int page = 1, int ps = 25)
    {
        if (!PageSizes.Contains(ps)) ps = 25;
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 100)];
        st = st is "available" or "soldout" or "limited" ? st : null;
        string? category = string.IsNullOrEmpty(cat) ? null : cat == "-" ? "" : cat.Trim();

        var items = await _profile.GetMenuPagedAsync(client.Id, q, category, st, page, ps);
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(DateTime.UtcNow, client.TimeZoneId));
        return new MenuPageModel
        {
            Client = client,
            Items = items.Items,
            TotalItems = await _profile.CountMenuAsync(client.Id),
            NewItem = newItem,
            Search = q,
            Category = category,
            Status = st,
            Categories = await _profile.GetMenuCategoriesAsync(client.Id),
            Pager = new PagerModel
            {
                Page = items.Page, PageSize = ps, TotalCount = items.TotalCount, Controller = "Menu", Noun = "item",
                Route = new()
                {
                    ["clientConfigId"] = client.Id.ToString(), ["q"] = q, ["cat"] = category is null ? null : category.Length == 0 ? "-" : category,
                    ["st"] = st, ["ps"] = ps == 25 ? null : ps.ToString()
                }
            },
            OrderedToday = items.Items.Any(i => i.DailyLimit.HasValue)
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
