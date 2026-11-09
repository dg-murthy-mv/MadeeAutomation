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

    public const string AdminEmail = "admin@e2e.test";
    public const string AdminPassword = "E2eAdmin2026x";

    public const string BakeryKnowledgeBase = "Opening hours: 7am-6pm every day\nDelivery: Ban Chang only, free over 300 THB";
    public const string BakeryMenu = "Croissant - 45 THB\nSourdough loaf - 180 THB";
    public const string IndiaMenu = "Catering package - 125000\nMasala chai - 30";

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

        // Old rows hold plain JSON - the app must still read them (and encrypt on the next save).
        Legacy = await AddClientAsync(db, "legacy", "E2E Legacy", ClientStatus.Active,
            Line("legacy-secret", "legacy-token").ToJson(), "legacy-secret", withWorkflow: true);
    }

    private static async Task<SeededClient> AddClientAsync(TestDatabase db, string code, string name, ClientStatus status,
        string settingsValue, string secret, bool withWorkflow, string? kb = null, string? menu = null, string? staffEmail = null,
        string country = "TH", string currency = "THB", string timeZone = "Asia/Bangkok")
    {
        var id = Guid.NewGuid();
        await db.ExecAsync(@"
            INSERT INTO dbo.ClientConfigs (Id, ClientName, ClientCode, Country, TimeZoneId, Currency, Vertical, Status,
                IntegrationSettingsJson, MerkaiIntegrationEnabled, PosFirstIntegrationEnabled, CreatedAtUtc, KnowledgeBaseText, MenuText, StaffAlertEmail)
            VALUES (@id, @name, @code, @country, @tz, @currency, 1, @status, @settings, 0, 1, SYSUTCDATETIME(), @kb, @menu, @staff);",
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

    private static async Task AddAdminAsync(TestDatabase db, string email, string name, string password)
    {
        var user = new AdminUser { Email = email, DisplayName = name };
        var hash = new Microsoft.AspNetCore.Identity.PasswordHasher<AdminUser>().HashPassword(user, password);
        await db.ExecAsync(@"
            INSERT INTO dbo.AdminUsers (Id, Email, DisplayName, PasswordHash, SecurityStamp, IsActive, MustChangePassword,
                FailedLoginCount, CreatedAtUtc)
            VALUES (@id, @email, @name, @hash, @stamp, 1, 0, 0, SYSUTCDATETIME());",
            ("@id", user.Id), ("@email", email), ("@name", name), ("@hash", hash), ("@stamp", user.SecurityStamp));
    }
}
