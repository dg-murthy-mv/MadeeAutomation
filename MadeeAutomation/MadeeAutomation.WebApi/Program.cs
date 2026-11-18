using MadeeAutomation.AI;
using MadeeAutomation.Application;
using MadeeAutomation.Connectors;
using MadeeAutomation.Infrastructure;
using MadeeAutomation.WebApi.Background;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

// On Azure App Service the app folder can be read-only, so log files go to /home/LogFiles/Application
// (kept between deployments, readable in Kudu / "Log stream"). On your PC they stay in the Logs folder.
static string LogFolder() =>
    Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is { Length: > 0 } && Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
        ? Path.Combine(home, "LogFiles", "Application")
        : "Logs";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    // HttpClient logs full request URLs at Information - keep them out of the files.
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(LogFolder(), "webapi-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting MadeeAutomation.WebApi");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddApplication();
    builder.Services.AddConnectors();
    builder.Services.AddClaudeAI();

    // Runs the customer messages the webhooks store (see WebhooksController).
    builder.Services.AddHostedService<InboundEventWorker>();
    builder.Services.AddHostedService<SchedulerWorker>();
    builder.Services.AddHostedService<BookingReminderWorker>();
    builder.Services.AddHostedService<MonthlyReportWorker>();

    // Azure App Service sits in front of the app and passes the real https / client address in X-Forwarded-* headers.
    // Without this, the app thinks every request is plain http (wrong links, https redirect loops on Linux).
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    var app = builder.Build();

    app.UseForwardedHeaders();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseSerilogRequestLogging();

    app.UseHttpsRedirection();
    app.UseAuthorization();
    app.MapControllers();

    // Used by the E2E tests (and any uptime monitor) to know the app is up.
    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "MadeeAutomation.WebApi terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
