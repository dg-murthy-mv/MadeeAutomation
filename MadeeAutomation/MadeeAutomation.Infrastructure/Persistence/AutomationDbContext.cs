using MadeeAutomation.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence;

public class AutomationDbContext : DbContext
{
    public AutomationDbContext(DbContextOptions<AutomationDbContext> options) : base(options) { }

    public DbSet<ClientConfig> ClientConfigs => Set<ClientConfig>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<ExecutionLog> ExecutionLogs => Set<ExecutionLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ClientConfig>(entity =>
        {
            entity.HasIndex(c => c.ClientCode).IsUnique();
            entity.Property(c => c.ClientName).HasMaxLength(200).IsRequired();
            entity.Property(c => c.ClientCode).HasMaxLength(50).IsRequired();
        });

        modelBuilder.Entity<WorkflowDefinition>(entity =>
        {
            entity.HasOne(w => w.ClientConfig)
                  .WithMany(c => c.Workflows)
                  .HasForeignKey(w => w.ClientConfigId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Property(w => w.HandlerKey).HasMaxLength(100).IsRequired();
            entity.Property(w => w.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<ExecutionLog>(entity =>
        {
            entity.HasOne(e => e.WorkflowDefinition)
                  .WithMany(w => w.ExecutionLogs)
                  .HasForeignKey(e => e.WorkflowDefinitionId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Index for the Admin dashboard's "recent executions per client" query.
            entity.HasIndex(e => new { e.ClientConfigId, e.StartedAtUtc });
        });
    }
}
