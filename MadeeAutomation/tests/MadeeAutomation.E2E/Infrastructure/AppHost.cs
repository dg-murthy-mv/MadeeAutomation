using System.Diagnostics;

namespace MadeeAutomation.E2E.Infrastructure;

/// <summary>
/// Starts MadeeAutomation.WebApi and MadeeAutomation.Admin.Web from their BUILT dlls (build the
/// solution first), pointed at the E2E database and the fake server. Output goes to
/// TestResults/webapi.out.log and TestResults/adminweb.out.log - send those when a test fails.
///
/// ASPNETCORE_ENVIRONMENT=E2E means your user-secrets (real Claude key, real WhatsApp numbers)
/// are NOT loaded - the apps only see the settings below.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly List<(Process process, StreamWriter log)> _running = new();

    public static string LogFolder => Path.Combine(E2ESettings.SolutionRoot(), "tests", "MadeeAutomation.E2E", "TestResults");

    public async Task StartAsync(E2ESettings settings)
    {
        Directory.CreateDirectory(LogFolder);
        Mailbox.Clear();
        var root = E2ESettings.SolutionRoot();

        var common = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E",
            ["ConnectionStrings__AutomationDb"] = settings.AppConnectionString,
            ["Security__SettingsEncryptionKey"] = E2ESettings.EncryptionKey,
            ["InternalApi__Key"] = E2ESettings.InternalApiKey,
            ["Email__PickupDirectory"] = Mailbox.Folder,
            ["Email__FromAddress"] = "noreply@madeeautomation.test",
        };

        var webApi = new Dictionary<string, string>(common)
        {
            ["ASPNETCORE_URLS"] = settings.WebApiUrl,
            ["Anthropic__ApiKey"] = "e2e-fake-key",
            ["Anthropic__BaseUrl"] = settings.FakeServerUrl + "/",
            ["Anthropic__TimeoutSeconds"] = "20",
            ["Anthropic__MaxRetries"] = "0",
            ["Connectors__Line__ApiBaseUrl"] = settings.FakeServerUrl + "/",
            ["Connectors__WhatsApp__PhoneNumberId"] = "",
            ["Connectors__WhatsApp__AccessToken"] = "",
            ["Connectors__WhatsApp__InternalNotifyTo"] = "",
            ["Webhooks__RequireLineSignature"] = "true",
            ["Worker__PollSeconds"] = "1",
        };

        var admin = new Dictionary<string, string>(common)
        {
            ["ASPNETCORE_URLS"] = settings.AdminUrl,
            ["WebApi__PublicBaseUrl"] = settings.WebApiUrl,
            ["WebApi__InternalBaseUrl"] = settings.WebApiUrl,
            ["Connectors__Line__ApiBaseUrl"] = settings.FakeServerUrl + "/",
        };

        Start(root, "MadeeAutomation.WebApi", settings.Configuration, webApi, "webapi.out.log");
        Start(root, "MadeeAutomation.Admin.Web", settings.Configuration, admin, "adminweb.out.log");

        await WaitHealthyAsync(settings.WebApiUrl + "/health", "WebApi", settings.StartupTimeoutSeconds);
        await WaitHealthyAsync(settings.AdminUrl + "/health", "Admin.Web", settings.StartupTimeoutSeconds);
    }

    private void Start(string root, string project, string configuration, Dictionary<string, string> env, string logName)
    {
        var projectDir = Path.Combine(root, project);
        var dll = Path.Combine(projectDir, "bin", configuration, "net8.0", project + ".dll");
        if (!File.Exists(dll))
            throw new InvalidOperationException($"{dll} not found. Build the whole solution ({configuration}) before running the E2E tests.");

        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = projectDir,       // content root -> appsettings.json, wwwroot
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var (k, v) in env) psi.Environment[k] = v;

        var log = new StreamWriter(Path.Combine(LogFolder, logName), append: false) { AutoFlush = true };
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine("ERR " + e.Data); };

        if (!process.Start()) throw new InvalidOperationException("Could not start " + project);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _running.Add((process, log));
    }

    private async Task WaitHealthyAsync(string url, string name, int timeoutSeconds)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (_running.Any(r => r.process.HasExited))
                throw new InvalidOperationException($"{name} exited during start-up - see {LogFolder}");
            try
            {
                var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        throw new TimeoutException($"{name} did not answer {url} within {timeoutSeconds}s - see {LogFolder}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (process, log) in _running)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception) { /* already gone */ }
            finally
            {
                process.Dispose();
                lock (log) log.Dispose();
            }
        }
        _running.Clear();
    }
}
