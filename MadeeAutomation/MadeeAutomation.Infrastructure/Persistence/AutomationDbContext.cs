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
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<OpeningHours> OpeningHours => Set<OpeningHours>();
    public DbSet<ClosedDate> ClosedDates => Set<ClosedDate>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

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
            entity.Ignore(c => c.EffectiveFallbackReply);
            entity.Property(c => c.FallbackReplyText).HasMaxLength(1000);
            entity.Property(c => c.StaffAlertEmail).HasMaxLength(256);
            entity.Property(c => c.StaffAlertLineUserId).HasMaxLength(100);
        });

        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.ToTable("AdminUsers");
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Property(u => u.Email).HasMaxLength(256).IsRequired();
            entity.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(u => u.PasswordHash).HasMaxLength(500).IsRequired();
            entity.Property(u => u.SecurityStamp).HasMaxLength(64).IsRequired();
            entity.Property(u => u.TimeZoneId).HasMaxLength(64).IsRequired();
            entity.HasIndex(u => u.ClientConfigId);
            entity.Ignore(u => u.IsClientUser);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasIndex(o => new { o.ClientConfigId, o.OrderNumber }).IsUnique();
            entity.HasIndex(o => new { o.ClientConfigId, o.OrderDate });
            entity.HasIndex(o => new { o.ClientConfigId, o.CreatedAtUtc });
            entity.Property(o => o.Reference).HasMaxLength(100).IsRequired();
            entity.Property(o => o.PosOrderId).HasMaxLength(100);
            entity.Property(o => o.Channel).HasMaxLength(20).IsRequired();
            entity.Property(o => o.CustomerId).HasMaxLength(200);
            entity.Property(o => o.CustomerName).HasMaxLength(200);
            entity.Property(o => o.Currency).HasMaxLength(3);
            entity.Property(o => o.Total).HasPrecision(18, 2);
            entity.Property(o => o.RequestedForText).HasMaxLength(200);
            entity.Property(o => o.CustomerNote).HasMaxLength(1000);
            entity.Property(o => o.StatusChangedBy).HasMaxLength(200);
            entity.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems");
            entity.HasIndex(i => i.MenuItemId);
            entity.Property(i => i.Name).HasMaxLength(200).IsRequired();
            entity.Property(i => i.UnitPrice).HasPrecision(18, 2);
            entity.Property(i => i.LineTotal).HasPrecision(18, 2);
        });

        modelBuilder.Entity<MenuItem>(entity =>
        {
            entity.ToTable("MenuItems");
            entity.HasIndex(m => m.ClientConfigId);
            entity.Property(m => m.Name).HasMaxLength(200).IsRequired();
            entity.Property(m => m.Category).HasMaxLength(100);
            entity.Property(m => m.Aliases).HasMaxLength(500);
            entity.Property(m => m.Price).HasPrecision(18, 2);
            entity.Ignore(m => m.AliasList);
        });

        modelBuilder.Entity<OpeningHours>(entity =>
        {
            entity.ToTable("OpeningHours");
            entity.HasIndex(h => h.ClientConfigId);
        });

        modelBuilder.Entity<ClosedDate>(entity =>
        {
            entity.ToTable("ClosedDates");
            entity.HasIndex(c => new { c.ClientConfigId, c.Date }).IsUnique();
            entity.Property(c => c.Reason).HasMaxLength(200);
        });

        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.ToTable("PasswordResetTokens");
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
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
