using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Application.Inbound;
using MadeeAutomation.Application.VerticalPacks.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Scrutor;

namespace MadeeAutomation.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Auto-registers every IHandler and IAction implementation in this assembly,
    /// the same Scrutor scan-and-register pattern used in Merkai CRM.
    /// Adding a new vertical pack handler = drop the class in (and add it to HandlerCatalog).
    /// Used by WebApi only - Admin.Web does not run workflows.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.Scan(scan => scan
            .FromAssemblyOf<WorkflowEngine>()
            .AddClasses(classes => classes.AssignableTo<IHandler>())
                .AsImplementedInterfaces()
                .WithScopedLifetime()
            .AddClasses(classes => classes.AssignableTo<IAction>())
                .AsImplementedInterfaces()
                .WithScopedLifetime());

        services.AddScoped<WorkflowEngine>();
        services.AddScoped<MessageAssistant>();
        services.AddScoped<BusinessProfileLoader>();
        services.AddScoped<OrderDesk>();
        services.AddScoped<MadeeAutomation.Application.Scheduling.ScheduleRunner>();
        services.AddScoped<CustomerMessageServices>();
        services.AddScoped<OrderCustomerNotifier>();

        // Appointments (beauty & wellness)
        services.AddScoped<MadeeAutomation.Application.VerticalPacks.Beauty.BookingDesk>();
        services.AddScoped<MadeeAutomation.Application.VerticalPacks.Beauty.BookingAssistant>();
        services.AddScoped<MadeeAutomation.Application.VerticalPacks.Beauty.AppointmentServices>();
        services.AddScoped<MadeeAutomation.Application.VerticalPacks.Beauty.BookingCustomerNotifier>();
        services.AddScoped<MadeeAutomation.Application.VerticalPacks.Beauty.BookingReminderRunner>();
        services.AddSingleton(TimeProvider.System);   // swap in tests to freeze the clock

        // Reports (the monthly email is sent from WebApi)
        services.AddScoped<MadeeAutomation.Application.Reports.ReportBuilder>();
        services.AddScoped<MadeeAutomation.Application.Reports.AdminReportBuilder>();
        services.AddScoped<MadeeAutomation.Application.Reports.MonthlyReportRunner>();

        // Webhook -> InboundEvents -> background worker pipeline
        services.AddSingleton<InboundSignal>();
        services.AddScoped<InboundEventIngestor>();
        services.AddScoped<InboundEventProcessor>();

        return services;
    }
}
