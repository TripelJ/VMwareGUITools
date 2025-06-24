using Microsoft.EntityFrameworkCore;
using VMwareGUITools.Core.Models;

namespace VMwareGUITools.Data;

/// <summary>
/// Entity Framework DbContext for the VMware GUI Tools application
/// </summary>
public class VMwareDbContext : DbContext
{
    public VMwareDbContext(DbContextOptions<VMwareDbContext> options) : base(options)
    {
    }

    // DbSets for all entities
    public DbSet<VCenter> VCenters { get; set; }
    public DbSet<Cluster> Clusters { get; set; }
    public DbSet<Host> Hosts { get; set; }
    public DbSet<Datastore> Datastores { get; set; }
    public DbSet<HostProfile> HostProfiles { get; set; }
    public DbSet<CheckCategory> CheckCategories { get; set; }
    public DbSet<CheckDefinition> CheckDefinitions { get; set; }
    public DbSet<CheckResult> CheckResults { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure VCenter entity
        modelBuilder.Entity<VCenter>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Url).IsRequired().HasMaxLength(255);
            entity.Property(e => e.EncryptedCredentials).IsRequired().HasMaxLength(500);
            entity.HasIndex(e => e.Url).IsUnique();
            entity.HasIndex(e => e.Name);
            
            // Ignore properties that are not persisted to database
            entity.Ignore(e => e.IsCurrentlyConnected);
            entity.Ignore(e => e.LastSuccessfulConnection);
        });

        // Configure Cluster entity
        modelBuilder.Entity<Cluster>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.MoId).IsRequired().HasMaxLength(50);
            
            entity.HasOne(e => e.VCenter)
                .WithMany(v => v.Clusters)
                .HasForeignKey(e => e.VCenterId)
                .OnDelete(DeleteBehavior.Cascade);
                
            entity.HasOne(e => e.Profile)
                .WithMany(p => p.Clusters)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.SetNull);
                
            entity.HasIndex(e => new { e.VCenterId, e.MoId }).IsUnique();
        });

        // Configure Host entity
        modelBuilder.Entity<Host>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.IpAddress).IsRequired().HasMaxLength(45);
            entity.Property(e => e.MoId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.HostType).HasConversion<string>();
            
            entity.HasOne(e => e.Cluster)
                .WithMany(c => c.Hosts)
                .HasForeignKey(e => e.ClusterId)
                .OnDelete(DeleteBehavior.Cascade);
                
            entity.HasOne(e => e.VCenter)
                .WithMany(v => v.Hosts)
                .HasForeignKey(e => e.VCenterId)
                .OnDelete(DeleteBehavior.Cascade);
                
            entity.HasOne(e => e.Profile)
                .WithMany(p => p.Hosts)
                .HasForeignKey(e => e.ProfileId)
                .OnDelete(DeleteBehavior.SetNull);
                
            entity.HasIndex(e => new { e.ClusterId, e.MoId }).IsUnique();
            entity.HasIndex(e => e.IpAddress);
        });

        // Configure Datastore entity
        modelBuilder.Entity<Datastore>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.MoId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(20);
            
            entity.HasOne(e => e.VCenter)
                .WithMany()
                .HasForeignKey(e => e.VCenterId)
                .OnDelete(DeleteBehavior.Cascade);
                
            entity.HasIndex(e => new { e.VCenterId, e.MoId }).IsUnique();
            entity.HasIndex(e => e.Name);
        });

        // Configure HostProfile entity
        modelBuilder.Entity<HostProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Type).HasConversion<string>();
            entity.Property(e => e.CheckConfigs).IsRequired();
            
            entity.HasIndex(e => e.Name).IsUnique();
        });

        // Configure CheckCategory entity
        modelBuilder.Entity<CheckCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Type).HasConversion<string>();
            
            entity.HasIndex(e => e.Name).IsUnique();
        });

        // Configure CheckDefinition entity
        modelBuilder.Entity<CheckDefinition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ExecutionType).HasConversion<string>();
            entity.Property(e => e.DefaultSeverity).HasConversion<string>();
            entity.Property(e => e.ScriptPath).IsRequired();
            entity.Property(e => e.Parameters).IsRequired();
            entity.Property(e => e.Thresholds).IsRequired();
            
            entity.HasOne(e => e.Category)
                .WithMany(c => c.CheckDefinitions)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
                
            entity.HasIndex(e => new { e.CategoryId, e.Name }).IsUnique();
        });

        // Configure CheckResult entity
        modelBuilder.Entity<CheckResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Output).HasDefaultValue(string.Empty);
            entity.Property(e => e.Details).HasDefaultValue(string.Empty);
            entity.Property(e => e.ErrorMessage).HasDefaultValue(string.Empty);
            
            entity.HasOne(e => e.Host)
                .WithMany(h => h.CheckResults)
                .HasForeignKey(e => e.HostId)
                .OnDelete(DeleteBehavior.Cascade);
                
            entity.HasOne(e => e.CheckDefinition)
                .WithMany(c => c.CheckResults)
                .HasForeignKey(e => e.CheckDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
                
            entity.HasIndex(e => new { e.HostId, e.CheckDefinitionId, e.ExecutedAt });
            entity.HasIndex(e => e.ExecutedAt);
        });

        // Seed default data
        SeedDefaultData(modelBuilder);
    }

    private static void SeedDefaultData(ModelBuilder modelBuilder)
    {
        // Seed default check categories
        modelBuilder.Entity<CheckCategory>().HasData(
            new CheckCategory
            {
                Id = 1,
                Name = "Configuration",
                Description = "Configuration compliance checks",
                Type = CheckCategoryType.Configuration,
                SortOrder = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CheckCategory
            {
                Id = 2,
                Name = "Health",
                Description = "System health and availability checks",
                Type = CheckCategoryType.Health,
                SortOrder = 2,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CheckCategory
            {
                Id = 3,
                Name = "Security",
                Description = "Security compliance and vulnerability checks",
                Type = CheckCategoryType.Security,
                SortOrder = 3,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        );

        // Seed default host profiles
        modelBuilder.Entity<HostProfile>().HasData(
            new HostProfile
            {
                Id = 1,
                Name = "Standard ESXi Host",
                Description = "Default profile for standard ESXi hosts",
                Type = HostType.Standard,
                CheckConfigs = """
                [
                  {
                    "Category": "Configuration",
                    "CheckId": 1,
                    "Schedule": "daily",
                    "AlertOnFailure": true,
                    "AlertOnSuccess": false,
                    "Parameters": {}
                  },
                  {
                    "Category": "Health",
                    "CheckId": 2,
                    "Schedule": "hourly",
                    "AlertOnFailure": true,
                    "AlertOnSuccess": false,
                    "Parameters": {}
                  }
                ]
                """,
                IsDefault = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new HostProfile
            {
                Id = 2,
                Name = "vSAN Node",
                Description = "Profile for vSAN cluster nodes",
                Type = HostType.VsanNode,
                CheckConfigs = """
                [
                  {
                    "Category": "Configuration",
                    "CheckId": 1,
                    "Schedule": "daily",
                    "AlertOnFailure": true,
                    "AlertOnSuccess": false,
                    "Parameters": {}
                  },
                  {
                    "Category": "Health",
                    "CheckId": 2,
                    "Schedule": "hourly",
                    "AlertOnFailure": true,
                    "AlertOnSuccess": false,
                    "Parameters": {}
                  }
                ]
                """,
                IsDefault = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        );
    }
} 