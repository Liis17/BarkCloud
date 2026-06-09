using BarkCloud.Users.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Users.Persistence.Contexts;

public class UsersContext : DbContext
{
    public UsersContext(DbContextOptions<UsersContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }

    public DbSet<UserContact> UserContacts { get; set; }

    public DbSet<UserDevice> UserDevices { get; set; }

    public DbSet<UserPrivacy> UserPrivacies { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasOne(u => u.Contact)
            .WithOne(p => p.User)
            .HasForeignKey<UserContact>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<User>()
            .HasOne(u => u.Privacy)
            .WithOne(p => p.User)
            .HasForeignKey<UserPrivacy>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<User>()
            .Property(u => u.StorageLimitGb)
            .HasDefaultValue(0);

        // Настройка связей для UserDevice
        modelBuilder.Entity<UserDevice>()
            .HasOne(ud => ud.User)
            .WithMany()
            .HasForeignKey(ud => ud.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        base.OnModelCreating(modelBuilder);
    }
}
