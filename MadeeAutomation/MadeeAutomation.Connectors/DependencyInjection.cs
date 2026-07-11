using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MadeeAutomation.Connectors;

public static class DependencyInjection
{
    public static IServiceCollection AddConnectors(this IServiceCollection services)
    {
        services.AddHttpClient("MerkaiApi");
        services.AddHttpClient("PosFirstApi");
        services.AddHttpClient("WhatsAppApi");
        services.AddHttpClient("LineApi");
        services.AddHttpClient("MadeeAutomationExternal");

        services.AddScoped<IMerkaiConnector, MerkaiApiConnector>();
        services.AddScoped<IPosFirstConnector, PosFirstApiConnector>();
        services.AddScoped<IMessagingConnector, MessagingConnector>();

        return services;
    }
}
