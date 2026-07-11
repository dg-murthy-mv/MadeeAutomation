using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MadeeAutomation.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AutomationDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("AutomationDb")));

        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IExecutionLogRepository, ExecutionLogRepository>();
        services.AddScoped<ICurrentClientService, CurrentClientService>();

        return services;
    }
}
