using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// Internal dashboard for MadeeAutomation staff (you) to configure clients and their
/// workflows. Controllers here talk to the DbContext directly for simplicity, mirroring
/// how an internal admin tool is typically thinner than the client-facing WebApi.
/// </summary>
public class ClientsController : Controller
{
    private readonly AutomationDbContext _db;

    public ClientsController(AutomationDbContext db) => _db = db;

    public async Task<IActionResult> Index(string? search, int page = 1)
    {
        const int pageSize = 10;
        var query = _db.ClientConfigs.Include(c => c.Workflows).AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(c => c.ClientName.Contains(search) || c.ClientCode.Contains(search));
        }

        var totalCount = await query.CountAsync();

        var clients = await query
            .OrderBy(c => c.ClientName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewBag.Search = search;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        ViewBag.TotalCount = totalCount;

        return View(clients);
    }

    public IActionResult Create() => View(new ClientConfig());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ClientConfig model)
    {
        if (!ModelState.IsValid) return View(model);

        model.Id = Guid.NewGuid();
        model.CreatedAtUtc = DateTime.UtcNow;
        _db.ClientConfigs.Add(model);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var client = await _db.ClientConfigs
            .Include(c => c.Workflows)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (client is null) return NotFound();
        return View(client);
    }

    public IActionResult CreateWorkflow(Guid clientConfigId)
    {
        return View(new WorkflowDefinition { ClientConfigId = clientConfigId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateWorkflow(WorkflowDefinition model)
    {
        if (!ModelState.IsValid) return View(model);

        model.Id = Guid.NewGuid();
        model.CreatedAtUtc = DateTime.UtcNow;
        _db.WorkflowDefinitions.Add(model);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Details), new { id = model.ClientConfigId });
    }
}
