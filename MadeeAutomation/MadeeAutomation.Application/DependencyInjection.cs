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
        services.AddSingleton(TimeProvider.System);   // swap in tests to freeze the clock

        // Webhook -> InboundEvents -> background worker pipeline
        services.AddSingleton<InboundSignal>();
        services.AddScoped<InboundEventIngestor>();
        services.AddScoped<InboundEventProcessor>();

        return services;
    }
}
