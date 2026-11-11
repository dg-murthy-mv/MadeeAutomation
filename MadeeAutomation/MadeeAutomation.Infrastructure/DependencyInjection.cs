using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Infrastructure.Email;
using MadeeAutomation.Infrastructure.Persistence;
using MadeeAutomation.Infrastructure.Persistence.Repositories;
using MadeeAutomation.Infrastructure.Security;
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
        services.AddScoped<IInboundEventRepository, InboundEventRepository>();

        // One class serves both: the engine's client lookup and the Admin client screens.
        services.AddScoped<ClientRepository>();
        services.AddScoped<IClientRepository>(sp => sp.GetRequiredService<ClientRepository>());
        services.AddScoped<ICurrentClientService>(sp => sp.GetRequiredService<ClientRepository>());

        services.AddScoped<IClientSettingsService, ClientSettingsService>();
        services.AddScoped<IAdminUserRepository, AdminUserRepository>();
        services.AddScoped<IBusinessProfileRepository, BusinessProfileRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IConversationRepository, ConversationRepository>();
        // Email: Resend when Resend:ApiKey is set; otherwise the pickup folder (tests) or SMTP.
        services.AddSingleton<SmtpEmailSender>();
        services.AddSingleton<IEmailSender, ResendEmailSender>();

        return services;
    }
}
