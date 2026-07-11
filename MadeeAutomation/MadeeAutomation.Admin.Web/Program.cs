using MadeeAutomation.AI;
using MadeeAutomation.Application;
using MadeeAutomation.Connectors;
using MadeeAutomation.Infrastructure;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: "Logs/adminweb-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting MadeeAutomation.Admin.Web");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.Services.AddControllersWithViews();

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddApplication();
    builder.Services.AddConnectors();
    builder.Services.AddClaudeAI();

    var app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
    }

    app.UseSerilogRequestLogging();

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthorization();

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "MadeeAutomation.Admin.Web terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}