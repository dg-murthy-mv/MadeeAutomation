using MadeeAutomation.E2E.Infrastructure;
using NUnit.Framework;

// Must be the PARENT namespace of the tests, or NUnit does not run it before them.
namespace MadeeAutomation.E2E;

/// <summary>
/// Runs once before all tests: fake server -> fresh E2E database (scripts in /database) -> seed World
/// -> start WebApi + Admin.Web. Stops everything after the last test.
/// </summary>
[SetUpFixture]
public class GlobalSetup
{
    public static E2ESettings Settings { get; private set; } = null!;
    public static TestDatabase Db { get; private set; } = null!;
    public static FakeServer Fakes { get; private set; } = null!;
    private static AppHost? _apps;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        Settings = E2ESettings.Load();

        Fakes = new FakeServer();
        await Fakes.StartAsync(Settings.FakeServerUrl);

        Db = new TestDatabase(Settings);
        await Db.RecreateAsync();
        await World.BuildAsync(Db, Settings);

        _apps = new AppHost();
        await _apps.StartAsync(Settings);

        TestContext.Progress.WriteLine($"E2E ready - WebApi {Settings.WebApiUrl}, Admin {Settings.AdminUrl}, logs in {AppHost.LogFolder}");
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_apps is not null) await _apps.DisposeAsync();
        if (Fakes is not null) await Fakes.DisposeAsync();
    }
}
