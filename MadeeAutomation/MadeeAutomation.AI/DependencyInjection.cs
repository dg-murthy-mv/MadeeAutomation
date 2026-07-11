using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MadeeAutomation.AI;

public static class DependencyInjection
{
    public static IServiceCollection AddClaudeAI(this IServiceCollection services)
    {
        services.AddHttpClient("ClaudeApi");
        services.AddScoped<IClaudeService, ClaudeService>();
        return services;
    }
}
