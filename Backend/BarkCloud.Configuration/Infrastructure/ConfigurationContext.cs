using BarkCloud.Configuration.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Configuration.Infrastructure;

public class ConfigurationContext : DbContext
{
    public ConfigurationContext(DbContextOptions<ConfigurationContext> options) : base(options) { }

    public DbSet<SettingRevision> SettingsHistory => Set<SettingRevision>();

    public DbSet<ReservedName> ReservedNames => Set<ReservedName>();

    public DbSet<StorageProfile> StorageProfiles => Set<StorageProfile>();

    public DbSet<StorageProfileRevision> StorageProfileRevisions => Set<StorageProfileRevision>();

    public DbSet<SettingRow> Settings(SettingsScope scope) => Set<SettingRow>(scope.EntityName);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var scope in SettingsScopes.All)
        {
            modelBuilder.SharedTypeEntity<SettingRow>(scope.EntityName, entity =>
            {
                entity.ToTable(scope.TableName);
                entity.HasKey(row => row.Key);
                entity.Property(row => row.Key).HasColumnType("text");
                entity.Property(row => row.Value).HasColumnType("text").IsRequired();
                entity.Property(row => row.EditedBy).HasColumnType("text").IsRequired();
                entity.Property(row => row.EditedAt).HasColumnType("timestamp with time zone");
            });
        }

        modelBuilder.Entity<SettingRevision>(entity =>
        {
            entity.ToTable("SettingsHistory");
            entity.HasKey(revision => revision.Id);
            entity.Property(revision => revision.SettingsTable).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.Key).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.PreviousValue).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.NewValue).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.ChangedAt).HasColumnType("timestamp with time zone");
            entity.Property(revision => revision.ChangedBy).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.ChangedFrom).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.ChangeKind).HasColumnType("text").IsRequired();
            entity.HasIndex(revision => new { revision.SettingsTable, revision.Key, revision.ChangedAt, revision.Id })
                .IsDescending(false, false, true, true);
            entity.HasOne<SettingRevision>()
                .WithMany()
                .HasForeignKey(revision => revision.SourceRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReservedName>(entity =>
        {
            entity.ToTable("ReservedNames");
            entity.HasKey(item => item.Name);
            entity.Property(item => item.Name).HasColumnType("text");
        });

        modelBuilder.Entity<StorageProfile>(entity =>
        {
            entity.ToTable("StorageProfiles");
            entity.HasKey(profile => profile.ProfileId);
            entity.Property(profile => profile.ProfileId).HasColumnType("text");
            entity.Property(profile => profile.Role).HasColumnType("text").IsRequired();
            entity.Property(profile => profile.ServiceUrl).HasColumnType("text").IsRequired();
            entity.Property(profile => profile.AccessKey).HasColumnType("text").IsRequired();
            entity.Property(profile => profile.SecretKey).HasColumnType("text").IsRequired();
            entity.Property(profile => profile.BucketName).HasColumnType("text").IsRequired();
            entity.Property(profile => profile.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(profile => profile.CreatedBy).HasColumnType("text").IsRequired();
            entity.Property(profile => profile.CreatedFrom).HasColumnType("text").IsRequired();
            entity.Property(profile => profile.EditedAt).HasColumnType("timestamp with time zone");
            entity.Property(profile => profile.EditedBy).HasColumnType("text").IsRequired();
            entity.Property(profile => profile.EditedFrom).HasColumnType("text").IsRequired();
            entity.HasIndex(profile => new { profile.Role, profile.Version }).IsUnique();
            entity.HasIndex(profile => new { profile.Role, profile.IsActive });
        });

        modelBuilder.Entity<StorageProfileRevision>(entity =>
        {
            entity.ToTable("StorageProfileRevisions");
            entity.HasKey(revision => revision.Id);
            entity.Property(revision => revision.ProfileId).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.PreviousValue).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.NewValue).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.ChangedAt).HasColumnType("timestamp with time zone");
            entity.Property(revision => revision.ChangedBy).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.ChangedFrom).HasColumnType("text").IsRequired();
            entity.Property(revision => revision.ChangeKind).HasColumnType("text").IsRequired();
            entity.HasIndex(revision => new { revision.ProfileId, revision.ChangedAt, revision.Id })
                .IsDescending(false, true, true);
            entity.HasOne<StorageProfile>()
                .WithMany()
                .HasForeignKey(revision => revision.ProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StorageProfileRevision>()
                .WithMany()
                .HasForeignKey(revision => revision.SourceRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
