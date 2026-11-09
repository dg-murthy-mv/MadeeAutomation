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
        services.AddHttpClient("MessengerApi");
        services.AddHttpClient("MadeeAutomationExternal");

        services.AddScoped<IMerkaiConnector, MerkaiApiConnector>();
        services.AddScoped<IPosFirstConnector, PosFirstApiConnector>();
        services.AddScoped<IMessagingConnector, MessagingConnector>();
        services.AddScoped<IStaffAlertService, StaffAlertService>();

        return services;
    }

    /// <summary>Only what Admin.Web needs: the "Check LINE credentials" button.</summary>
    public static IServiceCollection AddLineCredentialCheck(this IServiceCollection services)
    {
        services.AddHttpClient("LineApi");
        services.AddScoped<LineCredentialChecker>();
        return services;
    }
}
