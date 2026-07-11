using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Scrutor;

namespace MadeeAutomation.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Auto-registers every IHandler and IAction implementation in this assembly,
    /// the same Scrutor scan-and-register pattern already used in Merkai CRM.
    /// Adding a new vertical pack handler = drop the class in, no manual DI wiring.
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

        return services;
    }
}
