using System.Text.Json;
using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Services;
using MadeeAutomation.Connectors;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;
using MadeeAutomation.Domain.Settings;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// Internal dashboard for MadeeAutomation staff to configure clients, their credentials and workflows.
/// Talks to repositories only - no DbContext in controllers.
/// </summary>
public class ClientsController : Controller
{
    private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

    private readonly IClientRepository _clients;
    private readonly IWorkflowRepository _workflows;
    private readonly IClientSettingsService _settings;
    private readonly IConfiguration _configuration;
    private readonly LineCredentialChecker _lineChecker;
    private readonly IApiService _api;
    private readonly ILogger<ClientsController> _logger;
    private readonly IBusinessProfileRepository _profile;

    public ClientsController(IClientRepository clients, IWorkflowRepository workflows, IClientSettingsService settings,
        IConfiguration configuration, LineCredentialChecker lineChecker, IApiService api, ILogger<ClientsController> logger,
        IBusinessProfileRepository profile)
    {
        _profile = profile;
        _clients = clients;
        _workflows = workflows;
        _settings = settings;
        _configuration = configuration;
        _lineChecker = lineChecker;
        _api = api;
        _logger = logger;
    }

    private string WebApiBaseUrl => (_configuration["WebApi:PublicBaseUrl"] ?? "https://localhost:58715").TrimEnd('/');

    // ================= Clients =================

    public async Task<IActionResult> Index(string? search, int page = 1, int pageSize = 10)
    {
        if (!AllowedPageSizes.Contains(pageSize)) pageSize = 10;
        if (page < 1) page = 1;

        var result = await _clients.GetPagedAsync(search, page, pageSize);

        ViewBag.Search = search;
        ViewBag.PageSize = pageSize;
        return View(result);
    }

    public IActionResult Create() => View("Form", new ClientFormModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ClientFormModel model)
    {
        model.Id = null;
        // Accept "MadeeBakers" and store "madeebakers": re-check the code after lower-casing it.
        model.ClientCode = (model.ClientCode ?? "").Trim().ToLowerInvariant();
        ModelState.Remove(nameof(model.ClientCode));
        if (model.ClientCode.Length is < 2 or > 50 ||
            !System.Text.RegularExpressions.Regex.IsMatch(model.ClientCode, "^[a-z0-9][a-z0-9-]*$"))
            ModelState.AddModelError(nameof(model.ClientCode), "Use 2-50 lower-case letters, numbers and dashes (e.g. madeebakers).");
        else if (await _clients.CodeExistsAsync(model.ClientCode))
            ModelState.AddModelError(nameof(model.ClientCode), "Another client already uses this code.");

        ValidateMarket(model);
        if (!ModelState.IsValid) return View("Form", model);

        var client = new ClientConfig
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow,
            IntegrationSettingsJson = "{}"
        };
        CopyBasics(model, client);

        await _clients.AddAsync(client);
        TempData["Success"] = $"Client \"{client.ClientName}\" created. Add its LINE / POSFirst credentials next.";
        return RedirectToAction(nameof(Edit), new { id = client.Id });
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var client = await _clients.GetWithWorkflowsAsync(id);
        if (client is null) return NotFound();

        var model = new ClientDetailsModel { Client = client, WebApiBaseUrl = WebApiBaseUrl };
        var menu = await _profile.GetMenuAsync(client.Id);
        var localToday = DateOnly.FromDateTime(MadeeAutomation.Domain.Regional.LocalTime.ToLocal(DateTime.UtcNow, client.TimeZoneId));
        var hoursStatus = MadeeAutomation.Domain.Regional.BusinessClock.GetStatus(
            await _profile.GetHoursAsync(client.Id), await _profile.GetClosedDatesAsync(client.Id, localToday.AddDays(-1)),
            client.TimeZoneId, DateTime.UtcNow);
        model.MenuItemCount = menu.Count;
        model.SoldOutCount = menu.Count(m => !m.IsAvailable);
        model.HoursStatus = hoursStatus;
        try
        {
            model.Settings = _settings.Read(client.IntegrationSettingsJson);
        }
        catch (InvalidOperationException ex)
        {
            model.SettingsError = ex.Message;
        }

        return View(model);
    }

    public async Task<IActionResult> Edit(Guid id)
    {
        var client = await _clients.GetWithWorkflowsAsync(id);
        if (client is null) return NotFound();

        var model = new ClientFormModel
        {
            Id = client.Id,
            ClientName = client.ClientName,
            ClientCode = client.ClientCode,
            Country = client.Country,
            TimeZoneId = client.TimeZoneId,
            Currency = client.Currency,
            Vertical = client.Vertical,
            Status = client.Status,
            MerkaiIntegrationEnabled = client.MerkaiIntegrationEnabled,
            PosFirstIntegrationEnabled = client.PosFirstIntegrationEnabled,
            KnowledgeBaseText = client.KnowledgeBaseText,
            MenuText = client.MenuText,
            FallbackReplyText = client.FallbackReplyText,
            StaffAlertEmail = client.StaffAlertEmail,
            StaffAlertLineUserId = client.StaffAlertLineUserId
        };

        LoadSettingsInto(client, model); // adds a page error if the saved settings can't be decrypted
        return View("Form", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, ClientFormModel model)
    {
        var client = await _clients.GetWithWorkflowsAsync(id);
        if (client is null) return NotFound();

        // The client code is part of every webhook URL - it is fixed after creation.
        model.Id = client.Id;
        model.ClientCode = client.ClientCode;
        ModelState.Remove(nameof(model.ClientCode));

        foreach (var field in new[] { nameof(model.PosFirstBaseUrl), nameof(model.MerkaiBaseUrl) })
        {
            var value = field == nameof(model.PosFirstBaseUrl) ? model.PosFirstBaseUrl : model.MerkaiBaseUrl;
            if (!string.IsNullOrWhiteSpace(value) &&
                !(Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")))
                ModelState.AddModelError(field, "Enter a full URL starting with http:// or https://");
        }

        ValidateMarket(model);

        ClientIntegrationSettings settings;
        try
        {
            settings = _settings.Read(client.IntegrationSettingsJson);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            LoadSettingsInto(client, model);
            return View("Form", model);
        }

        if (!ModelState.IsValid)
        {
            FillHints(settings, model);
            return View("Form", model);
        }

        CopyBasics(model, client);

        // Secrets: blank = keep, ticked "Remove" = clear, anything else = replace.
        settings.LineChannelSecret = MergeSecret(settings.LineChannelSecret, model.LineChannelSecret, model, nameof(model.LineChannelSecret));
        settings.LineChannelAccessToken = MergeSecret(settings.LineChannelAccessToken, model.LineChannelAccessToken, model, nameof(model.LineChannelAccessToken));
        settings.WhatsAppAccessToken = MergeSecret(settings.WhatsAppAccessToken, model.WhatsAppAccessToken, model, nameof(model.WhatsAppAccessToken));
        settings.MessengerPageAccessToken = MergeSecret(settings.MessengerPageAccessToken, model.MessengerPageAccessToken, model, nameof(model.MessengerPageAccessToken));
        settings.PosFirstApiKey = MergeSecret(settings.PosFirstApiKey, model.PosFirstApiKey, model, nameof(model.PosFirstApiKey));
        settings.MerkaiApiKey = MergeSecret(settings.MerkaiApiKey, model.MerkaiApiKey, model, nameof(model.MerkaiApiKey));

        // Non-secret values are shown and saved as typed.
        settings.WhatsAppPhoneNumberId = Clean(model.WhatsAppPhoneNumberId);
        settings.PosFirstBaseUrl = Clean(model.PosFirstBaseUrl);
        settings.MerkaiBaseUrl = Clean(model.MerkaiBaseUrl);

        try
        {
            client.IntegrationSettingsJson = _settings.Protect(settings);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            FillHints(settings, model);
            return View("Form", model);
        }

        await _clients.UpdateAsync(client);
        _logger.LogInformation("Client {ClientCode} updated", client.ClientCode);

        TempData["Success"] = "Client saved.";
        return RedirectToAction(nameof(Details), new { id = client.Id });
    }

    // ================= Workflows =================

    public async Task<IActionResult> CreateWorkflow(Guid clientConfigId)
    {
        var client = await _clients.GetWithWorkflowsAsync(clientConfigId);
        if (client is null) return NotFound();

        return View("WorkflowForm", new WorkflowFormModel
        {
            ClientConfigId = client.Id,
            ClientName = client.ClientName,
            Vertical = client.Vertical,
            Name = "LINE customer messages"
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateWorkflow(WorkflowFormModel model)
    {
        var client = await _clients.GetWithWorkflowsAsync(model.ClientConfigId);
        if (client is null) return NotFound();
        model.Id = null;
        model.ClientName = client.ClientName;

        ValidateWorkflow(model, client);
        if (!ModelState.IsValid) return View("WorkflowForm", model);

        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            ClientConfigId = client.Id,
            CreatedAtUtc = DateTime.UtcNow
        };
        CopyWorkflow(model, workflow);

        await _workflows.AddAsync(workflow);
        TempData["Success"] = $"Workflow \"{workflow.Name}\" created.";
        return RedirectToAction(nameof(Details), new { id = client.Id });
    }

    public async Task<IActionResult> EditWorkflow(Guid id)
    {
        var workflow = await _workflows.GetByIdAsync(id);
        if (workflow is null) return NotFound();

        return View("WorkflowForm", new WorkflowFormModel
        {
            Id = workflow.Id,
            ClientConfigId = workflow.ClientConfigId,
            ClientName = workflow.ClientConfig.ClientName,
            Name = workflow.Name,
            HandlerKey = workflow.HandlerKey,
            Vertical = workflow.Vertical,
            TriggerType = workflow.TriggerType,
            Channel = workflow.Channel ?? "",
            TriggerConfigJson = workflow.TriggerConfigJson,
            IsEnabled = workflow.IsEnabled
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditWorkflow(Guid id, WorkflowFormModel model)
    {
        var workflow = await _workflows.GetByIdAsync(id);
        if (workflow is null) return NotFound();

        var client = await _clients.GetWithWorkflowsAsync(workflow.ClientConfigId);
        if (client is null) return NotFound();

        model.Id = workflow.Id;
        model.ClientConfigId = workflow.ClientConfigId;
        model.ClientName = client.ClientName;

        ValidateWorkflow(model, client);
        if (!ModelState.IsValid) return View("WorkflowForm", model);

        CopyWorkflow(model, workflow);
        await _workflows.UpdateAsync(workflow);

        TempData["Success"] = $"Workflow \"{workflow.Name}\" saved.";
        return RedirectToAction(nameof(Details), new { id = client.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleWorkflow(Guid id)
    {
        var workflow = await _workflows.GetByIdAsync(id);
        if (workflow is null) return NotFound();

        var enable = !workflow.IsEnabled;
        if (enable)
        {
            var client = await _clients.GetWithWorkflowsAsync(workflow.ClientConfigId);
            var clash = client is null ? null : FindChannelClash(client, workflow.Id, workflow.TriggerType, workflow.Channel);
            if (clash is not null)
            {
                TempData["Error"] = ClashMessage(clash);
                return RedirectToAction(nameof(Details), new { id = workflow.ClientConfigId });
            }
        }

        await _workflows.SetEnabledAsync(workflow.Id, enable);
        TempData["Success"] = $"Workflow \"{workflow.Name}\" {(enable ? "enabled" : "disabled")}.";
        return RedirectToAction(nameof(Details), new { id = workflow.ClientConfigId });
    }

    // ================= LINE credentials check =================

    /// <summary>Asks LINE whether the saved access token works and which account it belongs to.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckLine(Guid id, CancellationToken cancellationToken)
    {
        var client = await _clients.GetWithWorkflowsAsync(id, cancellationToken);
        if (client is null) return NotFound();

        try
        {
            var settings = _settings.Read(client.IntegrationSettingsJson);
            var result = await _lineChecker.CheckAccessTokenAsync(settings.LineChannelAccessToken, cancellationToken);
            TempData[result.Ok ? "Success" : "Error"] = result.Message;
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // ================= Test a message workflow =================

    public async Task<IActionResult> TestWorkflow(Guid id)
    {
        var workflow = await _workflows.GetByIdAsync(id);
        if (workflow is null) return NotFound();
        if (workflow.TriggerType != TriggerType.MessageReceived)
        {
            TempData["Error"] = "Only customer-message workflows can be tested.";
            return RedirectToAction(nameof(Details), new { id = workflow.ClientConfigId });
        }

        return View(new WorkflowTestModel
        {
            WorkflowId = workflow.Id,
            ClientConfigId = workflow.ClientConfigId,
            WorkflowName = workflow.Name,
            ClientName = workflow.ClientConfig.ClientName,
            ClientTimeZoneId = workflow.ClientConfig.TimeZoneId,
            ClientCurrency = workflow.ClientConfig.Currency,
            Channel = workflow.Channel ?? Channels.Line
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestWorkflow(Guid id, WorkflowTestModel model, CancellationToken cancellationToken)
    {
        var workflow = await _workflows.GetByIdAsync(id, cancellationToken);
        if (workflow is null) return NotFound();

        model.WorkflowId = workflow.Id;
        model.ClientConfigId = workflow.ClientConfigId;
        model.WorkflowName = workflow.Name;
        model.ClientName = workflow.ClientConfig.ClientName;
        model.ClientTimeZoneId = workflow.ClientConfig.TimeZoneId;
        model.ClientCurrency = workflow.ClientConfig.Currency;
        model.Channel = Channels.Normalize(model.Channel) ?? workflow.Channel ?? Channels.Line;

        if (!ModelState.IsValid) return View(model);

        model.Result = await _api.TestWorkflowAsync(workflow.Id, model.Message.Trim(), model.Channel, cancellationToken);
        return View(model);
    }

    // ================= helpers =================

    private static void CopyBasics(ClientFormModel model, ClientConfig client)
    {
        client.ClientName = model.ClientName.Trim();
        client.ClientCode = model.ClientCode.Trim().ToLowerInvariant();
        client.Country = model.Country;
        client.TimeZoneId = model.TimeZoneId;
        client.Currency = model.Currency.Trim().ToUpperInvariant();
        client.Vertical = model.Vertical;
        client.Status = model.Status;
        client.MerkaiIntegrationEnabled = model.MerkaiIntegrationEnabled;
        client.PosFirstIntegrationEnabled = model.PosFirstIntegrationEnabled;
        client.KnowledgeBaseText = string.IsNullOrWhiteSpace(model.KnowledgeBaseText) ? null : model.KnowledgeBaseText.Trim();
        client.MenuText = string.IsNullOrWhiteSpace(model.MenuText) ? null : model.MenuText.Trim();
        client.FallbackReplyText = Clean(model.FallbackReplyText);
        client.StaffAlertEmail = Clean(model.StaffAlertEmail)?.ToLowerInvariant();
        client.StaffAlertLineUserId = Clean(model.StaffAlertLineUserId);
    }

    /// <summary>Country, currency and time zone must be one of the four markets' values.</summary>
    private void ValidateMarket(ClientFormModel model)
    {
        if (Markets.Find(model.Country) is null)
            ModelState.AddModelError(nameof(model.Country), "Choose Thailand, India, Philippines or UAE.");
        model.Currency = (model.Currency ?? "").Trim().ToUpperInvariant();
        if (!Markets.Currencies.Contains(model.Currency))
            ModelState.AddModelError(nameof(model.Currency), "Choose THB, INR, PHP or AED.");
        if (!Markets.TimeZoneIds.Contains(model.TimeZoneId))
            ModelState.AddModelError(nameof(model.TimeZoneId), "Choose one of the listed time zones.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? MergeSecret(string? saved, string? posted, ClientFormModel model, string field)
    {
        if (model.ClearSecrets.Contains(field)) return null;
        return string.IsNullOrWhiteSpace(posted) ? saved : posted.Trim();
    }

    /// <summary>Fills non-secret values and masked hints from the stored settings. Returns false if they can't be read.</summary>
    private bool LoadSettingsInto(ClientConfig client, ClientFormModel model)
    {
        try
        {
            var settings = _settings.Read(client.IntegrationSettingsJson);
            model.WhatsAppPhoneNumberId = settings.WhatsAppPhoneNumberId;
            model.PosFirstBaseUrl = settings.PosFirstBaseUrl;
            model.MerkaiBaseUrl = settings.MerkaiBaseUrl;
            FillHints(settings, model);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return false;
        }
    }

    private static void FillHints(ClientIntegrationSettings settings, ClientFormModel model)
    {
        // Never send a saved secret back to the browser - only a masked hint.
        model.LineChannelSecret = model.LineChannelAccessToken = model.WhatsAppAccessToken = null;
        model.MessengerPageAccessToken = model.PosFirstApiKey = model.MerkaiApiKey = null;

        void Hint(string field, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) model.SavedSecretHints[field] = ClientIntegrationSettings.Mask(value);
        }
        Hint(nameof(model.LineChannelSecret), settings.LineChannelSecret);
        Hint(nameof(model.LineChannelAccessToken), settings.LineChannelAccessToken);
        Hint(nameof(model.WhatsAppAccessToken), settings.WhatsAppAccessToken);
        Hint(nameof(model.MessengerPageAccessToken), settings.MessengerPageAccessToken);
        Hint(nameof(model.PosFirstApiKey), settings.PosFirstApiKey);
        Hint(nameof(model.MerkaiApiKey), settings.MerkaiApiKey);
    }

    private void ValidateWorkflow(WorkflowFormModel model, ClientConfig client)
    {
        var handler = HandlerCatalog.Find(model.HandlerKey);
        if (handler is null)
        {
            ModelState.AddModelError(nameof(model.HandlerKey), "Pick a handler from the list.");
            return;
        }
        model.HandlerKey = handler.Key;

        if (handler.IsMessageHandler)
        {
            // Message handlers only make sense as MessageReceived; the channel decides which webhook feeds them.
            model.TriggerType = TriggerType.MessageReceived;
            model.Channel = Channels.Normalize(model.Channel) ?? "";
        }
        else
        {
            if (model.TriggerType == TriggerType.MessageReceived)
                ModelState.AddModelError(nameof(model.TriggerType), $"\"{handler.Name}\" does not answer customer messages - choose {handler.DefaultTrigger}.");
            model.Channel = "";
        }

        var json = string.IsNullOrWhiteSpace(model.TriggerConfigJson) ? "{}" : model.TriggerConfigJson.Trim();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                ModelState.AddModelError(nameof(model.TriggerConfigJson), "Trigger config must be a JSON object, e.g. { \"cron\": \"0 7 * * *\" }.");
        }
        catch (JsonException)
        {
            ModelState.AddModelError(nameof(model.TriggerConfigJson), "Trigger config is not valid JSON.");
        }
        model.TriggerConfigJson = json;

        if (model.IsEnabled)
        {
            var clash = FindChannelClash(client, model.Id, model.TriggerType, string.IsNullOrEmpty(model.Channel) ? null : model.Channel);
            if (clash is not null) ModelState.AddModelError(nameof(model.Channel), ClashMessage(clash));
        }
    }

    /// <summary>One enabled message workflow per channel per client - otherwise two bots would answer.</summary>
    private static WorkflowDefinition? FindChannelClash(ClientConfig client, Guid? selfId, TriggerType triggerType, string? channel)
    {
        if (triggerType != TriggerType.MessageReceived) return null;
        return client.Workflows.FirstOrDefault(w =>
            w.Id != selfId && w.IsEnabled && w.TriggerType == TriggerType.MessageReceived && w.Channel == channel);
    }

    private static string ClashMessage(WorkflowDefinition clash) =>
        $"This client already has an enabled message workflow for {Channels.DisplayName(clash.Channel)} (\"{clash.Name}\"). " +
        "Disable that one first, or pick another channel.";

    private static void CopyWorkflow(WorkflowFormModel model, WorkflowDefinition workflow)
    {
        workflow.Name = model.Name.Trim();
        workflow.HandlerKey = model.HandlerKey;
        workflow.Vertical = model.Vertical;
        workflow.TriggerType = model.TriggerType;
        workflow.Channel = string.IsNullOrEmpty(model.Channel) ? null : model.Channel;
        workflow.TriggerConfigJson = model.TriggerConfigJson;
        workflow.IsEnabled = model.IsEnabled;
    }
}
