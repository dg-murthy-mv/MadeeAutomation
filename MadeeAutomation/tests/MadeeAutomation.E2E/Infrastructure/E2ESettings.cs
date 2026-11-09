using System.Text.Json;

namespace MadeeAutomation.E2E.Infrastructure;

/// <summary>
/// Test-run settings from e2e.settings.json. Any value can be overridden with an environment
/// variable named MADEE_E2E_{Key} (e.g. MADEE_E2E_SqlServer) - handy on another PC or in CI.
/// </summary>
public class E2ESettings
{
    public string SqlServer { get; set; } = "Server=DG_MURTHY;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";
    public string DatabaseName { get; set; } = "MadeeAutomation_E2E";
    public int WebApiPort { get; set; } = 58801;
    public int AdminPort { get; set; } = 58802;
    public int FakeServerPort { get; set; } = 58803;
    public string Configuration { get; set; } = "Debug";
    public int StartupTimeoutSeconds { get; set; } = 60;

    /// <summary>Fixed test-only key (NOT your real one). The apps under test get it via environment variable.</summary>
    public const string EncryptionKey = "bWFkZWUtYXV0b21hdGlvbi1lMmUtdGVzdC1rZXktMzI=";

    /// <summary>Shared Admin.Web/WebApi key for internal endpoints (test-only value).</summary>
    public const string InternalApiKey = "e2e-internal-key-123";

    public string WebApiUrl => $"http://127.0.0.1:{WebApiPort}";
    public string AdminUrl => $"http://127.0.0.1:{AdminPort}";
    public string FakeServerUrl => $"http://127.0.0.1:{FakeServerPort}";

    public string AppConnectionString
    {
        get
        {
            var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(SqlServer) { InitialCatalog = DatabaseName };
            return b.ConnectionString;
        }
    }

    public static E2ESettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "e2e.settings.json");
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<E2ESettings>(File.ReadAllText(path)) ?? new E2ESettings()
            : new E2ESettings();

        foreach (var prop in typeof(E2ESettings).GetProperties().Where(p => p.CanWrite))
        {
            var env = Environment.GetEnvironmentVariable("MADEE_E2E_" + prop.Name);
            if (string.IsNullOrWhiteSpace(env)) continue;
            prop.SetValue(settings, prop.PropertyType == typeof(int) ? int.Parse(env) : env);
        }

        return settings;
    }

    /// <summary>Walks up from the test bin folder to the folder holding MadeeAutomation.sln.</summary>
    public static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MadeeAutomation.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find MadeeAutomation.sln above " + AppContext.BaseDirectory);
    }
}
