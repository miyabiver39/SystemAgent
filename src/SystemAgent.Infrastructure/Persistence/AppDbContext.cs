using Microsoft.EntityFrameworkCore;
using SystemAgent.Infrastructure.Persistence.Entities;

namespace SystemAgent.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<NodeEntity> Nodes => Set<NodeEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();
    public DbSet<ClusterSettingEntity> ClusterSettings => Set<ClusterSettingEntity>();
    public DbSet<JoinTokenEntity> JoinTokens => Set<JoinTokenEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NodeEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.HostName).IsUnique();
            e.Property(x => x.HostName).HasMaxLength(253);
            e.Property(x => x.IpAddress).HasMaxLength(253);
        });

        modelBuilder.Entity<ClusterSettingEntity>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });

        modelBuilder.Entity<JoinTokenEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SecretHash).IsUnique();
            e.Property(x => x.SecretHash).HasMaxLength(64);
        });

        modelBuilder.Entity<UserEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserName).IsUnique();
        });

        modelBuilder.Entity<AuditLogEntity>(e =>
        {
            e.HasKey(x => x.Id);
        });
    }
}
