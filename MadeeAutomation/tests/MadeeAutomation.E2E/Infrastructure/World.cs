using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Security;
using MadeeAutomation.Domain.Settings;

namespace MadeeAutomation.E2E.Infrastructure;

/// <summary>A client seeded before the tests run.</summary>
public record SeededClient(Guid Id, string Code, string LineSecret, Guid? WorkflowId);

/// <summary>
/// What the run created before the tests (all in MadeeAutomation_E2E):
///
///   bakery    Active, LINE secret+token, POSFirst -> fake, knowledge base + menu, customer-message on LINE
///   nosecret  Active, LINE token but NO channel secret              -> webhook must be refused
///   paused    Paused, LINE secret+token, workflow                   -> messages Skipped
///   noflow    Active, LINE secret+token, NO workflow                -> messages Skipped
///   failsend  Active, LINE token "fail-token" (fake LINE answers 500) -> run Failed
///   toggle    Active, LINE secret+token, workflow the UI test disables/enables
///   other     Active, LINE secret+token, workflow                   -> cross-client URL check
///   legacy    Active, credentials stored as OLD plain JSON (not encrypted) -> still works
///   indiacafe India / INR / Asia/Kolkata, menu "Catering package - 125000" -> lakh formatting + IST times
///   spicecafe India / INR, STRUCTURED menu (chai with aliases "chai, tea", samosa, paneer roll SOLD OUT),
///             hours Mon-Sat 07:00-14:00 + 16:00-22:00, Sunday closed, closed date in 10 days ("Diwali")
///   menuedit  Thailand, old text menu only -> menu page CRUD + import
///   hoursedit UAE / AED / Asia/Dubai, no hours -> hours page UI
///   cakeshop  Thailand, NO POS, staff LINE user + staff email -> "New order" messages, own ORD- references,
///             daily limit (Birthday cake: 3 a day)
///   bigmenu   30 menu items (12 Drinks, 12 Cakes, 6 with no category; 3 sold out; 5 with a daily limit) and
///             45 orders (30 New, 8 Accepted, 4 Completed, 3 Cancelled; 9 for tomorrow; 5 from "Somchai")
///             -> search, filters and paging. Nothing else may add data to this client.
///   buttoncafe Thailand, NO POS, staff LINE user. Menu: Drinks (Iced latte 75, Thai tea 45 with a daily limit
///             of 3), Bakery (Croissant 45, Brownie 60 SOLD OUT) -> the menu-buttons chat (Round 008)
///   buttonoff  like buttoncafe with one item; the test switches the menu buttons off
///   taxincluded Thailand, VAT 7% INCLUDED in menu prices; no POS; staff LINE user
///   taxadded    India, GST 5% ADDED on top of menu prices; POSFirst connected
///   taxform     Thailand, no tax - the test sets and clears it on the client's Edit page
///   portalcafe Thailand, NO POS and no staff channel; has client portal users (password World.PortalPassword):
///             owner@portalcafe.test, second@portalcafe.test (deactivation test)
///
/// Admin accounts (password for all: World.AdminPassword)
///   admin@e2e.test          used by every Admin UI test
///   lockme / resetme / changeme / deactivateme @e2e.test - one account per auth test, so they don't interfere
/// Staff alert emails: bakery -> owner@bakery.test, failsend -> ops@failsend.test
/// </summary>
public static class World
{
    public static SeededClient Bakery { get; private set; } = null!;
    public static SeededClient NoSecret { get; private set; } = null!;
    public static SeededClient Paused { get; private set; } = null!;
    public static SeededClient NoFlow { get; private set; } = null!;
    public static SeededClient FailSend { get; private set; } = null!;
    public static SeededClient Toggle { get; private set; } = null!;
    public static SeededClient Other { get; private set; } = null!;
    public static SeededClient Legacy { get; private set; } = null!;
    public static SeededClient IndiaCafe { get; private set; } = null!;
    public static SeededClient SpiceCafe { get; private set; } = null!;
    public static SeededClient MenuEdit { get; private set; } = null!;
    public static SeededClient HoursEdit { get; private set; } = null!;
    public static SeededClient CakeShop { get; private set; } = null!;
    public static SeededClient PortalCafe { get; private set; } = null!;
    public static SeededClient BigMenu { get; private set; } = null!;
    public static SeededClient ButtonCafe { get; private set; } = null!;
    public static SeededClient ButtonOff { get; private set; } = null!;
    public static SeededClient TaxIncluded { get; private set; } = null!;
    public static SeededClient TaxAdded { get; private set; } = null!;
    public static SeededClient TaxForm { get; private set; } = null!;
    // Phase 4: scheduler, retries, POS down
    public static SeededClient SchedOk { get; private set; } = null!;
    public static SeededClient SchedFail { get; private set; } = null!;
    public static SeededClient SchedPaused { get; private set; } = null!;
    public static SeededClient SchedForm { get; private set; } = null!;
    public static SeededClient PosDown { get; private set; } = null!;
    public static Guid SchedOkJob, SchedFailJob, SchedPausedJob, SchedFormJob;
    public static readonly string PosDownStaffLineId = "Uflaky" + new string('g', 27);
    // Phase 5: WhatsApp + Messenger
    public static SeededClient MetaCafe { get; private set; } = null!;
    public static SeededClient MetaNoSecret { get; private set; } = null!;
    public const string MetaSecret = "e2e-meta-app-secret";
    public const string MetaVerifyToken = "e2e-verify-me";
    public const string MetaPhoneNumberId = "100000000000001";
    public static readonly string TaxStaffLineId = "U" + new string('e', 32);
    public static readonly string ButtonCafeStaffLineId = "U" + new string('d', 32);

    public const string PortalEmail = "owner@portalcafe.test";
    public const string PortalSecondEmail = "second@portalcafe.test";
    public const string PortalPassword = "E2ePortal2026x";
    public static readonly string CakeShopStaffLineId = "U" + new string('c', 32);

    public const string AdminEmail = "admin@e2e.test";
    public const string AdminPassword = "E2eAdmin2026x";

    public const string BakeryKnowledgeBase = "Opening hours: 7am-6pm every day\nDelivery: Ban Chang only, free over 300 THB";
    public const string BakeryMenu = "Croissant - 45 THB\nSourdough loaf - 180 THB";
    public const string IndiaMenu = "Catering package - 125000\nMasala chai - 30";
    public const string MenuEditText = "Croissant - 45\nSourdough loaf - 180\nBirthday cake";

    /// <summary>Unique per run, so names never clash with a previous run's leftovers.</summary>
    public static string RunId { get; } = DateTime.UtcNow.ToString("HHmmss");

    public static async Task BuildAsync(TestDatabase db, E2ESettings settings)
    {
        var key = SettingsCipher.ParseKey(E2ESettings.EncryptionKey);

        string Encrypted(ClientIntegrationSettings s) => SettingsCipher.Encrypt(s.ToJson(), key);

        ClientIntegrationSettings Line(string secret, string token) => new()
        {
            LineChannelSecret = secret,
            LineChannelAccessToken = token
        };

        foreach (var (email, name) in new[]
                 {
                     (AdminEmail, "E2E Admin"), ("lockme@e2e.test", "Lock Me"), ("resetme@e2e.test", "Reset Me"),
                     ("changeme@e2e.test", "Change Me"), ("deactivateme@e2e.test", "Deactivate Me")
                 })
        {
            await AddAdminAsync(db, email, name, AdminPassword);
        }

        Bakery = await AddClientAsync(db, "bakery", "E2E Bakery", ClientStatus.Active,
            Encrypted(new ClientIntegrationSettings
            {
                LineChannelSecret = "bakery-secret",
                LineChannelAccessToken = "bakery-token-1234",
                PosFirstBaseUrl = settings.FakeServerUrl,
                PosFirstApiKey = "pos-key-bakery"
            }), "bakery-secret", withWorkflow: true, kb: BakeryKnowledgeBase, menu: BakeryMenu, staffEmail: "owner@bakery.test");

        NoSecret = await AddClientAsync(db, "nosecret", "E2E No Secret", ClientStatus.Active,
            Encrypted(new ClientIntegrationSettings { LineChannelAccessToken = "nosecret-token" }), "", withWorkflow: true);

        Paused = await AddClientAsync(db, "paused", "E2E Paused", ClientStatus.Paused,
            Encrypted(Line("paused-secret", "paused-token")), "paused-secret", withWorkflow: true);

        NoFlow = await AddClientAsync(db, "noflow", "E2E No Workflow", ClientStatus.Active,
            Encrypted(Line("noflow-secret", "noflow-token")), "noflow-secret", withWorkflow: false);

        FailSend = await AddClientAsync(db, "failsend", "E2E Fail Send", ClientStatus.Active,
            Encrypted(Line("failsend-secret", "fail-token")), "failsend-secret", withWorkflow: true, staffEmail: "ops@failsend.test");

        Toggle = await AddClientAsync(db, "toggle", "E2E Toggle", ClientStatus.Active,
            Encrypted(Line("toggle-secret", "toggle-token")), "toggle-secret", withWorkflow: true);

        Other = await AddClientAsync(db, "other", "E2E Other", ClientStatus.Active,
            Encrypted(Line("other-secret", "other-token")), "other-secret", withWorkflow: true);

        IndiaCafe = await AddClientAsync(db, "indiacafe", "E2E India Cafe", ClientStatus.Active,
            Encrypted(new ClientIntegrationSettings
            {
                LineChannelSecret = "india-secret",
                LineChannelAccessToken = "india-token",
                PosFirstBaseUrl = settings.FakeServerUrl,
                PosFirstApiKey = "pos-key-india"
            }), "india-secret", withWorkflow: true, menu: IndiaMenu, country: "IN", currency: "INR", timeZone: "Asia/Kolkata");

        SpiceCafe = await AddClientAsync(db, "spicecafe", "E2E Spice Cafe", ClientStatus.Active,
            Encrypted(new ClientIntegrationSettings
            {
                LineChannelSecret = "spice-secret",
                LineChannelAccessToken = "spice-token",
                PosFirstBaseUrl = settings.FakeServerUrl,
                PosFirstApiKey = "pos-key-spice"
            }), "spice-secret", withWorkflow: true, kb: "Delivery: Indiranagar only.", country: "IN", currency: "INR", timeZone: "Asia/Kolkata");
        await AddMenuItemAsync(db, SpiceCafe.Id, "Masala chai", "Drinks", 60m, "chai, tea", true, 0);
        await AddMenuItemAsync(db, SpiceCafe.Id, "Samosa", "Snacks", 25m, null, true, 1);
        await AddMenuItemAsync(db, SpiceCafe.Id, "Paneer roll", "Snacks", 120m, null, false, 2);
        for (var day = 1; day <= 6; day++) // Mon-Sat; Sunday closed
        {
            await AddHoursAsync(db, SpiceCafe.Id, day, "07:00", "14:00");
            await AddHoursAsync(db, SpiceCafe.Id, day, "16:00", "22:00");
        }
        await db.ExecAsync("INSERT INTO dbo.ClosedDates (Id, ClientConfigId, [Date], Reason) VALUES (NEWID(), @c, @d, 'Diwali');",
            ("@c", SpiceCafe.Id), ("@d", DateTime.UtcNow.Date.AddDays(10)));

        MenuEdit = await AddClientAsync(db, "menuedit", "E2E Menu Edit", ClientStatus.Active,
            Encrypted(Line("menuedit-secret", "menuedit-token")), "menuedit-secret", withWorkflow: true, menu: MenuEditText);

        HoursEdit = await AddClientAsync(db, "hoursedit", "E2E Hours Edit", ClientStatus.Active,
            Encrypted(Line("hoursedit-secret", "hoursedit-token")), "hoursedit-secret", withWorkflow: true,
            country: "AE", currency: "AED", timeZone: "Asia/Dubai");

        CakeShop = await AddClientAsync(db, "cakeshop", "E2E Cake Shop", ClientStatus.Active,
            Encrypted(Line("cakeshop-secret", "cakeshop-token")), "cakeshop-secret", withWorkflow: true,
            kb: "Delivery: Ban Chang only.", staffEmail: "owner@cakeshop.test", staffLine: CakeShopStaffLineId);
        await AddMenuItemAsync(db, CakeShop.Id, "Birthday cake", "Cakes", 500m, null, true, 0, dailyLimit: 3);
        await AddMenuItemAsync(db, CakeShop.Id, "Thai tea", "Drinks", 55m, "tea", true, 1);
        await AddMenuItemAsync(db, CakeShop.Id, "Samosa", "Snacks", 25m, null, true, 2);

        PortalCafe = await AddClientAsync(db, "portalcafe", "E2E Portal Cafe", ClientStatus.Active,
            Encrypted(Line("portalcafe-secret", "portalcafe-token")), "portalcafe-secret", withWorkflow: true,
            kb: "Delivery: Ban Chang only.");
        await AddMenuItemAsync(db, PortalCafe.Id, "Thai tea", "Drinks", 55m, "tea", true, 0);
        await AddMenuItemAsync(db, PortalCafe.Id, "Samosa", "Snacks", 25m, null, true, 1);
        await AddAdminAsync(db, PortalEmail, "Portal Owner", PortalPassword, PortalCafe.Id);
        await AddAdminAsync(db, PortalSecondEmail, "Second User", PortalPassword, PortalCafe.Id);

        BigMenu = await AddClientAsync(db, "bigmenu", "E2E Big Menu", ClientStatus.Active,
            Encrypted(Line("bigmenu-secret", "bigmenu-token")), "bigmenu-secret", withWorkflow: true);
        for (var n = 1; n <= 30; n++)
        {
            await db.ExecAsync(@"
                INSERT INTO dbo.MenuItems (Id, ClientConfigId, Name, Category, Price, Aliases, IsAvailable, SortOrder, CreatedAtUtc, DailyLimit)
                VALUES (NEWID(), @c, @name, @cat, @price, @aliases, @avail, @sort, SYSUTCDATETIME(), @limit);",
                ("@c", BigMenu.Id), ("@name", (n <= 12 ? "Coffee " : n <= 24 ? "Cake " : "Special ") + n.ToString("00")),
                ("@cat", n <= 12 ? "Drinks" : n <= 24 ? "Cakes" : null), ("@price", 40m + n),
                ("@aliases", n == 7 ? "kafae yen, กาแฟเย็น" : null), ("@avail", n % 10 != 0), ("@sort", n),
                ("@limit", n % 6 == 0 ? 5 : null));
        }
        var bangkokToday = DateTime.UtcNow.AddHours(7).Date;
        for (var n = 1; n <= 45; n++)
        {
            var orderId = Guid.NewGuid();
            await db.ExecAsync(@"
                INSERT INTO dbo.Orders (Id, ClientConfigId, OrderNumber, Reference, Channel, CustomerId, CustomerName, CustomerMessage,
                    Currency, Total, OrderDate, Status, CreatedAtUtc)
                VALUES (@id, @c, @n, @ref, 'line', @cust, @name, @msg, 'THB', @total, @day, @status, @created);",
                ("@id", orderId), ("@c", BigMenu.Id), ("@n", n), ("@ref", $"ORD-{n:0000}"), ("@cust", "Ubigmenu" + n),
                ("@name", n % 9 == 0 ? "Somchai" : "Customer " + n), ("@msg", "order " + n), ("@total", 100m + n),
                ("@day", n % 5 == 0 ? bangkokToday.AddDays(1) : bangkokToday),
                ("@status", n <= 30 ? 1 : n <= 38 ? 2 : n <= 42 ? 3 : 4), ("@created", DateTime.UtcNow.AddMinutes(-200 + n)));
            await db.ExecAsync(@"
                INSERT INTO dbo.OrderItems (Id, OrderId, Name, Quantity, UnitPrice, LineTotal)
                VALUES (NEWID(), @o, @name, 1, @price, @price);",
                ("@o", orderId), ("@name", n % 2 == 0 ? "Coffee 01" : "Cake 13"), ("@price", 100m + n));
        }

        ButtonCafe = await AddClientAsync(db, "buttoncafe", "E2E Button Cafe", ClientStatus.Active,
            Encrypted(Line("buttoncafe-secret", "buttoncafe-token")), "buttoncafe-secret", withWorkflow: true,
            kb: "Delivery: Ban Chang only.", staffLine: ButtonCafeStaffLineId);
        await AddMenuItemAsync(db, ButtonCafe.Id, "Iced latte", "Drinks", 75m, "latte", true, 0);
        await AddMenuItemAsync(db, ButtonCafe.Id, "Thai tea", "Drinks", 45m, "tea", true, 1, dailyLimit: 3);
        await AddMenuItemAsync(db, ButtonCafe.Id, "Croissant", "Bakery", 45m, null, true, 2);
        await AddMenuItemAsync(db, ButtonCafe.Id, "Brownie", "Bakery", 60m, null, false, 3);

        ButtonOff = await AddClientAsync(db, "buttonoff", "E2E Button Off", ClientStatus.Active,
            Encrypted(Line("buttonoff-secret", "buttonoff-token")), "buttonoff-secret", withWorkflow: true);
        await AddMenuItemAsync(db, ButtonOff.Id, "Thai tea", "Drinks", 45m, "tea", true, 0);
        await AddMenuItemAsync(db, ButtonOff.Id, "Croissant", "Bakery", 45m, null, true, 1);

        TaxIncluded = await AddClientAsync(db, "taxincluded", "E2E Tax Included", ClientStatus.Active,
            Encrypted(Line("taxincluded-secret", "taxincluded-token")), "taxincluded-secret", withWorkflow: true, staffLine: TaxStaffLineId);
        await AddMenuItemAsync(db, TaxIncluded.Id, "Thai tea", "Drinks", 55m, "tea", true, 0);
        await db.ExecAsync("UPDATE dbo.ClientConfigs SET TaxName = 'VAT', TaxRatePercent = 7, PricesIncludeTax = 1 WHERE Id = @c", ("@c", TaxIncluded.Id));

        TaxAdded = await AddClientAsync(db, "taxadded", "E2E Tax Added", ClientStatus.Active,
            Encrypted(new ClientIntegrationSettings
            {
                LineChannelSecret = "taxadded-secret",
                LineChannelAccessToken = "taxadded-token",
                PosFirstBaseUrl = settings.FakeServerUrl,
                PosFirstApiKey = "pos-key-taxadded"
            }), "taxadded-secret", withWorkflow: true, country: "IN", currency: "INR", timeZone: "Asia/Kolkata");
        await AddMenuItemAsync(db, TaxAdded.Id, "Samosa", "Snacks", 25m, null, true, 0);
        await db.ExecAsync("UPDATE dbo.ClientConfigs SET TaxName = 'GST', TaxRatePercent = 5, PricesIncludeTax = 0 WHERE Id = @c", ("@c", TaxAdded.Id));

        TaxForm = await AddClientAsync(db, "taxform", "E2E Tax Form", ClientStatus.Active,
            Encrypted(Line("taxform-secret", "taxform-token")), "taxform-secret", withWorkflow: true);
        await AddMenuItemAsync(db, TaxForm.Id, "Samosa", "Snacks", 25m, null, true, 0);

        // ---- Phase 4 ----
        // A NULL NextRunAtUtc would let WebApi work the first time out as soon as it starts - that is tested too.
        ClientIntegrationSettings Pos(string name, string key) => new()
        {
            LineChannelSecret = name + "-secret", LineChannelAccessToken = name + "-token",
            PosFirstBaseUrl = settings.FakeServerUrl, PosFirstApiKey = key
        };
        SchedOk = await AddClientAsync(db, "schedok", "E2E Sched OK", ClientStatus.Active, Encrypted(Pos("schedok", "pos-key-schedok")),
            "schedok-secret", withWorkflow: false, staffEmail: "owner@schedok.test");
        SchedOkJob = await AddScheduledAsync(db, SchedOk.Id, "Morning low-stock check", "{\"days\":[],\"times\":[\"07:00\"]}");

        SchedFail = await AddClientAsync(db, "schedfail", "E2E Sched Fail", ClientStatus.Active, Encrypted(Pos("schedfail", "lowstock-down")),
            "schedfail-secret", withWorkflow: false, staffEmail: "owner@schedfail.test", country: "PH", currency: "PHP", timeZone: "Asia/Manila");
        SchedFailJob = await AddScheduledAsync(db, SchedFail.Id, "Manila low-stock check", "{\"cron\":\"30 6 * * *\"}");

        SchedPaused = await AddClientAsync(db, "schedpaused", "E2E Sched Paused", ClientStatus.Paused, Encrypted(Pos("schedpaused", "pos-key-schedpaused")),
            "schedpaused-secret", withWorkflow: false, staffEmail: "owner@schedpaused.test");
        SchedPausedJob = await AddScheduledAsync(db, SchedPaused.Id, "Paused low-stock check", "{\"days\":[],\"times\":[\"07:00\"]}");

        SchedForm = await AddClientAsync(db, "schedform", "E2E Sched Form", ClientStatus.Active, Encrypted(Pos("schedform", "pos-key-schedform")),
            "schedform-secret", withWorkflow: false, country: "AE", currency: "AED", timeZone: "Asia/Dubai");
        SchedFormJob = await AddScheduledAsync(db, SchedForm.Id, "Dubai stock check", "{\"days\":[],\"times\":[\"07:00\"]}", enabled: false);

        PosDown = await AddClientAsync(db, "posdown", "E2E POS Down", ClientStatus.Active, Encrypted(Pos("posdown", "pos-down-e2e")),
            "posdown-secret", withWorkflow: true, country: "AE", currency: "AED", timeZone: "Asia/Dubai", staffLine: PosDownStaffLineId);
        await AddMenuItemAsync(db, PosDown.Id, "Shawarma", "Wraps", 18m, null, true, 0);

        // ---- Phase 5: WhatsApp + Messenger (Philippines) ----
        MetaCafe = await AddClientAsync(db, "metacafe", "E2E Meta Cafe", ClientStatus.Active, Encrypted(new ClientIntegrationSettings
            {
                WhatsAppPhoneNumberId = MetaPhoneNumberId, WhatsAppAccessToken = "wa-token-e2e", MessengerPageAccessToken = "fb-token-e2e",
                MetaAppSecret = MetaSecret, MetaVerifyToken = MetaVerifyToken
            }), MetaSecret, withWorkflow: false, country: "PH", currency: "PHP", timeZone: "Asia/Manila", staffEmail: "owner@metacafe.test");
        await AddChannelWorkflowAsync(db, MetaCafe.Id, "WhatsApp customer messages", "whatsapp");
        await AddChannelWorkflowAsync(db, MetaCafe.Id, "Messenger customer messages", "messenger");
        await AddMenuItemAsync(db, MetaCafe.Id, "Iced coffee", "Drinks", 120m, "coffee", true, 0);
        await AddMenuItemAsync(db, MetaCafe.Id, "Calamansi juice with honey and mint", "Drinks", 95m, null, true, 1);
        await AddMenuItemAsync(db, MetaCafe.Id, "Ensaymada", "Bakery", 60m, null, true, 2);
        await AddMenuItemAsync(db, MetaCafe.Id, "Pandesal", "Bakery", 10m, null, false, 3);

        MetaNoSecret = await AddClientAsync(db, "metanosecret", "E2E Meta No Secret", ClientStatus.Active, Encrypted(new ClientIntegrationSettings
            {
                WhatsAppPhoneNumberId = "100000000000002", WhatsAppAccessToken = "wa-token-e2e-2"
            }), "", withWorkflow: false, country: "AE", currency: "AED", timeZone: "Asia/Dubai");
        await AddChannelWorkflowAsync(db, MetaNoSecret.Id, "WhatsApp customer messages", "whatsapp");

        // Old rows hold plain JSON - the app must still read them (and encrypt on the next save).
        Legacy = await AddClientAsync(db, "legacy", "E2E Legacy", ClientStatus.Active,
            Line("legacy-secret", "legacy-token").ToJson(), "legacy-secret", withWorkflow: true);
    }

    private static async Task<SeededClient> AddClientAsync(TestDatabase db, string code, string name, ClientStatus status,
        string settingsValue, string secret, bool withWorkflow, string? kb = null, string? menu = null, string? staffEmail = null,
        string country = "TH", string currency = "THB", string timeZone = "Asia/Bangkok", string? staffLine = null)
    {
        var id = Guid.NewGuid();
        await db.ExecAsync(@"
            INSERT INTO dbo.ClientConfigs (Id, ClientName, ClientCode, Country, TimeZoneId, Currency, Vertical, Status,
                IntegrationSettingsJson, MerkaiIntegrationEnabled, PosFirstIntegrationEnabled, CreatedAtUtc, KnowledgeBaseText, MenuText, StaffAlertEmail, StaffAlertLineUserId)
            VALUES (@id, @name, @code, @country, @tz, @currency, 1, @status, @settings, 0, 1, SYSUTCDATETIME(), @kb, @menu, @staff, @staffLine);",
            ("@staffLine", staffLine),
            ("@id", id), ("@name", name), ("@code", code), ("@status", (int)status), ("@settings", settingsValue),
            ("@kb", kb), ("@menu", menu), ("@staff", staffEmail), ("@country", country), ("@tz", timeZone), ("@currency", currency));

        Guid? workflowId = null;
        if (withWorkflow)
        {
            workflowId = Guid.NewGuid();
            await db.ExecAsync(@"
                INSERT INTO dbo.WorkflowDefinitions (Id, ClientConfigId, Name, HandlerKey, Vertical, TriggerType, Channel,
                    TriggerConfigJson, IsEnabled, CreatedAtUtc)
                VALUES (@id, @client, 'LINE customer messages', 'customer-message', 1, 3, 'line', '{}', 1, SYSUTCDATETIME());",
                ("@id", workflowId), ("@client", id));
        }

        return new SeededClient(id, code, secret, workflowId);
    }

    private static Task AddChannelWorkflowAsync(TestDatabase db, Guid clientId, string name, string channel) =>
        db.ExecAsync(@"
            INSERT INTO dbo.WorkflowDefinitions (Id, ClientConfigId, Name, HandlerKey, Vertical, TriggerType, Channel,
                TriggerConfigJson, IsEnabled, CreatedAtUtc, RetryAttempt)
            VALUES (NEWID(), @client, @name, 'customer-message', 1, 3, @channel, '{}', 1, SYSUTCDATETIME(), 0);",
            ("@client", clientId), ("@name", name), ("@channel", channel));

    private static async Task<Guid> AddScheduledAsync(TestDatabase db, Guid clientId, string name, string scheduleJson, bool enabled = true)
    {
        var id = Guid.NewGuid();
        await db.ExecAsync(@"
            INSERT INTO dbo.WorkflowDefinitions (Id, ClientConfigId, Name, HandlerKey, Vertical, TriggerType, Channel,
                TriggerConfigJson, IsEnabled, CreatedAtUtc, RetryAttempt)
            VALUES (@id, @client, @name, 'low-stock-alert', 1, 2, NULL, @json, @enabled, SYSUTCDATETIME(), 0);",
            ("@id", id), ("@client", clientId), ("@name", name), ("@json", scheduleJson), ("@enabled", enabled));
        return id;
    }

    private static Task AddMenuItemAsync(TestDatabase db, Guid clientId, string name, string category, decimal price,
        string? aliases, bool available, int sort, int? dailyLimit = null) =>
        db.ExecAsync(@"
            INSERT INTO dbo.MenuItems (Id, ClientConfigId, Name, Category, Price, Aliases, IsAvailable, SortOrder, CreatedAtUtc, DailyLimit)
            VALUES (NEWID(), @c, @name, @cat, @price, @aliases, @avail, @sort, SYSUTCDATETIME(), @limit);",
            ("@limit", dailyLimit),
            ("@c", clientId), ("@name", name), ("@cat", category), ("@price", price), ("@aliases", aliases),
            ("@avail", available), ("@sort", sort));

    private static Task AddHoursAsync(TestDatabase db, Guid clientId, int dayOfWeek, string open, string close) =>
        db.ExecAsync("INSERT INTO dbo.OpeningHours (Id, ClientConfigId, DayOfWeek, OpenTime, CloseTime) VALUES (NEWID(), @c, @d, @o, @cl);",
            ("@c", clientId), ("@d", dayOfWeek), ("@o", TimeSpan.Parse(open)), ("@cl", TimeSpan.Parse(close)));

    private static async Task AddAdminAsync(TestDatabase db, string email, string name, string password, Guid? clientConfigId = null)
    {
        var user = new AdminUser { Email = email, DisplayName = name };
        var hash = new Microsoft.AspNetCore.Identity.PasswordHasher<AdminUser>().HashPassword(user, password);
        await db.ExecAsync(@"
            INSERT INTO dbo.AdminUsers (Id, Email, DisplayName, PasswordHash, SecurityStamp, IsActive, MustChangePassword,
                FailedLoginCount, CreatedAtUtc, ClientConfigId)
            VALUES (@id, @email, @name, @hash, @stamp, 1, 0, 0, SYSUTCDATETIME(), @client);",
            ("@client", clientConfigId),
            ("@id", user.Id), ("@email", email), ("@name", name), ("@hash", hash), ("@stamp", user.SecurityStamp));
    }
}
