using MadeeAutomation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MadeeAutomation.Infrastructure.Persistence;

/// <summary>
/// Schema is managed by the SQL scripts in /database (not EF migrations) - same approach as Merkai.
/// If you add a property here, add the column in a new numbered script too.
/// </summary>
public class AutomationDbContext : DbContext
{
    public AutomationDbContext(DbContextOptions<AutomationDbContext> options) : base(options) { }

    public DbSet<ClientConfig> ClientConfigs => Set<ClientConfig>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<ExecutionLog> ExecutionLogs => Set<ExecutionLog>();
    public DbSet<InboundEvent> InboundEvents => Set<InboundEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ClientConfig>(entity =>
        {
            entity.ToTable("ClientConfigs");
            entity.HasIndex(c => c.ClientCode).IsUnique();
            entity.Property(c => c.ClientName).HasMaxLength(200).IsRequired();
            entity.Property(c => c.ClientCode).HasMaxLength(50).IsRequired();
            entity.Ignore(c => c.CanRunWorkflows);
        });

        modelBuilder.Entity<WorkflowDefinition>(entity =>
        {
            entity.ToTable("WorkflowDefinitions");
            entity.HasOne(w => w.ClientConfig)
                  .WithMany(c => c.Workflows)
                  .HasForeignKey(w => w.ClientConfigId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Property(w => w.HandlerKey).HasMaxLength(100).IsRequired();
            entity.Property(w => w.Name).HasMaxLength(200).IsRequired();
            entity.Property(w => w.Channel).HasMaxLength(20);
        });

        modelBuilder.Entity<ExecutionLog>(entity =>
        {
            entity.ToTable("ExecutionLogs");
            entity.HasOne(e => e.WorkflowDefinition)
                  .WithMany(w => w.ExecutionLogs)
                  .HasForeignKey(e => e.WorkflowDefinitionId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.Channel).HasMaxLength(20);
            entity.Property(e => e.CustomerId).HasMaxLength(100);

            // Index for the Admin dashboard's "recent executions per client" query.
            entity.HasIndex(e => new { e.ClientConfigId, e.StartedAtUtc });
        });

        modelBuilder.Entity<InboundEvent>(entity =>
        {
            entity.ToTable("InboundEvents");
            entity.Property(e => e.Channel).HasMaxLength(20).IsRequired();
            entity.Property(e => e.ExternalEventId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.CustomerId).HasMaxLength(100);
            entity.Property(e => e.ReplyToken).HasMaxLength(200);
            entity.Property(e => e.LastError).HasMaxLength(2000);

            // The duplicate guard: a re-delivered event hits this index and is ignored.
            entity.HasIndex(e => new { e.ClientConfigId, e.Channel, e.ExternalEventId }).IsUnique();
            entity.HasIndex(e => new { e.Status, e.ReceivedAtUtc });
        });

        // Every DateTime in this model is UTC. SQL Server hands datetime2 back with Kind=Unspecified,
        // so mark it Utc on the way out - views can then convert to the client's time zone safely.
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var nullableUtcConverter = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime)) property.SetValueConverter(utcConverter);
                else if (property.ClrType == typeof(DateTime?)) property.SetValueConverter(nullableUtcConverter);
            }
        }
    }
}
