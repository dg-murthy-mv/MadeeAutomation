using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MadeeAutomation.AI;

public static class DependencyInjection
{
    public static IServiceCollection AddClaudeAI(this IServiceCollection services)
    {
        // ClaudeService applies its own per-attempt timeout, so the HttpClient one is just a backstop.
        services.AddHttpClient("ClaudeApi", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddScoped<IClaudeService, ClaudeService>();
        return services;
    }
}
