using System.Security.Claims;
using MadeeAutomation.Admin.Web.Services;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Connectors;
using MadeeAutomation.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Authorization;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
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

    // Every page needs a signed-in user unless it says [AllowAnonymous] (login, forgot/reset password, setup, errors).
    builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.Add(new AuthorizeFilter());
        // A client's portal user can only open pages marked [ClientPortal] - everything else answers "Not allowed".
        options.Filters.Add<MadeeAutomation.Admin.Web.Security.ClientUserAccessFilter>();
        options.Filters.Add<MustChangePasswordFilter>();
    });

    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "MadeeAutomation.Admin";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/Account/Login";
            options.LogoutPath = "/Account/Logout";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;

            // Sign the browser out when the account was deactivated or its password changed elsewhere.
            options.Events.OnValidatePrincipal = async context =>
            {
                var principal = context.Principal;
                var idText = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                var stamp = principal?.FindFirstValue(AdminAuthService.StampClaim);
                var users = context.HttpContext.RequestServices.GetRequiredService<IAdminUserRepository>();
                var user = Guid.TryParse(idText, out var id) ? await users.GetByIdAsync(id) : null;

                // The cookie must still describe the account: same password stamp, and the same client (or none).
                var cookieClient = MadeeAutomation.Admin.Web.Security.ClientAccess.ClientId(principal!);
                if (user is null || !user.IsActive || user.SecurityStamp != stamp || user.ClientConfigId != cookieClient)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
        });

    // Admin.Web manages data (clients, credentials, workflows, logs, admins). It does not run workflows,
    // so it needs no Claude key - only the database, Security:SettingsEncryptionKey (same as WebApi)
    // and InternalApi:Key (same as WebApi) for the workflow Test button.
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddLineCredentialCheck();
    builder.Services.AddSingleton<AdminAuthService>();
    builder.Services.AddScoped<IApiService, ApiService>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<PortalNav>();
    builder.Services.AddScoped<MadeeAutomation.Application.VerticalPacks.Beauty.BookingDesk>();
    builder.Services.AddScoped<MadeeAutomation.Application.Reports.ReportBuilder>();
    builder.Services.AddScoped<MadeeAutomation.Application.Reports.AdminReportBuilder>();
    builder.Services.AddHttpClient(ApiService.ClientName, client =>
    {
        var baseUrl = builder.Configuration["WebApi:InternalBaseUrl"] is { Length: > 0 } b ? b : "https://localhost:58715/";
        client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        client.Timeout = TimeSpan.FromSeconds(90);   // the test run waits for the AI
    });

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }
    app.UseStatusCodePagesWithReExecute("/Error/{0}");

    app.UseSerilogRequestLogging();

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}");

    app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "MadeeAutomation.Admin.Web terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
