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

    public async Task<IActionResult> Index()
    {
        var clients = await _db.ClientConfigs
            .Include(c => c.Workflows)
            .OrderBy(c => c.ClientName)
            .ToListAsync();

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
